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
    public bool OnGround { get; init; }
    public double GroundSpeedKnots { get; init; }
    public double HeadingTrueDeg { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public bool ParkingBrakeSet { get; init; }
    public bool EnginesRunning { get; init; }
    public IReadOnlyList<FuelTankState> FuelTanks { get; init; } = Array.Empty<FuelTankState>();

    public double TotalFuelKg => FuelTanks.Sum(t => t.QuantityKg);
    public double FuelCapacityKg => FuelTanks.Sum(t => t.CapacityKg);
    public bool IsStationary => OnGround && GroundSpeedKnots < 1.0;
}
