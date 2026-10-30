namespace Rampside.Core.Aircraft.Pmdg;

/// <summary>
/// Pretends to be a PMDG 737 for tests and <c>--fake-pmdg</c>: door events toggle the door annunciator
/// after a short delay, DC buses are powered unless switched off.
/// </summary>
public sealed class FakePmdgLink : IPmdgLink
{
    private readonly object _gate = new();
    private readonly Dictionary<string, bool> _bools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimeSpan> _moving = new(StringComparer.Ordinal);
    private readonly List<string> _sentEvents = new();

    public FakePmdgLink(TimeSpan? doorMoveTime = null)
    {
        DoorMoveTime = doorMoveTime ?? TimeSpan.FromSeconds(3);
        SetDcPower(true);
    }

    public TimeSpan DoorMoveTime { get; }
    public bool HasData { get; set; } = true;
    public string? Problem => HasData ? null : "Fake PMDG link has no data.";
    public string Diagnostics => "Fake PMDG 737 (no header used)";

    public IReadOnlyList<string> SentEvents
    {
        get { lock (_gate) return _sentEvents.ToArray(); }
    }

    public void SetDcPower(bool powered)
    {
        lock (_gate)
        {
            foreach (var bus in Pmdg737Adapter.DoorIndicationBuses)
            {
                _bools[$"ELEC_BusPowered[{bus}]"] = powered;
            }
        }
    }

    public void SetBool(string field, bool value, int index = 0)
    {
        lock (_gate) _bools[Key(field, index)] = value;
    }

    public bool? GetBool(string field, int index = 0)
    {
        if (!HasData)
        {
            return null;
        }

        lock (_gate) return _bools.TryGetValue(Key(field, index), out var value) ? value : false;
    }

    public double? GetNumber(string field, int index = 0) => GetBool(field, index) is { } b ? (b ? 1 : 0) : null;

    public PmdgCduScreen? GetCdu(int index) => null;

    public bool SendEvent(string eventName, uint parameter = 0)
    {
        var door = Pmdg737Adapter.DoorMap.FirstOrDefault(d => d.Event == eventName);
        lock (_gate)
        {
            _sentEvents.Add(eventName);
            if (door.Annunciator is not null && !_moving.ContainsKey(door.Annunciator))
            {
                _moving[door.Annunciator] = TimeSpan.Zero;
            }
        }

        return door.Event is not null;
    }

    public void Advance(TimeSpan elapsed)
    {
        lock (_gate)
        {
            foreach (var annunciator in _moving.Keys.ToList())
            {
                var time = _moving[annunciator] + elapsed;
                if (time >= DoorMoveTime)
                {
                    _moving.Remove(annunciator);
                    _bools[annunciator] = !(_bools.TryGetValue(annunciator, out var open) && open);
                }
                else
                {
                    _moving[annunciator] = time;
                }
            }
        }
    }

    private static string Key(string field, int index) => index == 0 && !field.StartsWith("ELEC_BusPowered", StringComparison.Ordinal)
        ? field
        : $"{field}[{index}]";
}
