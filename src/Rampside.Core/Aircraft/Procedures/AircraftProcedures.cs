using Rampside.Core.Aircraft.Pmdg;
using Rampside.Core.Profiles;
using Rampside.Core.Services;
using Rampside.Core.Simulation;

namespace Rampside.Core.Aircraft.Procedures;

/// <summary>
/// One way of doing one job on one kind of aircraft, e.g. "ground power through the PMDG CDU" or
/// "stairs through the sim's ground services". Profiles pick a procedure per job (see <see cref="AircraftProcedureCatalog"/>).
/// </summary>
public interface IAircraftProcedure
{
    /// <summary>Catalog name, e.g. "sim" or "pmdg-cdu".</summary>
    string Name { get; }

    /// <summary>Null when the procedure can work now, else why not (e.g. no PMDG SDK data).</summary>
    string? NotReadyReason { get; }

    void Tick(TimeSpan elapsed);
}

public interface IDoorProcedure : IAircraftProcedure
{
    /// <summary>Doors this procedure can open, close and report.</summary>
    IReadOnlyList<DoorId> Doors { get; }
    DoorState GetDoorState(DoorId door);

    /// <summary>Requests a door to open or close. Does nothing if it is already in that state.</summary>
    void SetDoor(DoorId door, bool open);
}

/// <summary>Jetway, stairs or ground power.</summary>
public interface IEquipmentProcedure : IAircraftProcedure
{
    GroundEquipment Equipment { get; }

    /// <summary>Brings (<paramref name="connected"/>) or takes away the equipment. Returns null when accepted, else the reason.</summary>
    string? Request(bool connected, AircraftState state);

    /// <summary>Where the equipment is, or null when this procedure cannot see it (Rampside then follows its own timing).</summary>
    ObservedEquipment? Observe(AircraftState state);
}

public interface IFuelProcedure : IAircraftProcedure
{
    /// <summary>False when the aircraft is fuelled through its own menus.</summary>
    bool Supported { get; }
    void SetFuel(IReadOnlyDictionary<FuelTankId, double> quantitiesKg);
}

public interface IPayloadProcedure : IAircraftProcedure
{
    /// <summary>False when the aircraft is loaded through its own menus.</summary>
    bool Supported { get; }
    void SetPayload(PayloadLoad load);
}

/// <summary>The procedures one aircraft uses, one per job.</summary>
public sealed record AircraftProcedures(
    IDoorProcedure Doors,
    IReadOnlyDictionary<GroundEquipment, IEquipmentProcedure> Equipment,
    IFuelProcedure Fuel,
    IPayloadProcedure Payload)
{
    public IEnumerable<IAircraftProcedure> All =>
        new IAircraftProcedure[] { Doors, Fuel, Payload }.Concat(Equipment.Values);
}

/// <summary>What procedures may use: the sim, the profile, aircraft SDK links and helpers shared between procedures.</summary>
public sealed class ProcedureContext
{
    private readonly IPmdgLink? _pmdg;
    private PmdgCduDriver? _pmdgCdu;

    public ProcedureContext(ISimBridge sim, AircraftProfile profile, IPmdgLink? pmdg = null)
    {
        Sim = sim;
        Profile = profile;
        _pmdg = pmdg;
    }

    public ISimBridge Sim { get; }
    public AircraftProfile Profile { get; }

    /// <summary>The PMDG SDK link, or a stand-in that explains why there is none.</summary>
    public IPmdgLink Pmdg => _pmdg ?? new UnavailablePmdgLink("PMDG SDK not set up.");

    /// <summary>One CDU driver for all PMDG procedures, so their key presses never mix.</summary>
    public PmdgCduDriver PmdgCdu
    {
        get
        {
            if (_pmdgCdu is null)
            {
                _pmdgCdu = new PmdgCduDriver(Pmdg);
                _pmdgCdu.Message += (_, text) => Log(text);
            }

            return _pmdgCdu;
        }
    }

    /// <summary>Notes for the log.</summary>
    public event EventHandler<string>? Message;

    public void Log(string text) => Message?.Invoke(this, text);

    /// <summary>Ticks the shared helpers. Called once per tick by the adapter, before the procedures.</summary>
    public void Tick(TimeSpan elapsed) => _pmdgCdu?.Tick(elapsed);
}
