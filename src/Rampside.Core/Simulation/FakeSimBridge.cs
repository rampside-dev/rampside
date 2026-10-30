namespace Rampside.Core.Simulation;

/// <summary>
/// In-memory simulator for tests and for running Rampside without MSFS (<c>--fake</c>).
/// Call <see cref="Advance"/> regularly so ground motion moves the aircraft.
/// </summary>
public sealed class FakeSimBridge : ISimBridge
{
    private readonly object _gate = new();
    private readonly Dictionary<int, double> _payloadStations = new();
    private readonly List<string> _triggeredEvents = new();
    private AircraftState _state;
    private bool _motionActive;
    private double _speedMps;
    private double _yawRateRadPerSec;

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

    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<string>? Message;

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
        lock (_gate) _triggeredEvents.Add(simEventName);
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

    /// <summary>Moves the aircraft according to the current ground motion.</summary>
    public void Advance(TimeSpan elapsed)
    {
        lock (_gate)
        {
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
