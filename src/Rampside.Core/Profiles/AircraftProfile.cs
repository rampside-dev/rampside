using Rampside.Core.Aircraft;

namespace Rampside.Core.Profiles;

/// <summary>
/// Per-aircraft settings, loaded from profiles/*.json. Every value has a default,
/// so a profile only needs to list what differs.
/// </summary>
public sealed class AircraftProfile
{
    public const string DefaultAdapter = "default";
    public const string Pmdg737Adapter = "pmdg-737";

    public string Id { get; init; } = "generic";
    public string Name { get; init; } = "Generic aircraft";

    /// <summary>Case-insensitive substrings of the aircraft title that select this profile.</summary>
    public List<string> Match { get; init; } = new();

    /// <summary>How Rampside talks to the aircraft: "default" or "pmdg-737".</summary>
    public string Adapter { get; init; } = DefaultAdapter;

    public DoorConfig Doors { get; init; } = new();

    public PassengerConfig Passengers { get; init; } = new();
    public List<PayloadStationConfig> PayloadStations { get; init; } = new();
    public FuelConfig Fuel { get; init; } = new();
    public PushbackConfig Pushback { get; init; } = new();
    public ConnectableConfig Jetway { get; init; } = new();
    public ConnectableConfig Stairs { get; init; } = new();
    public ConnectableConfig Gpu { get; init; } = new();
}

public sealed class PassengerConfig
{
    public int Capacity { get; init; } = 150;
    public double WeightKg { get; init; } = 84;
    public double BagWeightKg { get; init; } = 15;
    public double BoardingPerMinute { get; init; } = 15;
    public double DeboardingPerMinute { get; init; } = 25;
    public double PreparationSeconds { get; init; } = 20;
}

public enum PayloadKind
{
    Passengers,
    Cargo,
}

public sealed class PayloadStationConfig
{
    /// <summary>SimConnect index used in PAYLOAD STATION WEIGHT:index.</summary>
    public int Index { get; init; }
    public string Name { get; init; } = string.Empty;
    public PayloadKind Kind { get; init; } = PayloadKind.Passengers;

    /// <summary>Relative share of the passenger or cargo mass this station receives.</summary>
    public double Share { get; init; } = 1;
}

public sealed class FuelConfig
{
    public double RateKgPerMinute { get; init; } = 600;
    public double TruckArrivalSeconds { get; init; } = 45;
    public bool AllowWithEnginesRunning { get; init; }
}

public sealed class PushbackConfig
{
    public double WheelbaseMeters { get; init; } = 12.6;
    public double SpeedMps { get; init; } = 1.2;
    public double AccelerationMps2 { get; init; } = 0.25;
    public double MaxSteeringDeg { get; init; } = 60;
    public double SteeringRateDegPerSecond { get; init; } = 10;
    public double ConnectSeconds { get; init; } = 30;
    public double DisconnectSeconds { get; init; } = 20;
}

/// <summary>Which doors Rampside opens and closes automatically. Only used when the adapter controls doors.</summary>
public sealed class DoorConfig
{
    public bool Automatic { get; init; } = true;

    /// <summary>Opened when the jetway is connected, closed before it is removed.</summary>
    public List<DoorId> Jetway { get; init; } = new() { DoorId.ForwardLeft };

    /// <summary>Opened when the stairs are connected, closed before they are removed.</summary>
    public List<DoorId> Stairs { get; init; } = new() { DoorId.ForwardLeft };

    /// <summary>Opened while baggage is loaded or unloaded during boarding and deboarding.</summary>
    public List<DoorId> Cargo { get; init; } = new() { DoorId.CargoForward, DoorId.CargoAft };
}

public sealed class ConnectableConfig
{
    /// <summary>Sim event sent on connect and disconnect. Null = Rampside default, empty = no event.</summary>
    public string? Event { get; init; }
    public double ConnectSeconds { get; init; } = 20;
    public double DisconnectSeconds { get; init; } = 15;
}
