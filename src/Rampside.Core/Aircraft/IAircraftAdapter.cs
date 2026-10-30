using Rampside.Core.Simulation;

namespace Rampside.Core.Aircraft;

/// <summary>Doors Rampside knows about. Not every aircraft has all of them.</summary>
public enum DoorId
{
    ForwardLeft,
    ForwardRight,
    AftLeft,
    AftRight,
    CargoForward,
    CargoAft,
    Airstair,
}

public enum DoorState
{
    Unknown,
    Closed,
    Open,
}

/// <summary>
/// How Rampside talks to one aircraft type. The default adapter writes the simulator's own variables;
/// study-level aircraft (PMDG, ...) get adapters that go through the aircraft's own interfaces,
/// so the aircraft always stays in charge of its fuel, payload and doors.
/// </summary>
public interface IAircraftAdapter
{
    string Name { get; }

    /// <summary>False while the adapter cannot talk to the aircraft yet, see <see cref="NotReadyReason"/>.</summary>
    bool IsReady { get; }
    string? NotReadyReason { get; }

    /// <summary>Doors this adapter can open, close and report.</summary>
    IReadOnlyList<DoorId> Doors { get; }
    DoorState GetDoorState(DoorId door);

    /// <summary>Requests a door to open or close. Does nothing if it is already in that state.</summary>
    void SetDoor(DoorId door, bool open);

    bool SupportsFuel { get; }
    void SetFuel(IReadOnlyDictionary<FuelTankId, double> quantitiesKg);

    bool SupportsPayload { get; }
    void SetPassengerLoad(int passengers);

    void Tick(TimeSpan elapsed);
}
