using Rampside.Core.Aircraft.Procedures;
using Rampside.Core.Services;
using Rampside.Core.Simulation;

namespace Rampside.Core.Aircraft;

/// <summary>
/// An aircraft adapter made of one procedure per job (doors, jetway, stairs, ground power, fuel, payload),
/// chosen by the profile through <see cref="AircraftProcedureCatalog"/>. New aircraft only need the procedures
/// that differ, e.g. an aircraft with PMDG-style doors but the sim's ground services.
/// </summary>
public class ComposedAircraftAdapter : IAircraftAdapter
{
    private readonly ProcedureContext _context;
    private readonly IReadOnlyList<IAircraftProcedure> _all;
    private IReadOnlyList<string>? _unreportedWarnings;

    public ComposedAircraftAdapter(Composition composition)
    {
        Family = composition.Family;
        Procedures = composition.Procedures;
        Warnings = composition.Warnings;
        _context = composition.Context;
        _context.Message += (_, text) => Message?.Invoke(this, text);
        _all = Procedures.All.Distinct().ToArray();
        _unreportedWarnings = Warnings.Count > 0 ? Warnings : null;
    }

    public AircraftFamily Family { get; }
    public AircraftProcedures Procedures { get; }

    /// <summary>Problems with the profile's procedure names; also sent as messages on the first tick.</summary>
    public IReadOnlyList<string> Warnings { get; }

    public string Name => Family.Name;

    /// <summary>Which procedure does which job, e.g. "doors pmdg-sdk, jetway sim, stairs pmdg-cdu, ...".</summary>
    public string Summary =>
        $"doors {Procedures.Doors.Name}, " +
        string.Join(", ", Procedures.Equipment.Values.Select(e => $"{NoEquipment.Describe(e.Equipment)} {e.Name}")) +
        $", fuel {Procedures.Fuel.Name}, payload {Procedures.Payload.Name}";

    public event EventHandler<string>? Message;

    public bool IsReady => NotReadyReason is null;
    public string? NotReadyReason => _all.Select(p => p.NotReadyReason).FirstOrDefault(r => r is not null);

    public IReadOnlyList<DoorId> Doors => Procedures.Doors.Doors;
    public DoorState GetDoorState(DoorId door) => Procedures.Doors.GetDoorState(door);
    public void SetDoor(DoorId door, bool open) => Procedures.Doors.SetDoor(door, open);

    /// <summary>Every piece of equipment has a procedure; even "sim" sends its event itself.</summary>
    public bool HandlesEquipment(GroundEquipment equipment) => Procedures.Equipment.ContainsKey(equipment);

    public bool Supports(GroundEquipment equipment) =>
        Procedures.Equipment.TryGetValue(equipment, out var procedure) && procedure is not NoEquipment;

    public string? SetEquipment(GroundEquipment equipment, bool connected, AircraftState state) =>
        Procedures.Equipment.TryGetValue(equipment, out var procedure)
            ? procedure.Request(connected, state)
            : $"No procedure for {NoEquipment.Describe(equipment)}.";

    public ObservedEquipment? GetEquipmentState(GroundEquipment equipment, AircraftState state) =>
        Procedures.Equipment.TryGetValue(equipment, out var procedure) ? procedure.Observe(state) : null;

    public bool SupportsFuel => Procedures.Fuel.Supported;
    public void SetFuel(IReadOnlyDictionary<FuelTankId, double> quantitiesKg) => Procedures.Fuel.SetFuel(quantitiesKg);

    public bool SupportsPayload => Procedures.Payload.Supported;
    public void SetPayload(PayloadLoad load) => Procedures.Payload.SetPayload(load);

    public void Tick(TimeSpan elapsed)
    {
        // Warnings found while building come out on the first tick, when the orchestrator listens.
        if (_unreportedWarnings is { } warnings)
        {
            _unreportedWarnings = null;
            foreach (var warning in warnings)
            {
                Message?.Invoke(this, warning);
            }
        }

        _context.Tick(elapsed);
        foreach (var procedure in _all)
        {
            procedure.Tick(elapsed);
        }
    }
}
