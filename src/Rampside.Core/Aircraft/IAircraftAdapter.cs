using Rampside.Core.Services;
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

/// <summary>Ground equipment some aircraft bring themselves (PMDG ground services) instead of the sim's.</summary>
public enum GroundEquipment
{
    Jetway,
    Stairs,
    GroundPower,

    /// <summary>Wheel chocks: "connected" means set.</summary>
    Chocks,
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

    /// <summary>
    /// True when the aircraft brings this equipment itself (e.g. PMDG stairs and GPU). Rampside then asks the
    /// aircraft through <see cref="SetEquipment"/> instead of sending the sim's ground service events.
    /// </summary>
    bool HandlesEquipment(GroundEquipment equipment);

    /// <summary>False when Rampside cannot do this job for the aircraft at all; the UI then leaves it out.</summary>
    bool Supports(GroundEquipment equipment);

    /// <summary>Asks the aircraft to bring (<paramref name="connected"/>) or take away its equipment. Returns null when accepted, else the reason.</summary>
    string? SetEquipment(GroundEquipment equipment, bool connected, AircraftState state);

    /// <summary>State of the aircraft's own equipment, or null when the aircraft does not report it.</summary>
    ObservedEquipment? GetEquipmentState(GroundEquipment equipment, AircraftState state);

    /// <summary>Notes for the log, e.g. a request the aircraft refused.</summary>
    event EventHandler<string>? Message;

    bool SupportsFuel { get; }
    void SetFuel(IReadOnlyDictionary<FuelTankId, double> quantitiesKg);

    bool SupportsPayload { get; }
    void SetPayload(PayloadLoad load);

    void Tick(TimeSpan elapsed);
}
