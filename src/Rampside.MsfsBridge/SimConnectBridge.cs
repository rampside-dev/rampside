using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;
using Rampside.Core;
using Rampside.Core.Simulation;

namespace Rampside.MsfsBridge;

/// <summary>
/// Connects to MSFS via SimConnect, keeps <see cref="State"/> up to date and writes fuel, payload,
/// events and ground motion. Reconnects automatically when the sim is restarted.
/// </summary>
public sealed class SimConnectBridge : ISimBridge
{
    private const string ClientName = "Rampside";
    private const double DefaultFuelPoundsPerGallon = 6.7;
    private const uint FirstClientEventId = 1000;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private readonly object _simLock = new();
    private readonly object _stateLock = new();
    private readonly AutoResetEvent _messageSignal = new(false);
    private readonly HashSet<int> _registeredStations = new();
    private readonly Dictionary<string, uint> _eventIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ISimConnectExtension> _extensions = new();

    private SimConnect? _simConnect;
    private bool _connected;
    private bool _quitReceived;
    private uint _nextEventId = FirstClientEventId;
    private double _fuelPoundsPerGallon = DefaultFuelPoundsPerGallon;
    private AircraftState _state = AircraftState.Unknown;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private enum Definition : uint
    {
        AircraftState = 1,
        FuelWrite = 2,
        GroundMotion = 3,
        PayloadStationBase = 100,
    }

    private enum Request : uint
    {
        AircraftState = 1,
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

    public bool IsConnected
    {
        get { lock (_simLock) return _connected; }
    }

    public AircraftState State
    {
        get { lock (_stateLock) return _state; }
    }

    /// <summary>Adds an extension (e.g. a PMDG SDK link) that sets up its own SimConnect data. Call before StartAsync.</summary>
    public void AddExtension(ISimConnectExtension extension)
    {
        lock (_simLock) _extensions.Add(extension);
    }

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

        WithSim(sc => sc.SetDataOnSimObject(
            Definition.GroundMotion, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, data));
    }

    public void ReleaseGroundMotion()
    {
        // Writing zero once leaves the aircraft at rest; afterwards the sim's own physics take over again.
        SetGroundMotion(0, 0);
    }

    public async ValueTask DisposeAsync()
    {
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

    private void OnRecvSimobjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
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
        sc.RegisterDataDefineStruct<AircraftStateData>(Definition.AircraftState);

        Add(Definition.FuelWrite, "FUEL TANK LEFT MAIN QUANTITY", "gallons", SIMCONNECT_DATATYPE.FLOAT64);
        Add(Definition.FuelWrite, "FUEL TANK RIGHT MAIN QUANTITY", "gallons", SIMCONNECT_DATATYPE.FLOAT64);
        Add(Definition.FuelWrite, "FUEL TANK CENTER QUANTITY", "gallons", SIMCONNECT_DATATYPE.FLOAT64);
        sc.RegisterDataDefineStruct<FuelWriteData>(Definition.FuelWrite);

        Add(Definition.GroundMotion, "VELOCITY BODY X", "feet per second", SIMCONNECT_DATATYPE.FLOAT64);
        Add(Definition.GroundMotion, "VELOCITY BODY Z", "feet per second", SIMCONNECT_DATATYPE.FLOAT64);
        Add(Definition.GroundMotion, "ROTATION VELOCITY BODY Y", "radians per second", SIMCONNECT_DATATYPE.FLOAT64);
        sc.RegisterDataDefineStruct<GroundMotionData>(Definition.GroundMotion);
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
