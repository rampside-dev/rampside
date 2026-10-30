namespace Rampside.Core.Simulation;

/// <summary>
/// In-memory simulator for tests and for running Rampside without MSFS (<c>--fake</c>).
/// Call <see cref="Advance"/> regularly so ground motion moves the aircraft.
/// </summary>
public sealed class FakeSimBridge : ISimBridge, IGroundVehicleSpawner
{
    private readonly object _gate = new();
    private readonly Dictionary<int, double> _payloadStations = new();
    private readonly List<string> _triggeredEvents = new();
    private readonly Dictionary<int, FakeVehicle> _vehicles = new();
    private readonly Dictionary<int, VehicleAttachment> _attachments = new();
    private readonly Dictionary<int, (VehicleDrive Drive, TimeSpan Elapsed, bool RemoveAtEnd)> _drives = new();
    private static readonly TimeSpan JetwayAttachTime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan JetwayDetachTime = TimeSpan.FromSeconds(8);

    private AircraftState _state;
    private bool _motionActive;
    private bool? _jetwayAttaching;
    private TimeSpan _jetwayElapsed;
    private double _speedMps;
    private double _yawRateRadPerSec;
    private int _nextVehicle = 1;
    private PitchRamp? _pitchRamp;
    private TimeSpan _pitchElapsed;
    private double _naturalPitchDeg;

    public FakeSimBridge(AircraftState? initialState = null)
    {
        _state = initialState ?? CreateDefaultState();
    }

    public static AircraftState CreateDefaultState() => new()
    {
        Title = "Rampside Test Aircraft",
        OnGround = true,
        ParkingBrakeSet = true,
        Latitude = 50.0379,
        Longitude = 8.5622,
        HeadingTrueDeg = 250,
        FuelTanks = new[]
        {
            new FuelTankState(FuelTankId.LeftMain, 6250, 1000),
            new FuelTankState(FuelTankId.RightMain, 6250, 1000),
            new FuelTankState(FuelTankId.Center, 6500, 0),
        },
    };

    /// <summary>A stand with a jetway at rest, like EDDM gate 121 (parking index 156).</summary>
    public static StandInfo DemoStand { get; } = new("EDDM", 156, "Gate 121", 1.0, 83.4, new JetwayInfo(0, false, 0), DemoTaxiways());

    /// <summary>
    /// A straight taxiway "N" 50 m behind the default aircraft, at right angles to it, made of 25 m segments.
    /// With the default heading of 250° the pushback options are facing North and facing South.
    /// </summary>
    public static TaxiwayNetwork DemoTaxiways(AircraftState? aircraft = null, double distanceMeters = 50, double angleDeg = 90)
    {
        var state = aircraft ?? CreateDefaultState();
        var tail = state.HeadingTrueDeg + 180;
        var (lat, lon) = Geo.Offset(state.Latitude, state.Longitude, tail, distanceMeters, 0);
        var points = new List<TaxiPoint>();
        var paths = new List<TaxiPath>();
        for (var i = -6; i <= 6; i++)
        {
            var (pLat, pLon) = Geo.Offset(lat, lon, tail + angleDeg, i * 25.0, 0);
            points.Add(new TaxiPoint(pLat, pLon));
            if (i > -6)
            {
                paths.Add(new TaxiPath(TaxiPathType.Taxi, points.Count - 2, points.Count - 1, "N", 23));
            }
        }

        return new TaxiwayNetwork(points, paths);
    }

    public bool IsConnected { get; private set; }

    public AircraftState State
    {
        get { lock (_gate) return _state; }
    }

    public bool GroundMotionActive
    {
        get { lock (_gate) return _motionActive; }
    }

    public IReadOnlyDictionary<int, double> PayloadStations
    {
        get { lock (_gate) return new Dictionary<int, double>(_payloadStations); }
    }

    public IReadOnlyList<string> TriggeredEvents
    {
        get { lock (_gate) return _triggeredEvents.ToArray(); }
    }

    /// <summary>Vehicles spawned through <see cref="IGroundVehicleSpawner"/>, with their latest position.</summary>
    public IReadOnlyList<FakeVehicle> Vehicles
    {
        get { lock (_gate) return _vehicles.Values.Select(Placed).ToArray(); }
    }

    public int SpawnedVehicleCount
    {
        get { lock (_gate) return _vehicles.Count; }
    }

    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<string>? Message;
    public event EventHandler<IReadOnlyList<string>>? GroundVehicleCatalogReceived;

    public void RequestGroundVehicleCatalog() =>
        GroundVehicleCatalogReceived?.Invoke(this, new[] { "ASO_Baggage_Loader_01", "ASO_Catering_Truck_01", "ASO_Pushback_White" });

    public int? SpawnGroundVehicle(string title, double latitude, double longitude, double headingTrueDeg)
    {
        int vehicle;
        lock (_gate)
        {
            vehicle = _nextVehicle++;
            _vehicles[vehicle] = new FakeVehicle(vehicle, title, latitude, longitude, headingTrueDeg);
        }

        Message?.Invoke(this, $"Spawned {title}");
        return vehicle;
    }

    public void MoveGroundVehicle(int vehicle, double latitude, double longitude, double headingTrueDeg)
    {
        lock (_gate)
        {
            if (_vehicles.TryGetValue(vehicle, out var current))
            {
                _vehicles[vehicle] = current with { Latitude = latitude, Longitude = longitude, HeadingTrueDeg = headingTrueDeg };
            }
        }
    }

    public void RemoveGroundVehicle(int vehicle)
    {
        lock (_gate)
        {
            _vehicles.Remove(vehicle);
            _attachments.Remove(vehicle);
            _drives.Remove(vehicle);
        }
    }

    public void DriveGroundVehicle(int vehicle, VehicleDrive drive, bool removeAtEnd)
    {
        lock (_gate)
        {
            if (_vehicles.ContainsKey(vehicle))
            {
                _attachments.Remove(vehicle);
                _drives[vehicle] = (drive, TimeSpan.Zero, removeAtEnd);
            }
        }
    }

    /// <summary>True while the vehicle is driving along a path.</summary>
    public bool IsDriving(int vehicle)
    {
        lock (_gate) return _drives.ContainsKey(vehicle);
    }

    public void AttachGroundVehicle(int vehicle, VehicleAttachment? attachment)
    {
        lock (_gate)
        {
            if (!_vehicles.TryGetValue(vehicle, out var current))
            {
                return;
            }

            // Detached vehicles stay where they were last placed.
            _vehicles[vehicle] = Placed(current);
            if (attachment is { } place)
            {
                _attachments[vehicle] = place;
                _drives.Remove(vehicle);
            }
            else
            {
                _attachments.Remove(vehicle);
            }
        }
    }

    /// <summary>The vehicle at its current place: next to the aircraft while attached.</summary>
    private FakeVehicle Placed(FakeVehicle vehicle)
    {
        if (_drives.TryGetValue(vehicle.Id, out var driving))
        {
            var (dLat, dLon, dHeading) = driving.Drive.At(driving.Elapsed.TotalSeconds);
            return vehicle with { Latitude = dLat, Longitude = dLon, HeadingTrueDeg = dHeading };
        }

        if (!_attachments.TryGetValue(vehicle.Id, out var attachment))
        {
            return vehicle;
        }

        var (lat, lon, heading) = attachment.Place(_state.Latitude, _state.Longitude, _state.HeadingTrueDeg);
        return vehicle with { Latitude = lat, Longitude = lon, HeadingTrueDeg = heading };
    }

    public void RemoveSpawnedVehicles()
    {
        lock (_gate)
        {
            _vehicles.Clear();
            _attachments.Clear();
            _drives.Clear();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        IsConnected = true;
        ConnectionChanged?.Invoke(this, true);
        return Task.CompletedTask;
    }

    /// <summary>Changes the simulated aircraft, e.g. to set the parking brake in a test.</summary>
    public void Update(Func<AircraftState, AircraftState> change)
    {
        lock (_gate) _state = change(_state);
    }

    public void SetFuel(IReadOnlyDictionary<FuelTankId, double> quantitiesKg)
    {
        lock (_gate)
        {
            var tanks = _state.FuelTanks
                .Select(t => quantitiesKg.TryGetValue(t.Id, out var kg)
                    ? t with { QuantityKg = Math.Clamp(kg, 0, t.CapacityKg) }
                    : t)
                .ToArray();
            _state = _state with { FuelTanks = tanks };
        }
    }

    public void SetPayloadStation(int stationIndex, double weightKg)
    {
        lock (_gate) _payloadStations[stationIndex] = weightKg;
    }

    public void TriggerEvent(string simEventName, uint data = 0)
    {
        lock (_gate)
        {
            _triggeredEvents.Add(simEventName);

            // Like MSFS: TOGGLE_JETWAY moves the stand's jetway towards or away from the aircraft.
            if (simEventName == SimEvents.ToggleJetway && _jetwayAttaching is null && _state.Stand?.Jetway is { } jetway)
            {
                _jetwayAttaching = jetway.Status == JetwayStatus.Rest;
                _jetwayElapsed = TimeSpan.Zero;
            }
        }

        Message?.Invoke(this, $"Sim event {simEventName} ({data})");
    }

    public void SetGroundMotion(double longitudinalMps, double yawRateRadPerSec)
    {
        lock (_gate)
        {
            _motionActive = true;
            _speedMps = longitudinalMps;
            _yawRateRadPerSec = yawRateRadPerSec;
        }
    }

    public void ReleaseGroundMotion()
    {
        lock (_gate)
        {
            _motionActive = false;
            _speedMps = 0;
            _yawRateRadPerSec = 0;
            _state = _state with { GroundSpeedKnots = 0 };
        }
    }

    public void Reposition(double latitude, double longitude, double headingTrueDeg)
    {
        lock (_gate)
        {
            _motionActive = false;
            _speedMps = 0;
            _yawRateRadPerSec = 0;
            _state = _state with
            {
                Latitude = latitude,
                Longitude = longitude,
                HeadingTrueDeg = Geo.NormalizeHeading(headingTrueDeg),
                GroundSpeedKnots = 0,
                OnGround = true,
            };

            // Like the stand monitor: report the stand we landed on, with a taxiway behind it.
            var spot = _state.AirportParkings.FirstOrDefault(p => StandLocator.DistanceMeters(latitude, longitude, p.Latitude, p.Longitude) < 1);
            _state = _state with
            {
                Stand = spot is null
                    ? null
                    : new StandInfo(_state.AirportIcao ?? "FAKE", spot.Index, spot.DisplayName, 0, spot.HeadingDeg,
                        spot.NameCode == 10 ? new JetwayInfo(0, false, 0) : null, DemoTaxiways(_state)),
            };
        }

        Message?.Invoke(this, $"Repositioned to {latitude:F5}, {longitude:F5}, heading {headingTrueDeg:F0}°");
    }

    /// <summary>
    /// A few stands around the default aircraft position for the fake sim; index 0 is where the aircraft starts.
    /// </summary>
    public static IReadOnlyList<ParkingSpot> DemoParkings()
    {
        var start = CreateDefaultState();
        var spots = new List<ParkingSpot>();
        for (var i = 0; i < 6; i++)
        {
            // Side by side, 45 m apart, all facing the same way as the default aircraft.
            var (lat, lon) = Geo.Offset(start.Latitude, start.Longitude, start.HeadingTrueDeg, 0, i * 45.0);
            spots.Add(new ParkingSpot(i, 10, (uint)(121 + i), 0, start.HeadingTrueDeg, 20, lat, lon));
        }

        var (rLat, rLon) = Geo.Offset(start.Latitude, start.Longitude, start.HeadingTrueDeg, 0, -120);
        spots.Add(new ParkingSpot(6, 1, 5, 0, start.HeadingTrueDeg + 90, 30, rLat, rLon));
        return spots;
    }

    /// <summary>Moves the aircraft according to the current ground motion.</summary>
    public void Advance(TimeSpan elapsed)
    {
        lock (_gate)
        {
            AdvanceJetway(elapsed);
            AdvancePitch(elapsed);
            AdvanceDrives(elapsed);

            if (!_motionActive)
            {
                return;
            }

            var dt = elapsed.TotalSeconds;
            var heading = (_state.HeadingTrueDeg + Units.RadiansToDegrees(_yawRateRadPerSec * dt) + 360) % 360;
            var distance = _speedMps * dt;
            var headingRad = Units.DegreesToRadians(heading);
            var latitude = _state.Latitude + distance * Math.Cos(headingRad) / Units.MetersPerDegreeLatitude;
            var longitude = _state.Longitude + distance * Math.Sin(headingRad)
                / (Units.MetersPerDegreeLatitude * Math.Cos(Units.DegreesToRadians(_state.Latitude)));

            _state = _state with
            {
                HeadingTrueDeg = heading,
                Latitude = latitude,
                Longitude = longitude,
                GroundSpeedKnots = Math.Abs(_speedMps) / Units.MetersPerSecondPerKnot,
            };
        }
    }

    /// <summary>Like the SimConnect bridge: the held pitch follows the ramp; released, the aircraft settles back.</summary>
    public void HoldPitch(PitchRamp? ramp)
    {
        lock (_gate)
        {
            if (_pitchRamp is null)
            {
                _naturalPitchDeg = _state.PitchDeg;
            }

            _pitchRamp = ramp;
            _pitchElapsed = TimeSpan.Zero;
            if (ramp is null)
            {
                _state = _state with { PitchDeg = _naturalPitchDeg };
            }
        }
    }

    /// <summary>True while a pitch is held (the nose wheel is lifted or being lowered).</summary>
    public bool PitchHeld
    {
        get { lock (_gate) return _pitchRamp is not null; }
    }

    private void AdvanceDrives(TimeSpan elapsed)
    {
        foreach (var (vehicle, (drive, driven, removeAtEnd)) in _drives.ToArray())
        {
            var now = driven + elapsed;
            if (drive.IsDone(now.TotalSeconds))
            {
                _drives.Remove(vehicle);
                if (removeAtEnd)
                {
                    _vehicles.Remove(vehicle);
                }
                else if (_vehicles.TryGetValue(vehicle, out var current))
                {
                    var (lat, lon, heading) = drive.At(now.TotalSeconds);
                    _vehicles[vehicle] = current with { Latitude = lat, Longitude = lon, HeadingTrueDeg = heading };
                }

                continue;
            }

            _drives[vehicle] = (drive, now, removeAtEnd);
        }
    }

    private void AdvancePitch(TimeSpan elapsed)
    {
        if (_pitchRamp is not { } ramp)
        {
            return;
        }

        _pitchElapsed += elapsed;
        var seconds = _pitchElapsed.TotalSeconds;
        if (ramp.ReleaseWhenDone && ramp.IsDone(seconds))
        {
            _pitchRamp = null;
            _state = _state with { PitchDeg = _naturalPitchDeg };
            return;
        }

        _state = _state with { PitchDeg = ramp.At(seconds) };
    }

    private void AdvanceJetway(TimeSpan elapsed)
    {
        if (_jetwayAttaching is not { } attaching || _state.Stand is not { Jetway: not null } stand)
        {
            return;
        }

        _jetwayElapsed += elapsed;
        var duration = attaching ? JetwayAttachTime : JetwayDetachTime;
        JetwayInfo jetway;
        if (_jetwayElapsed >= duration)
        {
            jetway = attaching ? new JetwayInfo(7, true, 0) : new JetwayInfo(0, false, 0);
            _jetwayAttaching = null;
        }
        else
        {
            // Same raw status sequence as observed in MSFS 2024 (1, 2, 3 while attaching).
            var raw = attaching ? 1 + (int)(3 * _jetwayElapsed.Ticks / duration.Ticks) : 4;
            jetway = new JetwayInfo(raw, true, 0);
        }

        _state = _state with { Stand = stand with { Jetway = jetway } };
    }

    public ValueTask DisposeAsync()
    {
        if (IsConnected)
        {
            IsConnected = false;
            ConnectionChanged?.Invoke(this, false);
        }

        return ValueTask.CompletedTask;
    }
}

public sealed record FakeVehicle(int Id, string Title, double Latitude, double Longitude, double HeadingTrueDeg);
