using Rampside.Core.Profiles;
using Rampside.Core.Aircraft.Procedures;

namespace Rampside.Core.Aircraft.Pmdg;

/// <summary>
/// "pmdg-sdk" doors: toggled with the PMDG door events, read back from the overhead door lights.
/// The events toggle and the PMDG ignores them while a door moves, so the wanted state is kept per door and
/// driven carefully (one event per door every 12 s, at most three tries, dropped while the lights are dark).
/// </summary>
public sealed class PmdgSdkDoors : IDoorProcedure
{
    // Value sent with the door toggle events. Confirmed in MSFS 2024 with the PMDG 737-800.
    private const uint DoorEventParameter = 1;
    private const int MaxDoorAttempts = 3;

    // The PMDG ignores a door event while that door is still moving (seen in the sim: a close sent 3 s after
    // an open was lost). Doors take up to about 10 s, so events for one door are at least this far apart.
    private static readonly TimeSpan MinToggleInterval = TimeSpan.FromSeconds(12);

    private readonly IPmdgLink _link;
    private readonly IReadOnlyList<DoorMapping> _doorMap;
    private readonly Dictionary<DoorId, (bool Open, int Attempts)> _wanted = new();
    private readonly Dictionary<DoorId, TimeSpan> _sinceToggle = new();
    private readonly Action<string> _log;

    public PmdgSdkDoors(IPmdgLink link, IReadOnlyList<DoorMapping>? doorMap = null, Action<string>? log = null)
    {
        _link = link;
        _log = log ?? (_ => { });
        _doorMap = doorMap is { Count: > 0 } ? doorMap : Pmdg737.DefaultDoorMap;
        Doors = _doorMap.Select(d => d.Door).Distinct().ToArray();
    }

    public string Name => "pmdg-sdk";
    public string? NotReadyReason => Pmdg737.NotReadyReason(_link);
    public IReadOnlyList<DoorId> Doors { get; }

    /// <summary>True when the overhead door lights are powered, so their state means something.</summary>
    public bool HasDoorIndication => Pmdg737.DoorIndicationBuses.Any(bus => _link.GetBool("ELEC_BusPowered", bus) == true);

    public DoorState GetDoorState(DoorId door)
    {
        var mapping = Find(door);
        if (mapping is null || !HasDoorIndication)
        {
            return DoorState.Unknown;
        }

        return _link.GetBool(mapping.Indicator) switch
        {
            true => DoorState.Open,
            false => DoorState.Closed,
            null => DoorState.Unknown,
        };
    }

    public void SetDoor(DoorId door, bool open)
    {
        if (Find(door) is null)
        {
            return;
        }

        if (_wanted.TryGetValue(door, out var wanted) && wanted.Open == open)
        {
            return; // already working on it
        }

        _wanted[door] = (open, 0);
        Drive(door);
    }

    public void Tick(TimeSpan elapsed)
    {
        foreach (var door in _sinceToggle.Keys.ToList())
        {
            _sinceToggle[door] += elapsed;
        }

        foreach (var door in _wanted.Keys.ToList())
        {
            Drive(door);
        }
    }

    /// <summary>Sends a toggle when the door is not yet where it should be and the last toggle had time to finish.</summary>
    private void Drive(DoorId door)
    {
        var (open, attempts) = _wanted[door];
        var state = GetDoorState(door);
        if (state == (open ? DoorState.Open : DoorState.Closed))
        {
            _wanted.Remove(door);
            return;
        }

        if (attempts >= MaxDoorAttempts)
        {
            _wanted.Remove(door);
            _log($"{Label(door)} door still {state.ToString().ToLowerInvariant()} after {attempts} {(open ? "open" : "close")} events, giving up.");
            return;
        }

        // The events toggle, so they are only safe while the state is known. Without door lights the request
        // is dropped rather than carried out by surprise once power comes back.
        if (state == DoorState.Unknown)
        {
            _wanted.Remove(door);
            _log($"{Label(door)} door not {(open ? "opened" : "closed")}: the door lights are dark (no DC power).");
            return;
        }

        if (_sinceToggle.GetValueOrDefault(door, MinToggleInterval) < MinToggleInterval)
        {
            return;
        }

        if (_link.SendEvent(Find(door)!.Event, DoorEventParameter))
        {
            _sinceToggle[door] = TimeSpan.Zero;
            _wanted[door] = (open, attempts + 1);
            _log($"{Label(door)} door: {(open ? "open" : "close")} event sent{(attempts > 0 ? $" (try {attempts + 1})" : string.Empty)}.");
        }
    }

    private static string Label(DoorId door) => door switch
    {
        DoorId.ForwardLeft => "L1",
        DoorId.ForwardRight => "R1",
        DoorId.AftLeft => "L2",
        DoorId.AftRight => "R2",
        DoorId.CargoForward => "Forward cargo",
        DoorId.CargoAft => "Aft cargo",
        _ => door.ToString(),
    };

    private DoorMapping? Find(DoorId door) => _doorMap.FirstOrDefault(d => d.Door == door);
}
