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

    /// <summary>
    /// ICAO type designators of the aircraft ("B738"). Flight plans for other types are refused.
    /// Empty = use the sim's ATC MODEL.
    /// </summary>
    public List<string> IcaoTypes { get; init; } = new();

    /// <summary>
    /// Aircraft family: "default" (the sim's own systems) or "pmdg-737". It sets the usual procedure for every job,
    /// see <see cref="Aircraft.Procedures.AircraftProcedureCatalog"/>.
    /// </summary>
    public string Adapter { get; init; } = DefaultAdapter;

    /// <summary>
    /// Jobs done differently from the family, e.g. <c>{ "stairs": "sim", "groundPower": "none" }</c>.
    /// Jobs: doors, jetway, stairs, groundPower, fuel, payload.
    /// </summary>
    public Aircraft.Procedures.ProcedureSet Procedures { get; init; } = new();

    public DoorConfig Doors { get; init; } = new();

    /// <summary>
    /// How the adapter opens and reads each door (used by the PMDG adapters): the SDK event that toggles the door
    /// and the data field (door light) that is true while the door is not closed. Empty = adapter default.
    /// </summary>
    public List<DoorMapping> DoorMap { get; init; } = new();

    public PassengerConfig Passengers { get; init; } = new();
    public List<PayloadStationConfig> PayloadStations { get; init; } = new();
    public FuelConfig Fuel { get; init; } = new();
    public PushbackConfig Pushback { get; init; } = new();
    public ConnectableConfig Jetway { get; init; } = new();
    public ConnectableConfig Stairs { get; init; } = new();
    public ConnectableConfig Gpu { get; init; } = new();

    /// <summary>Wheel chocks; only used where a procedure handles them (PMDG CDU).</summary>
    public ConnectableConfig Chocks { get; init; } = new() { ConnectSeconds = 10, DisconnectSeconds = 10 };
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

    /// <summary>Preferred turn radius of a planned pushback; smaller when the taxi line is close behind the stand.</summary>
    public double TurnRadiusMeters { get; init; } = 20;

    /// <summary>How far a planned pushback continues straight along the taxi line after the turn.</summary>
    public double StraightAfterTurnMeters { get; init; } = 10;

    public double ConnectSeconds { get; init; } = 30;
    public double DisconnectSeconds { get; init; } = 20;

    /// <summary>
    /// The crew waits this long after the parking brake was released (before pushing) or set (before disconnecting);
    /// the brake must stay in that position meanwhile. Setting the brake while moving still stops at once.
    /// </summary>
    public double BrakeReactionSeconds { get; init; } = 3;

    /// <summary>Ground vehicle (SimObject title) shown as the tug while the pushback runs; empty = no tug.</summary>
    public string TugTitle { get; init; } = "ASO_Pushback_White";

    /// <summary>
    /// Nose gear position ahead of the aircraft reference point (the x of the nose wheel contact point in the
    /// aircraft's flight_model.cfg, converted from feet). Null = 80 % of <see cref="WheelbaseMeters"/>: the reference
    /// point usually sits a little ahead of the main gear (PMDG 737-800: nose gear 12.3 m ahead, wheelbase 15.6 m).
    /// </summary>
    public double? NoseGearForwardMeters { get; init; }

    /// <summary>
    /// From the nose gear to the tug's reference point. The default tug ASO_Pushback_White is towbarless and its
    /// reference point is at the cradle, so 0 puts the nose wheel into the cradle (checked in the sim with the PMDG 737).
    /// </summary>
    public double TugDistanceMeters { get; init; }

    /// <summary>
    /// How far the towbarless tug pitches the aircraft nose up to lift the nose wheel off the ground, so it does not
    /// scrub while the tug turns it; 0 = no lift. PMDG 737: the nose wheel leaves the ground at about 1.7°, GSX uses about 2.4°.
    /// </summary>
    public double NoseLiftDeg { get; init; } = 2.0;

    /// <summary>Time to lift or lower the nose wheel (sin² curve).</summary>
    public double NoseLiftSeconds { get; init; } = 8;

    /// <summary>Nose gear position ahead of the reference point, with the default filled in.</summary>
    public double NoseGearMeters => NoseGearForwardMeters ?? 0.8 * WheelbaseMeters;
}

public sealed class DoorMapping
{
    public DoorId Door { get; init; }
    public string Event { get; init; } = string.Empty;
    public string Indicator { get; init; } = string.Empty;
}

/// <summary>Which doors Rampside opens and closes automatically. Only used when the adapter controls doors.</summary>
public sealed class DoorConfig
{
    public bool Automatic { get; init; } = true;

    /// <summary>Opened when the jetway is connected, closed before it is removed.</summary>
    public List<DoorId> Jetway { get; init; } = new() { DoorId.ForwardLeft };

    /// <summary>Wait after the jetway has attached before opening its doors (the sim may open the door itself).</summary>
    public double JetwayOpenDelaySeconds { get; init; } = 8;

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
