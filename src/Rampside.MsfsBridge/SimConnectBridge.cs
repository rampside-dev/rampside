using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;
using Rampside.Core;
using Rampside.Core.Simulation;

namespace Rampside.MsfsBridge;

/// <summary>
/// Connects to MSFS via SimConnect, keeps <see cref="State"/> up to date and writes fuel, payload,
/// events and ground motion. Reconnects automatically when the sim is restarted.
/// </summary>
public sealed class SimConnectBridge : ISimBridge, IGroundVehicleSpawner
{
    private const string ClientName = "Rampside";
    private const double DefaultFuelPoundsPerGallon = 6.7;
    private const uint FirstClientEventId = 1000;
    private const uint FirstAiRequestId = 10_000;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private readonly object _simLock = new();
    private readonly object _stateLock = new();
    private readonly AutoResetEvent _messageSignal = new(false);
    private readonly HashSet<int> _registeredStations = new();
    private readonly Dictionary<string, uint> _eventIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ISimConnectExtension> _extensions = new();
    private readonly Dictionary<uint, string> _pendingSpawns = new();
    private readonly Dictionary<uint, uint> _spawnedObjects = new(); // spawn request id -> sim object id
    private readonly HashSet<uint> _cancelledSpawns = new();
    private readonly Dictionary<uint, VehicleAttachment> _attachments = new(); // spawn request id -> place at the aircraft
    private readonly Dictionary<uint, (VehicleDrive Drive, System.Diagnostics.Stopwatch Clock, bool RemoveAtEnd)> _drives = new();
    private readonly System.Diagnostics.Stopwatch _pitchClock = new();
    private PitchRamp? _pitchRamp;
    private GroundMotionData? _groundMotion; // written every frame until released
    private readonly System.Diagnostics.Stopwatch _groundMotionClock = new();
    private static readonly TimeSpan GroundMotionTimeout = TimeSpan.FromSeconds(0.5);
    private readonly Dictionary<SimObjectKind, SortedSet<string>> _catalogs = new();

    private SimConnect? _simConnect;
    private bool _connected;
    private bool _quitReceived;
    private uint _nextEventId = FirstClientEventId;
    private uint _nextAiRequestId = FirstAiRequestId;
    private double _fuelPoundsPerGallon = DefaultFuelPoundsPerGallon;
    private AircraftState _state = AircraftState.Unknown;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private enum Definition : uint
    {
        AircraftState = 1,
        FuelWrite = 2,
        GroundMotion = 3,
        InitPosition = 4,
        AircraftPose = 5,
        PitchWrite = 6,
        PayloadStationBase = 100,
    }

    private enum Request : uint
    {
        AircraftState = 1,
        AircraftPose = 3,
        // One request per catalog kind: CatalogBase + (int)SimObjectKind.
        CatalogBase = 100,
    }

    private enum Group : uint
    {
        // Used with GROUPID_IS_PRIORITY: value of SIMCONNECT_GROUP_PRIORITY_HIGHEST.
        HighestPriority = 1,
    }

    private enum ClientEvent : uint
    {
    }

    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<string>? Message;
    public event EventHandler<SimObjectCatalog>? CatalogReceived;

    public bool IsConnected
    {
        get { lock (_simLock) return _connected; }
    }

    public AircraftState State
    {
        get { lock (_stateLock) return _state; }
    }

    public int SpawnedVehicleCount
    {
        get { lock (_simLock) return _spawnedObjects.Count + _pendingSpawns.Count; }
    }

    /// <summary>Adds an extension (e.g. a PMDG SDK link) that sets up its own SimConnect data. Call before StartAsync.</summary>
    public void AddExtension(ISimConnectExtension extension)
    {
        lock (_simLock) _extensions.Add(extension);
    }

    /// <summary>Supplies <see cref="AircraftState.Stand"/>, e.g. a <see cref="StandMonitor"/>.</summary>
    public Func<StandInfo?>? StandSource { get; set; }

    /// <summary>Supplies <see cref="AircraftState.AirportIcao"/>, e.g. a <see cref="StandMonitor"/>.</summary>
    public Func<string?>? AirportSource { get; set; }

    /// <summary>Supplies <see cref="AircraftState.NearbyObjects"/>, e.g. a <see cref="NearbyObjectScanner"/>.</summary>
    public Func<IReadOnlyList<NearbyObject>>? NearbySource { get; set; }

    /// <summary>Supplies <see cref="AircraftState.AirportParkings"/>, e.g. from a <see cref="StandMonitor"/>.</summary>
    public Func<IReadOnlyList<ParkingSpot>>? ParkingSource { get; set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;
        _loop = Task.Factory.StartNew(() => Run(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return Task.CompletedTask;
    }

    public void SetFuel(IReadOnlyDictionary<FuelTankId, double> quantitiesKg)
    {
        var current = State.FuelTanks.ToDictionary(t => t.Id, t => t.QuantityKg);
        double Gallons(FuelTankId id)
        {
            var kg = quantitiesKg.TryGetValue(id, out var value) ? value : current.GetValueOrDefault(id);
            return Units.KilogramsToPounds(kg) / _fuelPoundsPerGallon;
        }

        var data = new FuelWriteData
        {
            LeftMainGallons = Gallons(FuelTankId.LeftMain),
            RightMainGallons = Gallons(FuelTankId.RightMain),
            CenterGallons = Gallons(FuelTankId.Center),
        };

        WithSim(sc => sc.SetDataOnSimObject(
            Definition.FuelWrite, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, data));
    }

    public void SetPayloadStation(int stationIndex, double weightKg)
    {
        WithSim(sc =>
        {
            var definition = (Definition)((uint)Definition.PayloadStationBase + (uint)stationIndex);
            if (_registeredStations.Add(stationIndex))
            {
                sc.AddToDataDefinition(
                    definition, $"PAYLOAD STATION WEIGHT:{stationIndex}", "pounds",
                    SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                sc.RegisterDataDefineStruct<SingleValue>(definition);
            }

            sc.SetDataOnSimObject(
                definition, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT,
                new SingleValue { Value = Units.KilogramsToPounds(weightKg) });
        });
    }

    public void TriggerEvent(string simEventName, uint data = 0)
    {
        WithSim(sc =>
        {
            if (!_eventIds.TryGetValue(simEventName, out var id))
            {
                id = _nextEventId++;
                sc.MapClientEventToSimEvent((ClientEvent)id, simEventName);
                _eventIds[simEventName] = id;
            }

            sc.TransmitClientEvent(
                SimConnect.SIMCONNECT_OBJECT_ID_USER, (ClientEvent)id, data,
                Group.HighestPriority, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
        });
        Message?.Invoke(this, $"Sim event {simEventName} ({data})");
    }

    public void SetGroundMotion(double longitudinalMps, double yawRateRadPerSec)
    {
        // Body axes: Z forward, Y up. Sign of the yaw rate still needs confirming in the sim.
        var data = new GroundMotionData
        {
            VelocityBodyX = 0,
            VelocityBodyZ = Units.MetersToFeet(longitudinalMps),
            RotationVelocityBodyY = yawRateRadPerSec,
        };

        WithSim(sc =>
        {
            _groundMotion = data;
            _groundMotionClock.Restart();
            sc.SetDataOnSimObject(Definition.GroundMotion, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, data);
        });
    }

    public void RequestCatalog(SimObjectKind kind)
    {
        WithSim(sc =>
        {
            _catalogs[kind] = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            sc.EnumerateSimObjectsAndLiveries((Request)((uint)Request.CatalogBase + (uint)kind), ToSimConnect(kind));
        });
    }

    private static SIMCONNECT_SIMOBJECT_TYPE ToSimConnect(SimObjectKind kind) => kind switch
    {
        SimObjectKind.All => SIMCONNECT_SIMOBJECT_TYPE.ALL,
        SimObjectKind.Aircraft => SIMCONNECT_SIMOBJECT_TYPE.AIRCRAFT,
        SimObjectKind.Helicopter => SIMCONNECT_SIMOBJECT_TYPE.HELICOPTER,
        SimObjectKind.Boat => SIMCONNECT_SIMOBJECT_TYPE.BOAT,
        SimObjectKind.Ground => SIMCONNECT_SIMOBJECT_TYPE.GROUND,
        SimObjectKind.HotAirBalloon => SIMCONNECT_SIMOBJECT_TYPE.HOT_AIR_BALLOON,
        SimObjectKind.Animal => SIMCONNECT_SIMOBJECT_TYPE.ANIMAL,
        SimObjectKind.UserAvatar => SIMCONNECT_SIMOBJECT_TYPE.USER_AVATAR,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public int? SpawnGroundVehicle(string title, double latitude, double longitude, double headingTrueDeg)
    {
        int? handle = null;
        WithSim(sc =>
        {
            var requestId = _nextAiRequestId++;
            sc.AICreateSimulatedObject_EX1(title, string.Empty, GroundPosition(latitude, longitude, headingTrueDeg), (Request)requestId);
            _pendingSpawns[requestId] = title;
            handle = (int)requestId;
        });
        return handle;
    }

    public void MoveGroundVehicle(int vehicle, double latitude, double longitude, double headingTrueDeg)
    {
        WithSim(sc =>
        {
            if (_spawnedObjects.TryGetValue((uint)vehicle, out var objectId))
            {
                sc.SetDataOnSimObject(
                    Definition.InitPosition, objectId, SIMCONNECT_DATA_SET_FLAG.DEFAULT,
                    GroundPosition(latitude, longitude, headingTrueDeg));
            }
        });
    }

    public void HoldPitch(PitchRamp? ramp)
    {
        WithSim(_ =>
        {
            _pitchRamp = ramp;
            _pitchClock.Restart();
        });
    }

    public void AttachGroundVehicle(int vehicle, VehicleAttachment? attachment)
    {
        WithSim(_ =>
        {
            var requestId = (uint)vehicle;
            if (attachment is { } place)
            {
                _attachments[requestId] = place;
                _drives.Remove(requestId);
            }
            else
            {
                _attachments.Remove(requestId);
            }
        });
    }

    public void DriveGroundVehicle(int vehicle, VehicleDrive drive, bool removeAtEnd)
    {
        WithSim(_ =>
        {
            var requestId = (uint)vehicle;
            _attachments.Remove(requestId);
            _drives[requestId] = (drive, System.Diagnostics.Stopwatch.StartNew(), removeAtEnd);
        });
    }

    public void RemoveGroundVehicle(int vehicle)
    {
        WithSim(sc =>
        {
            var requestId = (uint)vehicle;
            _attachments.Remove(requestId);
            _drives.Remove(requestId);
            if (_pendingSpawns.Remove(requestId))
            {
                // Not created yet: remove it as soon as the sim reports its object id.
                _cancelledSpawns.Add(requestId);
            }
            else if (_spawnedObjects.Remove(requestId, out var objectId))
            {
                sc.AIRemoveObject(objectId, (Request)_nextAiRequestId++);
            }
        });
    }

    public void RemoveSpawnedVehicles()
    {
        WithSim(sc =>
        {
            foreach (var objectId in _spawnedObjects.Values)
            {
                sc.AIRemoveObject(objectId, (Request)_nextAiRequestId++);
            }

            Message?.Invoke(this, $"Removed {_spawnedObjects.Count} vehicle(s)");
            _cancelledSpawns.UnionWith(_pendingSpawns.Keys);
            _spawnedObjects.Clear();
            _pendingSpawns.Clear();
            _attachments.Clear();
            _drives.Clear();
        });
    }

    private static SIMCONNECT_DATA_INITPOSITION GroundPosition(double latitude, double longitude, double headingTrueDeg) => new()
    {
        Latitude = latitude,
        Longitude = longitude,
        Altitude = 0,
        Pitch = 0,
        Bank = 0,
        Heading = headingTrueDeg,
        OnGround = 1,
        Airspeed = 0,
    };

    public void Reposition(double latitude, double longitude, double headingTrueDeg)
    {
        // OnGround = 1 puts the aircraft on the ground elevation there; the altitude is ignored.
        var position = new SIMCONNECT_DATA_INITPOSITION
        {
            Latitude = latitude,
            Longitude = longitude,
            Altitude = 0,
            Pitch = 0,
            Bank = 0,
            Heading = headingTrueDeg,
            OnGround = 1,
            Airspeed = 0,
        };

        WithSim(sc => sc.SetDataOnSimObject(
            Definition.InitPosition, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, position));
        Message?.Invoke(this, $"Repositioned to {latitude:F6}, {longitude:F6}, heading {headingTrueDeg:F0}° true");
    }

    public void ReleaseGroundMotion()
    {
        // Writing zero once leaves the aircraft at rest; afterwards the sim's own physics take over again.
        SetGroundMotion(0, 0);
        WithSim(_ => _groundMotion = null);
    }

    public async ValueTask DisposeAsync()
    {
        RemoveSpawnedVehicles();
        _cts?.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Close();
        _messageSignal.Dispose();
        _cts?.Dispose();
    }

    private void Run(CancellationToken token)
    {
        var waitHandles = new WaitHandle[] { _messageSignal, token.WaitHandle };
        while (!token.IsCancellationRequested)
        {
            if (!TryOpen())
            {
                token.WaitHandle.WaitOne(RetryInterval);
                continue;
            }

            while (!token.IsCancellationRequested && HasSession())
            {
                if (WaitHandle.WaitAny(waitHandles, TimeSpan.FromSeconds(1)) == 0)
                {
                    Pump();
                }
            }
        }
    }

    private bool HasSession()
    {
        lock (_simLock) return _simConnect is not null;
    }

    private bool TryOpen()
    {
        try
        {
            var sc = new SimConnect(ClientName, IntPtr.Zero, 0, _messageSignal, 0);
            sc.OnRecvOpen += OnRecvOpen;
            sc.OnRecvQuit += (_, _) => _quitReceived = true;
            sc.OnRecvException += OnRecvException;
            sc.OnRecvSimobjectData += OnRecvSimobjectData;
            sc.OnRecvEnumerateSimobjectAndLiveryList += OnRecvSimobjectCatalog;
            sc.OnRecvAssignedObjectId += OnRecvAssignedObjectId;
            sc.OnRecvClientData += (_, data) =>
            {
                foreach (var extension in _extensions)
                {
                    extension.OnClientData(data);
                }
            };
            lock (_simLock)
            {
                _quitReceived = false;
                _simConnect = sc;
            }

            return true;
        }
        catch (COMException)
        {
            // Sim not running yet.
            return false;
        }
    }

    private void Pump()
    {
        try
        {
            lock (_simLock) _simConnect?.ReceiveMessage();
        }
        catch (COMException ex)
        {
            Message?.Invoke(this, $"SimConnect error: {ex.Message}");
            Close();
            return;
        }

        // Close outside the SimConnect callback that reported the quit.
        if (_quitReceived)
        {
            Message?.Invoke(this, "Simulator closed");
            Close();
        }
    }

    private void OnRecvOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
    {
        RegisterDefinitions(sender);
        sender.RequestDataOnSimObject(
            Request.AircraftState, Definition.AircraftState, SimConnect.SIMCONNECT_OBJECT_ID_USER,
            SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 5, 0);

        // Every frame, so vehicles attached to the aircraft (the pushback tug) move with it smoothly.
        sender.RequestDataOnSimObject(
            Request.AircraftPose, Definition.AircraftPose, SimConnect.SIMCONNECT_OBJECT_ID_USER,
            SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);

        foreach (var extension in _extensions)
        {
            try
            {
                extension.OnOpen(sender);
            }
            catch (COMException ex)
            {
                Message?.Invoke(this, $"{extension.GetType().Name} setup failed: {ex.Message}");
            }
        }

        lock (_simLock) _connected = true;
        Message?.Invoke(this, $"Connected to {data.szApplicationName}");
        ConnectionChanged?.Invoke(this, true);
    }

    private void OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
    {
        Message?.Invoke(this, $"SimConnect exception {(SIMCONNECT_EXCEPTION)data.dwException} (send id {data.dwSendID}, index {data.dwIndex})");
    }

    private void OnRecvSimobjectCatalog(SimConnect sender, SIMCONNECT_RECV_ENUMERATE_SIMOBJECT_AND_LIVERY_LIST data)
    {
        var kind = (SimObjectKind)(data.dwRequestID - (uint)Request.CatalogBase);
        if (data.dwRequestID < (uint)Request.CatalogBase || !_catalogs.TryGetValue(kind, out var catalog))
        {
            return;
        }

        foreach (var entry in data.rgData.OfType<SIMCONNECT_ENUMERATE_SIMOBJECT_LIVERY>())
        {
            if (!string.IsNullOrWhiteSpace(entry.AircraftTitle))
            {
                catalog.Add(entry.AircraftTitle);
            }
        }

        // Large catalogs arrive in several packets.
        if (data.dwEntryNumber + 1 >= data.dwOutOf)
        {
            CatalogReceived?.Invoke(this, new SimObjectCatalog(kind, catalog.ToArray()));
        }
    }

    private void OnRecvAssignedObjectId(SimConnect sender, SIMCONNECT_RECV_ASSIGNED_OBJECT_ID data)
    {
        if (_cancelledSpawns.Remove(data.dwRequestID))
        {
            sender.AIRemoveObject(data.dwObjectID, (Request)_nextAiRequestId++);
        }
        else if (_pendingSpawns.Remove(data.dwRequestID, out var title))
        {
            _spawnedObjects[data.dwRequestID] = data.dwObjectID;
            Message?.Invoke(this, $"Spawned {title} (object {data.dwObjectID})");
        }
    }

    /// <summary>Writes the held pitch for this frame; runs once per sim frame.</summary>
    private void WritePitch(SimConnect sc)
    {
        if (_pitchRamp is not { } ramp)
        {
            return;
        }

        var elapsed = _pitchClock.Elapsed.TotalSeconds;
        if (ramp.ReleaseWhenDone && ramp.IsDone(elapsed))
        {
            _pitchRamp = null;
            return;
        }

        // Pitch rate and vertical speed are zeroed too: otherwise the gear springs build up a pitching motion
        // between frames, and the aircraft trembles.
        sc.SetDataOnSimObject(
            Definition.PitchWrite, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT,
            new PitchWriteData { PitchDeg = ramp.At(elapsed) });
    }

    /// <summary>Moves every driving vehicle along its path and removes those that arrived; runs once per sim frame.</summary>
    private void PlaceDrivenVehicles(SimConnect sc)
    {
        foreach (var (requestId, (drive, clock, removeAtEnd)) in _drives.ToArray())
        {
            var elapsed = clock.Elapsed.TotalSeconds;
            if (drive.IsDone(elapsed) && removeAtEnd)
            {
                _drives.Remove(requestId);
                if (_pendingSpawns.Remove(requestId))
                {
                    _cancelledSpawns.Add(requestId);
                }
                else if (_spawnedObjects.Remove(requestId, out var removed))
                {
                    sc.AIRemoveObject(removed, (Request)_nextAiRequestId++);
                }

                continue;
            }

            if (_spawnedObjects.TryGetValue(requestId, out var objectId))
            {
                var (lat, lon, heading) = drive.At(elapsed);
                sc.SetDataOnSimObject(Definition.InitPosition, objectId, SIMCONNECT_DATA_SET_FLAG.DEFAULT, GroundPosition(lat, lon, heading));
            }
        }
    }

    /// <summary>Places every attached vehicle at its spot next to the aircraft; runs once per sim frame.</summary>
    private void PlaceAttachedVehicles(SimConnect sc, AircraftPoseData aircraft)
    {
        foreach (var (requestId, attachment) in _attachments)
        {
            if (!_spawnedObjects.TryGetValue(requestId, out var objectId))
            {
                continue; // not created yet
            }

            // Every frame, also while the aircraft stands still: left alone, the sim's vehicle starts to roll.
            var place = attachment.Place(aircraft.Latitude, aircraft.Longitude, aircraft.HeadingTrueDeg);
            sc.SetDataOnSimObject(
                Definition.InitPosition, objectId, SIMCONNECT_DATA_SET_FLAG.DEFAULT,
                GroundPosition(place.Latitude, place.Longitude, place.HeadingTrueDeg));
        }
    }

    private void OnRecvSimobjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        if (data.dwRequestID == (uint)Request.AircraftPose)
        {
            if (_attachments.Count > 0)
            {
                PlaceAttachedVehicles(sender, (AircraftPoseData)data.dwData[0]);
            }

            if (_drives.Count > 0)
            {
                PlaceDrivenVehicles(sender);
            }

            WritePitch(sender);
            // Rampside sends a new motion every tick; if it stops (hung), stop moving the aircraft too.
            if (_groundMotion is { } motion && _groundMotionClock.Elapsed < GroundMotionTimeout)
            {
                // Every frame, not only on Rampside's ticks: smoother, and nothing builds up between ticks.
                sender.SetDataOnSimObject(Definition.GroundMotion, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, motion);
            }

            return;
        }

        if (data.dwRequestID != (uint)Request.AircraftState)
        {
            return;
        }

        var d = (AircraftStateData)data.dwData[0];
        if (d.FuelPoundsPerGallon > 0)
        {
            _fuelPoundsPerGallon = d.FuelPoundsPerGallon;
        }

        double Kg(double gallons) => Units.PoundsToKilograms(gallons * _fuelPoundsPerGallon);

        var state = new AircraftState
        {
            Title = d.Title ?? string.Empty,
            OnGround = d.OnGround != 0,
            GroundSpeedKnots = d.GroundSpeedKnots,
            HeadingTrueDeg = d.HeadingTrueDeg,
            Latitude = d.Latitude,
            Longitude = d.Longitude,
            ParkingBrakeSet = d.ParkingBrake != 0,
            EnginesRunning = d.Engine1 != 0 || d.Engine2 != 0 || d.Engine3 != 0 || d.Engine4 != 0,
            FuelTanks = new[]
            {
                new FuelTankState(FuelTankId.LeftMain, Kg(d.LeftMainCapacity), Kg(d.LeftMainQuantity)),
                new FuelTankState(FuelTankId.RightMain, Kg(d.RightMainCapacity), Kg(d.RightMainQuantity)),
                new FuelTankState(FuelTankId.Center, Kg(d.CenterCapacity), Kg(d.CenterQuantity)),
            },
            Stand = StandSource?.Invoke(),
            AirportIcao = AirportSource?.Invoke(),
            NearbyObjects = NearbySource?.Invoke() ?? Array.Empty<NearbyObject>(),
            AirportParkings = ParkingSource?.Invoke() ?? Array.Empty<ParkingSpot>(),
            AtcModel = d.AtcModel ?? string.Empty,
            EmptyWeightKg = Units.PoundsToKilograms(d.EmptyWeightPounds),
            PitchDeg = d.PitchDeg,
        };

        lock (_stateLock) _state = state;
    }

    private static void RegisterDefinitions(SimConnect sc)
    {
        void Add(Definition definition, string name, string? unit, SIMCONNECT_DATATYPE type) =>
            sc.AddToDataDefinition(definition, name, unit, type, 0.0f, SimConnect.SIMCONNECT_UNUSED);

        // Order must match AircraftStateData.
        Add(Definition.AircraftState, "TITLE", null, SIMCONNECT_DATATYPE.STRING256);
        Add(Definition.AircraftState, "SIM ON GROUND", "bool", SIMCONNECT_DATATYPE.INT32);
        Add(Definition.AircraftState, "GROUND VELOCITY", "knots", SIMCONNECT_DATATYPE.FLOAT64);
        Add(Definition.AircraftState, "PLANE HEADING DEGREES TRUE", "degrees", SIMCONNECT_DATATYPE.FLOAT64);
        Add(Definition.AircraftState, "PLANE LATITUDE", "degrees", SIMCONNECT_DATATYPE.FLOAT64);
        Add(Definition.AircraftState, "PLANE LONGITUDE", "degrees", SIMCONNECT_DATATYPE.FLOAT64);
        Add(Definition.AircraftState, "BRAKE PARKING POSITION", "bool", SIMCONNECT_DATATYPE.INT32);
        Add(Definition.AircraftState, "GENERAL ENG COMBUSTION:1", "bool", SIMCONNECT_DATATYPE.INT32);
        Add(Definition.AircraftState, "GENERAL ENG COMBUSTION:2", "bool", SIMCONNECT_DATATYPE.INT32);
        Add(Definition.AircraftState, "GENERAL ENG COMBUSTION:3", "bool", SIMCONNECT_DATATYPE.INT32);
        Add(Definition.AircraftState, "GENERAL ENG COMBUSTION:4", "bool", SIMCONNECT_DATATYPE.INT32);
        Add(Definition.AircraftState, "FUEL WEIGHT PER GALLON", "pounds", SIMCONNECT_DATATYPE.FLOAT64);
        Add(Definition.AircraftState, "FUEL TANK LEFT MAIN CAPACITY", "gallons", SIMCONNECT_DATATYPE.FLOAT64);
        Add(Definition.AircraftState, "FUEL TANK LEFT MAIN QUANTITY", "gallons", SIMCONNECT_DATATYPE.FLOAT64);
        Add(Definition.AircraftState, "FUEL TANK RIGHT MAIN CAPACITY", "gallons", SIMCONNECT_DATATYPE.FLOAT64);
        Add(Definition.AircraftState, "FUEL TANK RIGHT MAIN QUANTITY", "gallons", SIMCONNECT_DATATYPE.FLOAT64);
        Add(Definition.AircraftState, "FUEL TANK CENTER CAPACITY", "gallons", SIMCONNECT_DATATYPE.FLOAT64);
        Add(Definition.AircraftState, "FUEL TANK CENTER QUANTITY", "gallons", SIMCONNECT_DATATYPE.FLOAT64);
        Add(Definition.AircraftState, "ATC MODEL", null, SIMCONNECT_DATATYPE.STRING64);
        Add(Definition.AircraftState, "EMPTY WEIGHT", "pounds", SIMCONNECT_DATATYPE.FLOAT64);
        Add(Definition.AircraftState, "PLANE PITCH DEGREES", "degrees", SIMCONNECT_DATATYPE.FLOAT64);
        sc.RegisterDataDefineStruct<AircraftStateData>(Definition.AircraftState);

        Add(Definition.FuelWrite, "FUEL TANK LEFT MAIN QUANTITY", "gallons", SIMCONNECT_DATATYPE.FLOAT64);
        Add(Definition.FuelWrite, "FUEL TANK RIGHT MAIN QUANTITY", "gallons", SIMCONNECT_DATATYPE.FLOAT64);
        Add(Definition.FuelWrite, "FUEL TANK CENTER QUANTITY", "gallons", SIMCONNECT_DATATYPE.FLOAT64);
        sc.RegisterDataDefineStruct<FuelWriteData>(Definition.FuelWrite);

        Add(Definition.GroundMotion, "VELOCITY BODY X", "feet per second", SIMCONNECT_DATATYPE.FLOAT64);
        Add(Definition.GroundMotion, "VELOCITY BODY Z", "feet per second", SIMCONNECT_DATATYPE.FLOAT64);
        Add(Definition.GroundMotion, "ROTATION VELOCITY BODY Y", "radians per second", SIMCONNECT_DATATYPE.FLOAT64);
        sc.RegisterDataDefineStruct<GroundMotionData>(Definition.GroundMotion);

        Add(Definition.InitPosition, "Initial Position", null, SIMCONNECT_DATATYPE.INITPOSITION);
        sc.RegisterDataDefineStruct<SIMCONNECT_DATA_INITPOSITION>(Definition.InitPosition);

        // Order must match AircraftPoseData.
        Add(Definition.AircraftPose, "PLANE LATITUDE", "degrees", SIMCONNECT_DATATYPE.FLOAT64);
        Add(Definition.AircraftPose, "PLANE LONGITUDE", "degrees", SIMCONNECT_DATATYPE.FLOAT64);
        Add(Definition.AircraftPose, "PLANE HEADING DEGREES TRUE", "degrees", SIMCONNECT_DATATYPE.FLOAT64);
        sc.RegisterDataDefineStruct<AircraftPoseData>(Definition.AircraftPose);

        // Order must match PitchWriteData.
        Add(Definition.PitchWrite, "PLANE PITCH DEGREES", "degrees", SIMCONNECT_DATATYPE.FLOAT64);
        Add(Definition.PitchWrite, "VELOCITY BODY Y", "feet per second", SIMCONNECT_DATATYPE.FLOAT64);
        Add(Definition.PitchWrite, "ROTATION VELOCITY BODY X", "radians per second", SIMCONNECT_DATATYPE.FLOAT64);
        sc.RegisterDataDefineStruct<PitchWriteData>(Definition.PitchWrite);
    }

    private void WithSim(Action<SimConnect> action)
    {
        try
        {
            lock (_simLock)
            {
                if (_simConnect is null || !_connected)
                {
                    return;
                }

                action(_simConnect);
            }
        }
        catch (COMException ex)
        {
            Message?.Invoke(this, $"SimConnect call failed: {ex.Message}");
        }
    }

    private void Close()
    {
        bool wasConnected;
        lock (_simLock)
        {
            wasConnected = _connected;
            _connected = false;
            _simConnect?.Dispose();
            _simConnect = null;
            _registeredStations.Clear();
            _eventIds.Clear();
            _nextEventId = FirstClientEventId;
            _pendingSpawns.Clear();
            _spawnedObjects.Clear();
            _cancelledSpawns.Clear();
            _attachments.Clear();
            _drives.Clear();
            _pitchRamp = null;
            _groundMotion = null;
            _catalogs.Clear();
            foreach (var extension in _extensions)
            {
                extension.OnClose();
            }
        }

        lock (_stateLock) _state = AircraftState.Unknown;

        if (wasConnected)
        {
            ConnectionChanged?.Invoke(this, false);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct AircraftStateData
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Title;
        public int OnGround;
        public double GroundSpeedKnots;
        public double HeadingTrueDeg;
        public double Latitude;
        public double Longitude;
        public int ParkingBrake;
        public int Engine1;
        public int Engine2;
        public int Engine3;
        public int Engine4;
        public double FuelPoundsPerGallon;
        public double LeftMainCapacity;
        public double LeftMainQuantity;
        public double RightMainCapacity;
        public double RightMainQuantity;
        public double CenterCapacity;
        public double CenterQuantity;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string AtcModel;
        public double EmptyWeightPounds;
        public double PitchDeg;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct PitchWriteData
    {
        public double PitchDeg;
        public double VelocityBodyY;
        public double RotationVelocityBodyX;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct AircraftPoseData
    {
        public double Latitude;
        public double Longitude;
        public double HeadingTrueDeg;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct FuelWriteData
    {
        public double LeftMainGallons;
        public double RightMainGallons;
        public double CenterGallons;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct GroundMotionData
    {
        public double VelocityBodyX;
        public double VelocityBodyZ;
        public double RotationVelocityBodyY;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SingleValue
    {
        public double Value;
    }
}
