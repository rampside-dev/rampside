using System.Globalization;
using Rampside.Core.Gsx;
using Rampside.Core.Gsx.Scripting;
using Rampside.Core.Simulation;

namespace Rampside.Core.Tests;

/// <summary>GSX airport scripts (.py): the small Python interpreter and the per-aircraft stop offsets. Scripts are made up.</summary>
public class GsxScriptTests
{
    private static readonly GsxAircraft B738 = GsxAircraft.FromIcaoType("B738")!;
    private static readonly GsxAircraft A388 = GsxAircraft.FromIcaoType("A388")!;

    /// <summary>In the layout GSX scripts use: a shared lookup helper, decorated offset functions and the parkings table.</summary>
    private const string Script = """
        # -- coding: utf-8 --
        msfs_mode = 1
        icao = "xxxx"

        def Lookup(aircraftData, specificTables, genericTable):
            major_id = aircraftData.idMajor
            if major_id in specificTables:
                specific_table, fallback_key = specificTables[major_id]
                result = specific_table.get(aircraftData.idMinor)
                if result is None:
                    result = specific_table.get(fallback_key)
            else:
                result = genericTable.get(major_id, 0)
            return result

        @AlternativeStopPositions
        def offsetPier(aircraftData):
            table = {
                0: 0,
                321: 2,
                380: 15.5,
            }
            table737 = {
                800: 1,
                900: 3,
            }
            global Lookup
            return Distance.fromMeters(Lookup(aircraftData, { 737 : (table737, 800), }, table))

        @AlternativeStopPositions
        def offsetRemote(aircraftData):
            try:
                return Distance.fromMeters({320: -1.5}.get(aircraftData.idMajor))
            except:
                return Distance()

        @AlternativeStopPositions
        def offsetBroken(aircraftData):
            return Distance.fromMeters({380: 4}.get(aircraftData.idMajor))

        def Names(name, priority):
            return CustomizedName("%s|Stand #" % name, priority)

        Pier = Names("Pier A", 1)

        parkings = {
            GATE_A : {
                None : (Pier, offsetPier),
                12 : (Pier, ),
                13L : (CustomizedName(u"Pier A|Gate 13"), offsetBroken, ),
            },
            N_PARKING : {
                None : ( ),
                5 : (Pier, offsetRemote),
            },
        }
        """;

    [Fact]
    public void Interpreter_runs_the_python_the_scripts_use()
    {
        var module = PyModule.Load("""
            d = {None: 'n', 1: 'one', (2, 'b'): 'tuple'}
            a, b = 3, 4.5
            text = "%s-%d|%.1f %%" % ('x', 7.9, 2.26)
            chain = 1 < a <= 3 and not (a == 4)
            pick = 'big' if b > 4 else 'small'
            def safe(key):
                try:
                    return d[key]
                except KeyError:
                    return 'missing'
                finally:
                    pass
            found = safe((2, 'b'))
            missing = safe(99)
            count = len(d) + abs(-2) + max(1, 5, 3)
            """);

        Assert.Equal("n", ((PyDict)module.Get("d")!).Items[PyNone.Value]);
        Assert.Equal("x-7|2.3 %", module.Get("text")); // %d truncates, as in Python
        Assert.Equal(true, module.Get("chain"));
        Assert.Equal("big", module.Get("pick"));
        Assert.Equal("tuple", module.Get("found"));
        Assert.Equal("missing", module.Get("missing"));
        Assert.Equal(10.0, module.Get("count"));
    }

    [Fact]
    public void Statements_python_2_cannot_run_here_are_skipped_and_the_rest_still_loads()
    {
        var problems = new List<string>();
        var module = PyModule.Load("""
            import os
            for i in [1, 2]:
                x = i
            before = 1
            def bad():
                while True:
                    pass
            print "hello"
            after = before + 1
            os_name = os
            """, problems);

        Assert.Equal(2.0, module.Get("after"));
        Assert.Null(module.Get("bad"));
        Assert.Null(module.Get("os_name"));
        Assert.Contains(problems, p => p.Contains("'import' is not supported"));
        Assert.Contains(problems, p => p.Contains("'for' is not supported"));
        Assert.Contains(problems, p => p.Contains("'while' is not supported"));
        Assert.Contains(problems, p => p.Contains("'print' is not supported"));
        Assert.Contains(problems, p => p.Contains("name 'os' is not defined"));
    }

    [Fact]
    public void Runaway_recursion_stops_at_the_limits()
    {
        var problems = new List<string>();
        var module = PyModule.Load("""
            def f(n):
                return f(n + 1) + f(n + 1)
            def g(n):
                try:
                    return g(n + 1)
                except:
                    return 'caught'
            """, problems);

        var error = Assert.ThrowsAny<PyError>(() => module.Call(module.Get("f")!, 0.0));
        Assert.Contains("call depth", error.Message);

        // The script cannot catch the limit and carry on.
        Assert.ThrowsAny<PyError>(() => module.Call(module.Get("g")!, 0.0));
    }

    [Fact]
    public void Offsets_follow_the_parkings_table()
    {
        var problems = new List<string>();
        var offsets = GsxStopOffsets.Parse("xxxx-test", Script, problems);

        Assert.Empty(problems);
        Assert.Equal(new[] { "gate a 12", "gate a 13", "n parking 5" }, offsets.Stands.Order());

        // Group default (None) for GATE_A stands that are not listed.
        Assert.Equal("offsetPier", offsets.FunctionFor("gate a 7")?.Name);
        Assert.Equal(1, offsets.For("gate a 7", B738)!.Meters);
        Assert.Equal(15.5, offsets.For("gate a 7", A388)!.Meters);
        Assert.Equal(3, offsets.For("gate a 7", GsxAircraft.FromIcaoType("B739")!)!.Meters);
        Assert.Equal(1, offsets.For("gate a 7", GsxAircraft.FromIcaoType("B736")!)!.Meters); // falls back to 737/800
        Assert.Equal(0, offsets.For("gate a 7", GsxAircraft.FromIcaoType("A320")!)!.Meters);

        // Listed without a function, and a group whose default has none.
        Assert.Null(offsets.FunctionFor("gate a 12"));
        Assert.Null(offsets.For("n parking 6", B738));

        // try/except in the script: no table entry gives Distance() = 0.
        Assert.Equal(-1.5, offsets.For("n parking 5", GsxAircraft.FromIcaoType("A320")!)!.Meters);
        Assert.Equal(0, offsets.For("n parking 5", B738)!.Meters);
    }

    [Fact]
    public void Failing_offset_function_gives_no_offset_and_says_why()
    {
        var problems = new List<string>();
        var offsets = GsxStopOffsets.Parse("xxxx-test", Script, problems);

        Assert.Equal(4, offsets.For("gate a 13", A388, problems)!.Meters);
        Assert.Null(offsets.For("gate a 13", B738, problems)); // fromMeters(None), as GSX
        Assert.Contains(problems, p => p.Contains("offsetBroken failed for B738"));
    }

    [Theory]
    [InlineData("B738", 737, 800, "ARC-C")]
    [InlineData("b789", 787, 9, "ARC-E")]
    [InlineData("A388", 380, 0, "ARC-F")]
    [InlineData("E190", 190, 0, "ARC-C")]
    public void Icao_types_map_to_gsx_ids(string type, int major, int minor, string group)
    {
        var aircraft = GsxAircraft.FromIcaoType(type);

        Assert.Equal((major, minor, group), (aircraft!.IdMajor, aircraft.IdMinor, aircraft.AircraftGroup));
    }

    [Fact]
    public void Profile_ids_replace_the_table()
    {
        Assert.Null(GsxAircraft.From(new[] { "ZZZZ" }));
        var aircraft = GsxAircraft.From(new[] { "ZZZZ", "B738" }, idMajor: 737, idMinor: 900);

        Assert.Equal((737, 900, "B738"), (aircraft!.IdMajor, aircraft.IdMinor, aircraft.IcaoTypeDesignator));
    }

    [Fact]
    public void Library_moves_the_stop_position_back_by_the_scripts_offset()
    {
        var start = FakeSimBridge.CreateDefaultState();
        var folder = Directory.CreateTempSubdirectory("rampside-gsx-").FullName;
        try
        {
            var stop = (start.Latitude, start.Longitude);
            File.WriteAllText(Path.Combine(folder, "xxxx-test.ini"), $"""
                [gate a 7]
                this_parking_pos = {P(stop, start.HeadingTrueDeg)}
                parkingsystem_stopposition = {P(stop, start.HeadingTrueDeg)}
                """);
            File.WriteAllText(Path.Combine(folder, "xxxx-test.py"), Script);
            var library = new GsxProfileLibrary(folder);
            var spot = new ParkingSpot(0, 10, 7, 1, start.HeadingTrueDeg, 20, start.Latitude, start.Longitude);

            var plain = library.Find("XXXX", spot, "Gate A7");
            Assert.Null(plain?.StopOffset);
            Assert.Equal(plain!.Stand.StopPosition, plain.StopPosition);

            var match = library.Find("XXXX", spot, "Gate A7", A388);
            Assert.Equal("offsetPier", match?.StopOffset?.Function);
            var (forward, right) = Geo.Relative(stop.Latitude, stop.Longitude, start.HeadingTrueDeg, match!.StopPosition.Latitude, match.StopPosition.Longitude);
            Assert.Equal(-15.5, forward, 2);
            Assert.Equal(0, right, 2);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string P((double Lat, double Lon) p, double heading) =>
        string.Create(CultureInfo.InvariantCulture, $"{p.Lat:R} {p.Lon:R} {heading:R}");
}
