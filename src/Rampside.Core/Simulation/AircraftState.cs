namespace Rampside.Core.Simulation;

public enum FuelTankId
{
    LeftMain,
    RightMain,
    Center,
}

public sealed record FuelTankState(FuelTankId Id, double CapacityKg, double QuantityKg);

/// <summary>Snapshot of the user aircraft as reported by the simulator.</summary>
public sealed record AircraftState
{
    public static AircraftState Unknown { get; } = new();

    public string Title { get; init; } = string.Empty;

    /// <summary>The sim's ATC MODEL variable, usually containing the ICAO type ("B738"). Empty when unknown.</summary>
    public string AtcModel { get; init; } = string.Empty;

    /// <summary>Empty weight of the aircraft in the sim, 0 when unknown.</summary>
    public double EmptyWeightKg { get; init; }

    /// <summary>Nearest airport (within a few km) while parked, null when unknown. The stand's airport takes precedence.</summary>
    public string? AirportIcao { get; init; }

    public bool OnGround { get; init; }
    public double GroundSpeedKnots { get; init; }
    public double HeadingTrueDeg { get; init; }

    /// <summary>PLANE PITCH DEGREES as the sim reports it: negative = nose up.</summary>
    public double PitchDeg { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public bool ParkingBrakeSet { get; init; }
    public bool EnginesRunning { get; init; }
    public IReadOnlyList<FuelTankState> FuelTanks { get; init; } = Array.Empty<FuelTankState>();

    /// <summary>Stand the aircraft is parked on, with its jetway. Null when unknown or not on a stand.</summary>
    public StandInfo? Stand { get; init; }

    /// <summary>Parking spots of <see cref="AirportIcao"/> while on the ground there; empty when unknown.</summary>
    public IReadOnlyList<ParkingSpot> AirportParkings { get; init; } = Array.Empty<ParkingSpot>();

    /// <summary>SimObjects within about 80 m of the aircraft (not the aircraft itself), refreshed every few seconds.</summary>
    public IReadOnlyList<NearbyObject> NearbyObjects { get; init; } = Array.Empty<NearbyObject>();

    public double TotalFuelKg => FuelTanks.Sum(t => t.QuantityKg);
    public double FuelCapacityKg => FuelTanks.Sum(t => t.CapacityKg);
    public bool IsStationary => OnGround && GroundSpeedKnots < 1.0;
}
