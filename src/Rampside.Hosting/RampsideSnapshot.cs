using Rampside.Core.Aircraft;
using Rampside.Core.Services;
using Rampside.Core.Simulation;

namespace Rampside.Hosting;

public sealed record ServiceSnapshot(string Id, string DisplayName, ServiceStatus Status, string Detail, double? Progress, bool IsBusy);

public sealed record DoorSnapshot(DoorId Door, string Name, DoorState State);

/// <summary>Immutable picture of Rampside for user interfaces, rebuilt a few times per second on the host loop.</summary>
public sealed record RampsideSnapshot
{
    public static RampsideSnapshot Empty { get; } = new();

    public bool SimConnected { get; init; }
    public bool UsingFakeSim { get; init; }
    public AircraftState Aircraft { get; init; } = AircraftState.Unknown;
    public string? ProfileName { get; init; }
    public string? AdapterName { get; init; }

    /// <summary>Why the adapter cannot talk to the aircraft yet, or null when it is ready.</summary>
    public string? AdapterProblem { get; init; }

    public string PmdgInfo { get; init; } = string.Empty;
    public double PassengersOnBoard { get; init; }
    public int PassengerCapacity { get; init; }
    public IReadOnlyList<ServiceSnapshot> Services { get; init; } = Array.Empty<ServiceSnapshot>();
    public IReadOnlyList<DoorSnapshot> Doors { get; init; } = Array.Empty<DoorSnapshot>();
    public double PushbackSpeedMps { get; init; }
    public double PushbackSteeringDeg { get; init; }
}
