using Rampside.Core.Simulation;

namespace Rampside.Core.Aircraft.Pmdg;

/// <summary>
/// PMDG 737 through the PMDG SDK only (as the PMDG EULA requires). Doors are toggled with the PMDG door events
/// and read back from the overhead door annunciators. Fuel and payload stay with the aircraft for now;
/// they will go through the CDU menus once those are mapped.
/// </summary>
public sealed class Pmdg737Adapter : IAircraftAdapter
{
    /// <summary>Door event and the annunciator that is lit while that door is not closed.</summary>
    public static readonly IReadOnlyList<(DoorId Door, string Event, string Annunciator)> DoorMap = new[]
    {
        (DoorId.ForwardLeft, "EVT_DOOR_FWD_L", "DOOR_annunFWD_ENTRY"),
        (DoorId.ForwardRight, "EVT_DOOR_FWD_R", "DOOR_annunFWD_SERVICE"),
        (DoorId.AftLeft, "EVT_DOOR_AFT_L", "DOOR_annunAFT_ENTRY"),
        (DoorId.AftRight, "EVT_DOOR_AFT_R", "DOOR_annunAFT_SERVICE"),
        (DoorId.CargoForward, "EVT_DOOR_CARGO_FWD", "DOOR_annunFWD_CARGO"),
        (DoorId.CargoAft, "EVT_DOOR_CARGO_AFT", "DOOR_annunAFT_CARGO"),
        (DoorId.Airstair, "EVT_DOOR_AIRSTAIR", "DOOR_annunAIRSTAIR"),
    };

    /// <summary>Door annunciators need DC power. Assumption until verified: any of DC BATT, DC STBY, DC 1, DC 2.</summary>
    public static readonly int[] DoorIndicationBuses = { 2, 3, 4, 5 };

    // Value sent with the door toggle events. Confirmed in MSFS 2024 with the PMDG 737-800 (aft right door).
    private const uint DoorEventParameter = 1;
    private static readonly TimeSpan DoorTimeout = TimeSpan.FromSeconds(30);

    private readonly IPmdgLink _link;
    private readonly Dictionary<DoorId, (bool Open, TimeSpan Age)> _pending = new();

    public Pmdg737Adapter(IPmdgLink link)
    {
        _link = link;
    }

    public string Name => "PMDG 737";
    public bool IsReady => _link.HasData;

    public string? NotReadyReason => IsReady
        ? null
        : _link.Problem ?? "No data from the PMDG 737 yet. Add EnableDataBroadcast=1 under [SDK] in 737_Options.ini and reload the aircraft.";

    public IReadOnlyList<DoorId> Doors { get; } = DoorMap.Select(d => d.Door).ToArray();
    public bool SupportsFuel => false;
    public bool SupportsPayload => false;

    /// <summary>True when the overhead door lights are powered, so their state means something.</summary>
    public bool HasDoorIndication => DoorIndicationBuses.Any(bus => _link.GetBool("ELEC_BusPowered", bus) == true);

    public DoorState GetDoorState(DoorId door)
    {
        var entry = DoorMap.FirstOrDefault(d => d.Door == door);
        if (entry.Annunciator is null || !HasDoorIndication)
        {
            return DoorState.Unknown;
        }

        return _link.GetBool(entry.Annunciator) switch
        {
            true => DoorState.Open,
            false => DoorState.Closed,
            null => DoorState.Unknown,
        };
    }

    public void SetDoor(DoorId door, bool open)
    {
        var entry = DoorMap.FirstOrDefault(d => d.Door == door);
        if (entry.Event is null)
        {
            return;
        }

        // The events toggle, so only send one when the current state is known and differs.
        var state = GetDoorState(door);
        if (state == DoorState.Unknown || state == (open ? DoorState.Open : DoorState.Closed))
        {
            _pending.Remove(door);
            return;
        }

        if (_pending.TryGetValue(door, out var pending) && pending.Open == open)
        {
            return;
        }

        if (_link.SendEvent(entry.Event, DoorEventParameter))
        {
            _pending[door] = (open, TimeSpan.Zero);
        }
    }

    public void SetFuel(IReadOnlyDictionary<FuelTankId, double> quantitiesKg)
    {
    }

    public void SetPassengerLoad(int passengers)
    {
    }

    public void Tick(TimeSpan elapsed)
    {
        foreach (var door in _pending.Keys.ToList())
        {
            var (open, age) = _pending[door];
            age += elapsed;
            var reached = GetDoorState(door) == (open ? DoorState.Open : DoorState.Closed);
            if (reached || age >= DoorTimeout)
            {
                _pending.Remove(door);
            }
            else
            {
                _pending[door] = (open, age);
            }
        }
    }
}
