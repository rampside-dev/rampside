using Rampside.Core.Simulation;

namespace Rampside.Core.Aircraft.Pmdg;

/// <summary>
/// Pretends to be a PMDG 737 for tests and <c>--fake-pmdg</c>: door events toggle the door annunciator
/// after a short delay, DC buses are powered unless switched off. The right CDU shows MENU, FS ACTIONS and
/// GROUND SERVICES (texts copied from the real aircraft); its GROUND POWER and STAIRS lines bring the GPU
/// (lights GRD POWER AVAILABLE) and stairs trucks (see <see cref="NearbyObjects"/>) after <see cref="VehicleTime"/>.
/// </summary>
public sealed class FakePmdgLink : IPmdgLink
{
    private readonly object _gate = new();
    private readonly Dictionary<string, bool> _bools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimeSpan> _moving = new(StringComparer.Ordinal);
    private readonly List<string> _sentEvents = new();

    // Label -> time until the vehicle is in place, or null once it is there.
    private readonly Dictionary<string, TimeSpan?> _vehicles = new(StringComparer.Ordinal);
    private string _page = "MENU";
    private IReadOnlyList<NearbyObject> _nearby = Array.Empty<NearbyObject>();
    private string _nearbyKey = string.Empty;

    public FakePmdgLink(TimeSpan? doorMoveTime = null, TimeSpan? vehicleTime = null)
    {
        DoorMoveTime = doorMoveTime ?? TimeSpan.FromSeconds(3);
        VehicleTime = vehicleTime ?? TimeSpan.FromSeconds(5);
        SetDcPower(true);
    }

    public TimeSpan DoorMoveTime { get; }
    public TimeSpan VehicleTime { get; }

    /// <summary>
    /// False to pretend the right CDU cannot be read (cold and dark, or EnableCDUBroadcast.1 missing): like the real
    /// aircraft it then reports an empty screen that is "off", but its keys still work.
    /// </summary>
    public bool CduReadable { get; set; } = true;

    /// <summary>
    /// True for a dark right CDU (new cold and dark flight): the first key only wakes it up, and keys during the
    /// next <see cref="CduWakeUpTime"/> are lost, as seen in the sim. It then shows MENU.
    /// </summary>
    public bool CduDark { get; set; }

    /// <summary>Wheel chocks as shown on GROUND SERVICES (R6 toggles them). Set when the aircraft is loaded.</summary>
    public bool ChocksSet { get; set; } = true;

    public TimeSpan CduWakeUpTime { get; set; } = TimeSpan.FromSeconds(2);

    private TimeSpan _wakingLeft;

    /// <summary>Page on the right CDU: MENU, FS ACTIONS, SERVICES 1..3 or anything else (shown as LEGS).</summary>
    public string CduPage
    {
        get { lock (_gate) return _page; }
        set { lock (_gate) _page = value; }
    }

    /// <summary>Vehicles in place: "GROUND POWER", "STAIRS 1L", "STAIRS 2L".</summary>
    public IReadOnlyList<string> VehiclesInPlace
    {
        get { lock (_gate) return _vehicles.Where(v => v.Value is null).Select(v => v.Key).OrderBy(k => k).ToArray(); }
    }

    /// <summary>Number of upcoming door events to drop, to test lost events.</summary>
    public int IgnoreNextDoorEvents { get; set; }
    public bool HasData { get; set; } = true;
    public string? Problem => HasData ? null : "Fake PMDG link has no data.";
    public string Diagnostics => "Fake PMDG 737 (no header used)";

    public IReadOnlyList<string> SentEvents
    {
        get { lock (_gate) return _sentEvents.ToArray(); }
    }

    /// <summary>Only the door events (the CDU keys Rampside presses on its own, e.g. to look at the chocks, left out).</summary>
    public IReadOnlyList<string> DoorEvents => SentEvents.Where(e => e.StartsWith("EVT_DOOR_", StringComparison.Ordinal)).ToArray();

    public void SetDcPower(bool powered)
    {
        lock (_gate)
        {
            foreach (var bus in Pmdg737.DoorIndicationBuses)
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

    public PmdgCduScreen? GetCdu(int index)
    {
        if (index != 1 || !HasData)
        {
            return null;
        }

        if (!CduReadable)
        {
            return new PmdgCduScreen(Enumerable.Repeat(new string(' ', 24), 14).ToArray(), Powered: false);
        }

        lock (_gate)
        {
            var lines = Screen();
            var flags = Enumerable.Range(0, lines.Length).Select(_ => new byte[24]).ToArray();
            if (_page.StartsWith("SERVICES", StringComparison.Ordinal))
            {
                // "<RETURN     SET REMOVED>": the option not selected is in the small font, as in the aircraft.
                var (from, to) = ChocksSet ? (16, 22) : (12, 14);
                for (var column = from; column <= to; column++)
                {
                    flags[12][column] = PmdgCduScreen.SmallFont;
                }
            }

            return new PmdgCduScreen(lines, Powered: true, Flags: flags);
        }
    }

    /// <summary>PMDG stairs trucks standing at the aircraft, as the sim would list them. Same list instance until they change.</summary>
    public IReadOnlyList<NearbyObject> NearbyObjects(AircraftState aircraft)
    {
        lock (_gate)
        {
            var stairs = _vehicles.Where(v => v.Value is null && v.Key.StartsWith("STAIRS", StringComparison.Ordinal))
                .Select(v => v.Key).OrderBy(k => k).ToArray();
            var key = string.Join(",", stairs);
            if (key != _nearbyKey)
            {
                _nearbyKey = key;
                _nearby = stairs.Select((label, i) =>
                {
                    // Where the real PMDG_Stairs_1L stood: 12 m ahead of and 5.7 m left of the reference point.
                    var forward = label.EndsWith("1L", StringComparison.Ordinal) ? 12 : -10;
                    var (lat, lon) = Geo.Offset(aircraft.Latitude, aircraft.Longitude, aircraft.HeadingTrueDeg, forward, -5.7);
                    return new NearbyObject(1000 + (uint)i, $"PMDG_Stairs_{label[^2..]}", lat, lon, aircraft.HeadingTrueDeg + 97);
                }).ToArray();
            }

            return _nearby;
        }
    }

    public bool SendEvent(string eventName, uint parameter = 0)
    {
        var door = Pmdg737.DefaultDoorMap.FirstOrDefault(d => d.Event == eventName);
        lock (_gate)
        {
            _sentEvents.Add(eventName);
            if (eventName.StartsWith("EVT_CDU_R_", StringComparison.Ordinal))
            {
                PressKey(eventName["EVT_CDU_R_".Length..]);
                return true;
            }

            if (door is not null && IgnoreNextDoorEvents > 0)
            {
                IgnoreNextDoorEvents--;
            }
            else if (door is not null && !_moving.ContainsKey(door.Indicator))
            {
                _moving[door.Indicator] = TimeSpan.Zero;
            }
        }

        return door is not null;
    }

    public void Advance(TimeSpan elapsed)
    {
        lock (_gate)
        {
            _wakingLeft = _wakingLeft > elapsed ? _wakingLeft - elapsed : TimeSpan.Zero;

            foreach (var label in _vehicles.Keys.ToList())
            {
                if (_vehicles[label] is { } left)
                {
                    _vehicles[label] = left - elapsed > TimeSpan.Zero ? left - elapsed : null;
                }
            }

            _bools[Pmdg737.GroundPowerIndicator] = _vehicles.TryGetValue("GROUND POWER", out var gpu) && gpu is null;

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

    private void PressKey(string key)
    {
        if (CduDark)
        {
            CduDark = false;
            _wakingLeft = CduWakeUpTime;
            _page = "MENU";
            return;
        }

        if (_wakingLeft > TimeSpan.Zero)
        {
            return; // still starting up
        }

        switch (_page, key)
        {
            case (var page, "R6") when page.StartsWith("SERVICES", StringComparison.Ordinal):
                ChocksSet = !ChocksSet;
                break;
            case (_, "MENU"):
                _page = "MENU";
                break;
            case ("MENU", "R5"):
                _page = "FS ACTIONS";
                break;
            case ("FS ACTIONS", "R1"):
                _page = "SERVICES 1";
                break;
            case ("SERVICES 1" or "SERVICES 2", "NEXT_PAGE"):
            case ("SERVICES 2" or "SERVICES 3", "PREV_PAGE"):
                _page = $"SERVICES {_page[^1] - '0' + (key == "NEXT_PAGE" ? 1 : -1)}";
                break;
            case ("SERVICES 1", "L2"):
                Toggle("GROUND POWER");
                break;
            case ("SERVICES 3", "L1"):
                Toggle("STAIRS 1L");
                break;
            case ("SERVICES 3", "L2"):
                Toggle("STAIRS 2L");
                break;
        }
    }

    private void Toggle(string label)
    {
        if (!_vehicles.Remove(label))
        {
            _vehicles[label] = VehicleTime;
        }
    }

    private string Verb(string label) => _vehicles.ContainsKey(label) ? "<RELEASE" : "<REQUEST";

    private string[] Screen()
    {
        var lines = _page switch
        {
            "MENU" => new[]
            {
                "          MENU          ", "", "<FMC      <REQ>         ", "", "<DLNK", "", "<DFDAU", "                   PMDG ",
                "                  SETUP>", "", "             FS ACTIONS>", "", "", "",
            },
            "FS ACTIONS" => new[]
            {
                "       FS ACTIONS       ", "                 GROUND ", "<FUEL          SERVICES>", "                  GROUND",
                "<PAYLOAD    MAINTENANCE>", "                   AUTO ", "<DOORS           CRUISE>", "", "<PUSHBACK", "",
                "<CABIN LIGHTS", " RETURN TO", "<MENU", "",
            },
            "SERVICES 1" => new[]
            {
                "   AIRPLANE SERVICE 1/4 ", " GROUND POWER TYPE", "<GPU CART", " GROUND POWER     JETWAY",
                $"{Verb("GROUND POWER"),-19}INHBT", " AIR START UNIT", "<REQUEST", " AIR CONDITIONING UNIT", "<REQUEST", "", "",
                "           WHEEL CHOCKS ", "<RETURN     SET REMOVED>", "",
            },
            "SERVICES 2" => new[]
            {
                "   AIRPLANE SERVICE 2/4 ", " POTABLE WATER", "<REQUEST", " LAVATORY", "<REQUEST", " MAINTENANCE VAN", "<REQUEST",
                " FUEL TRUCK       TARGET", "<REQUEST 11367 KGS", "                FUELING ", "                  START>",
                "           WHEEL CHOCKS ", "<RETURN     SET REMOVED>", "",
            },
            "SERVICES 3" => new[]
            {
                " PASSENGER SERVICE  3/4 ", " STAIRS 1L    PAX ENTRY ", $"{Verb("STAIRS 1L"),-17}STAIRS>", " STAIRS 2L        TARGET",
                $"{Verb("STAIRS 2L"),-12}174 PAX", " AIRSTAIRS      DEPLANE ", "STAIRS INHBT      START>", " PAX BUS     FWD GALLEY ",
                "<REQUEST        REQUEST>", " CABIN CLNG  AFT GALLEY ", "<REQUEST        REQUEST>", "           WHEEL CHOCKS ",
                "<RETURN     SET REMOVED>", "",
            },
            _ => new[] { "      ACT RTE 1 LEGS    ", "", "", "", "", "", "", "", "", "", "", "", "", "" },
        };

        return lines.Select(l => l.PadRight(24)[..24]).ToArray();
    }

    private static string Key(string field, int index) => index == 0 && !field.StartsWith("ELEC_BusPowered", StringComparison.Ordinal)
        ? field
        : $"{field}[{index}]";
}
