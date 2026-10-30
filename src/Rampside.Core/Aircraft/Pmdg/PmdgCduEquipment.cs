using Rampside.Core.Aircraft.Procedures;
using Rampside.Core.Services;
using Rampside.Core.Simulation;

namespace Rampside.Core.Aircraft.Pmdg;

/// <summary>
/// "pmdg-cdu" stairs or ground power: the PMDG's own vehicles, requested on the right CDU
/// (FS ACTIONS → GROUND SERVICES, see <see cref="PmdgCduDriver"/>). The sim's ground service events do nothing for them.
/// Seen from outside: ground power by the GRD POWER AVAILABLE light, stairs by PMDG_Stairs_* trucks next to the aircraft,
/// chocks only on the CDU (SET or REMOVED in the large font), so their state is known only after the right CDU showed it.
/// While a request is on its way (up to 3 minutes) the equipment is reported as moving.
/// </summary>
public sealed class PmdgCduEquipment : IEquipmentProcedure
{
    private static readonly TimeSpan ArrivalTimeout = TimeSpan.FromMinutes(3);
    private const double StairsSearchRadiusM = 40;
    private const double StairsStillM = 0.3;

    private readonly IPmdgLink _link;
    private readonly PmdgCduDriver _cdu;
    private readonly IReadOnlyList<PmdgServiceItem> _items;
    private PendingRequest? _pending;
    private IReadOnlyList<NearbyObject>? _lastScan;
    private Dictionary<uint, NearbyObject> _lastStairs = new();
    private bool _stairsMoving;
    private bool? _chocksSeen;
    private TimeSpan _sincePeek = PeekInterval;

    // How often Rampside opens GROUND SERVICES on the right CDU just to see whether the chocks are set.
    private static readonly TimeSpan PeekInterval = TimeSpan.FromSeconds(30);

    public PmdgCduEquipment(ProcedureContext context, GroundEquipment equipment)
    {
        Equipment = equipment;
        _link = context.Pmdg;
        _cdu = context.PmdgCdu;
        _items = equipment switch
        {
            GroundEquipment.Stairs => Pmdg737.StairsItems,
            GroundEquipment.GroundPower => new[] { Pmdg737.GroundPowerItem },
            GroundEquipment.Chocks => new[] { Pmdg737.ChocksItem },
            _ => throw new ArgumentException($"The PMDG CDU has no {equipment}.", nameof(equipment)),
        };
        _cdu.Finished += OnCduFinished;
    }

    public string Name => "pmdg-cdu";
    public GroundEquipment Equipment { get; }
    public string? NotReadyReason => Pmdg737.NotReadyReason(_link);

    public string? Request(bool connected, AircraftState state)
    {
        if (NotReadyReason is { } reason)
        {
            return reason;
        }

        // The chocks key toggles and the chocks are visible nowhere but on the CDU: never press it blind without knowing.
        if (Equipment == GroundEquipment.Chocks && !_cdu.CanRead && _chocksSeen is null)
        {
            return "The chocks state is unknown: the right CDU cannot be read (no power). Set or remove them on the CDU.";
        }

        foreach (var item in _items)
        {
            _cdu.Enqueue(item, connected, IsOn(item, state));
        }

        _pending = new PendingRequest(connected, _items.Count);
        return null;
    }

    public ObservedEquipment? Observe(AircraftState state)
    {
        ObservedEquipment? actual = Equipment switch
        {
            GroundEquipment.GroundPower => _link.GetBool(Pmdg737.GroundPowerIndicator) switch
            {
                true => ObservedEquipment.Connected,
                false => ObservedEquipment.Away,
                null => null,
            },
            GroundEquipment.Chocks => ChocksState(),
            _ => StairsState(state),
        };

        if (actual is null || _pending is not { } pending)
        {
            return actual;
        }

        var wanted = pending.Connect ? ObservedEquipment.Connected : ObservedEquipment.Away;
        if (actual == wanted || pending.Elapsed > ArrivalTimeout || pending.Failed)
        {
            _pending = null;
            return actual;
        }

        // Our request is on its way: the vehicle is driving in or out.
        return ObservedEquipment.Moving;
    }

    public void Tick(TimeSpan elapsed)
    {
        _sincePeek += elapsed;
        if (_pending is not null)
        {
            _pending.Elapsed += elapsed;
        }
    }

    /// <summary>
    /// What the aircraft shows of one item from outside, for blind CDU use: GRD POWER AVAILABLE (works cold and dark)
    /// or the stairs truck for that door (e.g. PMDG_Stairs_1L) next to the aircraft.
    /// </summary>
    private bool? IsOn(PmdgServiceItem item, AircraftState state)
    {
        if (item == Pmdg737.GroundPowerItem)
        {
            return _link.GetBool(Pmdg737.GroundPowerIndicator);
        }

        if (item == Pmdg737.ChocksItem)
        {
            return _chocksSeen; // unknown on a dark CDU: then a blind toggle is refused
        }

        var title = $"{Pmdg737.StairsTitlePrefix}_{item.Label[^2..]}";
        return state.NearbyObjects.Any(o =>
            o.Title.Equals(title, StringComparison.OrdinalIgnoreCase) && Distance(state.Latitude, state.Longitude, o) <= StairsSearchRadiusM);
    }

    private void OnCduFinished(PmdgServiceItem item, bool ok)
    {
        if (_pending is not null && _items.Contains(item))
        {
            _pending.Outstanding--;
            _pending.Succeeded += ok ? 1 : 0;
        }
    }

    /// <summary>
    /// Set or removed as last seen on the right CDU. While unknown and the CDU can be read, Rampside opens
    /// GROUND SERVICES there now and then (without pressing anything) to find out.
    /// </summary>
    private ObservedEquipment? ChocksState()
    {
        if (_cdu.ReadState(Pmdg737.ChocksItem) is { } seen)
        {
            _chocksSeen = seen;
        }
        else if (_chocksSeen is null && _pending is null && _sincePeek >= PeekInterval && _cdu.CanRead)
        {
            _sincePeek = TimeSpan.Zero;
            _cdu.Peek(Pmdg737.ChocksItem);
        }

        return _chocksSeen switch
        {
            true => ObservedEquipment.Connected,
            false => ObservedEquipment.Away,
            null => null,
        };
    }

    /// <summary>
    /// Connected while PMDG stairs stand still next to the aircraft, Moving while one of them drives.
    /// The nearby list is refreshed every few seconds, so movement is measured between two refreshes.
    /// </summary>
    private ObservedEquipment StairsState(AircraftState state)
    {
        if (!ReferenceEquals(state.NearbyObjects, _lastScan))
        {
            _lastScan = state.NearbyObjects;
            var stairs = state.NearbyObjects
                .Where(o => o.Title.StartsWith(Pmdg737.StairsTitlePrefix, StringComparison.OrdinalIgnoreCase))
                .Where(o => Distance(state.Latitude, state.Longitude, o) <= StairsSearchRadiusM)
                .ToDictionary(o => o.ObjectId);

            // A stairs truck seen for the first time counts as standing still, so stairs already in place
            // are not reported as arriving.
            _stairsMoving = stairs.Values.Any(o =>
                _lastStairs.TryGetValue(o.ObjectId, out var before) && Distance(before.Latitude, before.Longitude, o) > StairsStillM);
            _lastStairs = stairs;
        }

        return _lastStairs.Count == 0 ? ObservedEquipment.Away
            : _stairsMoving ? ObservedEquipment.Moving
            : ObservedEquipment.Connected;
    }

    private static double Distance(double latitude, double longitude, NearbyObject o) =>
        Geo.ToLocal(latitude, longitude, o.Latitude, o.Longitude).Length;

    private sealed class PendingRequest
    {
        public PendingRequest(bool connect, int items)
        {
            Connect = connect;
            Outstanding = items;
        }

        public bool Connect { get; }
        public TimeSpan Elapsed { get; set; }
        public int Outstanding { get; set; }
        public int Succeeded { get; set; }

        /// <summary>Every CDU request failed, so nothing is coming.</summary>
        public bool Failed => Outstanding <= 0 && Succeeded == 0;
    }
}
