using System.Globalization;
using Rampside.Core.Gsx;
using Rampside.Core.Services;
using Rampside.Core.Simulation;

namespace Rampside.Core.Tests;

/// <summary>
/// GSX profiles, with a made-up stand around the default fake aircraft (heading 250°):
/// straight back 35 m, a right-hand 90° turn of 30 m radius, 20 m on with the nose at 340°.
/// </summary>
public class GsxProfileTests
{
    private static readonly AircraftState Start = FakeSimBridge.CreateDefaultState();

    private static readonly (double Lat, double Lon) Turn = Geo.Offset(Start.Latitude, Start.Longitude, 70, 35, 0);
    private static readonly (double Lat, double Lon) TurnEnd = At(Turn, (70, 30), (160, 30));
    private static readonly (double Lat, double Lon) Final = At(TurnEnd, (160, 20));

    /// <summary>Push and pull: after the turn (nose 340°) the tug tows the aircraft forwards, turning right to 70°.</summary>
    private static readonly (double Lat, double Lon) Towed = At(TurnEnd, (340, 10), (25, 15), (70, 10));

    [Fact]
    public void Python_literal_reads_gsx_values()
    {
        var value = PythonLiteral.Parse("[{'wws': 2, 'pos': (50.1, -8.5, -96.5), 'label': u'Area \\'1\\'', 'snap': False, 'approach': [None, (1.0, 2e-1)]}]");

        var item = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsType<List<object?>>(value)));
        Assert.Equal(2.0, item["wws"]);
        Assert.Equal(new List<object?> { 50.1, -8.5, -96.5 }, item["pos"]);
        Assert.Equal("Area '1'", item["label"]);
        Assert.Equal(false, item["snap"]);
        Assert.Equal(new List<object?> { null, new List<object?> { 1.0, 0.2 } }, item["approach"]);
    }

    [Theory]
    [InlineData("[1, 2")]
    [InlineData("{'a' 1}")]
    [InlineData("__import__('os')")]
    public void Python_literal_rejects_anything_else(string text)
    {
        Assert.Throws<FormatException>(() => PythonLiteral.Parse(text));
    }

    [Theory]
    [InlineData("gate 121", "Gate 121")]
    [InlineData("gate a 11", "Gate A11")]
    [InlineData("parking 701", "Parking 701")]
    public void Section_names_become_stand_names(string section, string expected)
    {
        Assert.Equal(expected, GsxProfile.StandName(section));
    }

    [Fact]
    public void Profile_reads_stands_and_all_kinds_of_pushback_targets()
    {
        var problems = new List<string>();
        var profile = GsxProfile.Parse("test", ProfileText(), problems);

        Assert.Empty(problems);
        var stand = Assert.Single(profile.Stands); // general, jetway heights and de-ice pad are no stands
        Assert.Equal("gate 121", stand.Section);
        Assert.Equal("Gate 121", stand.Name);
        Assert.Equal(36.0, stand.MaxWingspanMeters);
        Assert.True(stand.HasJetway);
        Assert.NotNull(stand.ParkingPosition);

        // pushback = 1: only the left target; the "DO NOT USE" entry without a position is dropped.
        Assert.Equal(new[] { "Facing North - N1", "Facing North - N1 Blue" }, stand.PushbackTargets.Select(t => t.Label));
        var left = stand.PushbackTargets[0];
        Assert.Equal(new double?[] { 250, -20 }, left.Waypoints.Select(w => w.HeadingDeg)); // as written, not normalised
        Assert.Equal(2, left.Wingwalkers);
        var extra = stand.PushbackTargets[1];
        Assert.Null(extra.Waypoints[1].HeadingDeg); // approach point given without heading
        Assert.Equal(45.0, extra.StartEnginesPercent);
    }

    [Fact]
    public void Broken_stand_is_skipped_and_reported()
    {
        var problems = new List<string>();
        var profile = GsxProfile.Parse("test", ProfileText() + "\n[gate 9]\nparkingsystem_stopposition = 50.0\npushbackaddpos = []\n", problems);

        Assert.Single(profile.Stands);
        Assert.Contains(problems, p => p.Contains("gate 9"));
    }

    [Fact]
    public void Stand_on_the_spot_is_matched_whatever_its_name()
    {
        var profile = GsxProfile.Parse("test", ProfileText());
        var onSpot = Spot(Start.Latitude, Start.Longitude, "wrong name is fine");
        var (farLat, farLon) = Geo.Offset(Start.Latitude, Start.Longitude, 0, 10, 0);

        Assert.NotNull(GsxProfileLibrary.Match(profile, onSpot, "Gate 999"));
        Assert.Null(GsxProfileLibrary.Match(profile, Spot(farLat, farLon), "Gate 999"));
    }

    [Theory]
    [InlineData("Facing North - E1 Blue", "E1 Blue", CompassDirection.North)]
    [InlineData("facing south-west: Area 5", "Area 5", CompassDirection.SouthWest)]
    [InlineData("Facing East", "", CompassDirection.East)]
    public void Gsx_label_is_split_into_taxiway_and_direction(string label, string rest, CompassDirection direction)
    {
        Assert.Equal((rest, (int)direction), GsxPushbackBuilder.SortKey(label));
    }

    [Fact]
    public void Gsx_labels_sort_by_taxiway_then_direction()
    {
        string[] labels = ["Facing North - E1", "Facing South - E1", "Facing North - E1 Orange", "Facing South - E1 Orange", "Facing North - E1 Blue", "Facing South - E1 Blue", "Remote pushback"];

        var sorted = labels
            .OrderBy(l => GsxPushbackBuilder.SortKey(l).Taxiway, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => GsxPushbackBuilder.SortKey(l).Direction);

        Assert.Equal(
            ["Facing North - E1", "Facing South - E1", "Facing North - E1 Blue", "Facing South - E1 Blue", "Facing North - E1 Orange", "Facing South - E1 Orange", "Remote pushback"],
            sorted);
    }

    [Fact]
    public void Stand_with_the_same_name_is_matched_a_few_metres_off_the_spot()
    {
        // Like Simwings EDDM Gate 118: the profile's parking position is 5.7 m off the scenery's spot.
        var profile = GsxProfile.Parse("test", ProfileText());
        var (lat, lon) = Geo.Offset(Start.Latitude, Start.Longitude, 0, 10, 0);

        var match = GsxProfileLibrary.Match(profile, Spot(lat, lon), "Gate 121");

        Assert.Equal("Gate 121", match?.Stand.Name);
        Assert.Equal(10, match!.DistanceMeters!.Value, 0);
        var (farLat, farLon) = Geo.Offset(Start.Latitude, Start.Longitude, 0, 60, 0);
        Assert.Null(GsxProfileLibrary.Match(profile, Spot(farLat, farLon), "Gate 121"));
    }

    [Fact]
    public void Library_finds_the_airports_profiles_only()
    {
        var folder = Directory.CreateTempSubdirectory("rampside-gsx-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "eddm-test.ini"), ProfileText());
            File.WriteAllText(Path.Combine(folder, "EDDMX-other.ini"), ProfileText());
            File.WriteAllText(Path.Combine(folder, "eddm-test.py"), "not a profile");
            var library = new GsxProfileLibrary(folder);

            Assert.Equal(new[] { "eddm-test.ini" }, library.ProfileFiles("EDDM").Select(Path.GetFileName));
            var match = library.Find("EDDM", Spot(Start.Latitude, Start.Longitude), "Gate 121");
            Assert.Equal("eddm-test", match?.ProfileName);
            Assert.Null(library.Find("EDDF", Spot(Start.Latitude, Start.Longitude), "Gate 121"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Built_path_starts_at_the_aircraft_and_ends_at_the_target_without_kinks()
    {
        var options = GsxPushbackBuilder.Build(Start, Match());

        Assert.Equal(2, options.Count);
        foreach (var option in options)
        {
            Assert.Equal(CompassDirection.North, option.Facing);
            Assert.Equal("GSX profile test", option.Source);
            var path = option.Path;
            Assert.Equal(new Vec2(0, 0), path[0]);
            Assert.True((path[^1] - Geo.ToLocal(Start.Latitude, Start.Longitude, Final.Lat, Final.Lon)).Length < 0.01);

            // Leaves straight backwards, then never turns more than a few degrees per metre.
            Assert.InRange(Math.Abs(Geo.HeadingDifference((path[1] - path[0]).HeadingDeg, 70)), 0, 1);
            for (var i = 2; i < path.Count; i++)
            {
                var turn = Geo.HeadingDifference((path[i - 1] - path[i - 2]).HeadingDeg, (path[i] - path[i - 1]).HeadingDeg);
                Assert.True(Math.Abs(turn) <= 5, $"{option.Label}: {turn:F1} deg at point {i} of {path.Count}");
            }
        }
    }

    [Fact]
    public void Targets_that_do_not_start_behind_the_aircraft_are_skipped()
    {
        var skipped = new List<string>();

        // Turned around: the targets now lie ahead of the nose.
        var turned = Start with { HeadingTrueDeg = 70 };
        Assert.Empty(GsxPushbackBuilder.Build(turned, Match(), skipped));
        Assert.Contains(skipped, s => s.Contains("does not start behind"));

        // Parked somewhere else.
        var (lat, lon) = Geo.Offset(Start.Latitude, Start.Longitude, 0, 100, 0);
        Assert.Empty(GsxPushbackBuilder.Build(Start with { Latitude = lat, Longitude = lon }, Match()));
    }

    [Fact]
    public void Push_and_pull_target_becomes_a_push_then_a_forward_tow()
    {
        var notes = new List<string>();

        var option = Assert.Single(GsxPushbackBuilder.Build(Start, PushAndPull(), notes));

        // Pushed to the end of the turn, then towed forwards (nose first) to the target.
        Assert.True((option.Path[^1] - Geo.ToLocal(Start.Latitude, Start.Longitude, TurnEnd.Lat, TurnEnd.Lon)).Length < 0.01);
        var tow = Assert.Single(option.Then!);
        Assert.True(tow.Forward);
        Assert.Equal(option.Path[^1], tow.Path[0]);
        Assert.True((tow.Path[^1] - Geo.ToLocal(Start.Latitude, Start.Longitude, Towed.Lat, Towed.Lon)).Length < 0.01);
        Assert.InRange(option.TowMeters, 29, 36); // a quarter turn over a 29 m chord
        Assert.InRange(Math.Abs(Geo.HeadingDifference(option.FinalHeadingDeg, 70)), 0, 0.5);
        Assert.Equal(CompassDirection.East, option.Facing);
        Assert.Contains(notes, n => n.StartsWith("Towed: push and pull, towed forwards", StringComparison.Ordinal));

        // The tow leaves along the nose (340°) and arrives along the target heading (70°).
        Assert.InRange(Math.Abs(Geo.HeadingDifference((tow.Path[1] - tow.Path[0]).HeadingDeg, 340)), 0, 3);
        Assert.InRange(Math.Abs(Geo.HeadingDifference((tow.Path[^1] - tow.Path[^2]).HeadingDeg, 70)), 0, 3);
    }

    [Fact]
    public void Push_and_pull_is_flown_with_a_stop_between_push_and_tow()
    {
        var rig = new TestRig(state: Start with { Stand = FakeSimBridge.DemoStand }, gsx: new FixedSource(PushAndPull()));
        rig.Run(0.1);

        Assert.True(rig.Orchestrator.RequestPlannedPushback("towed").Accepted);
        rig.Run(6);
        rig.SetParkingBrake(false);
        var details = new HashSet<string>();
        var maxForward = 0.0;
        var minBackward = 0.0;
        var stoppedBetween = false;
        for (var t = 0; t < 600 && !rig.Orchestrator.Pushback.Detail.Contains("set parking brake"); t++)
        {
            rig.Run(0.5);
            var pushback = rig.Orchestrator.Pushback;
            details.Add(pushback.Detail);
            maxForward = Math.Max(maxForward, pushback.SpeedMps);
            minBackward = Math.Min(minBackward, pushback.SpeedMps);
            stoppedBetween |= pushback.Detail.StartsWith("Stopping, then towing forwards", StringComparison.Ordinal) && pushback.SpeedMps == 0;
        }

        Assert.Contains("Towing forwards, Towed", details);
        Assert.True(stoppedBetween);
        Assert.True(minBackward < -1 && maxForward > 1, $"speeds {minBackward:F1} .. {maxForward:F1} m/s");
        var end = rig.Sim.State;
        Assert.InRange(StandLocator.DistanceMeters(end.Latitude, end.Longitude, Towed.Lat, Towed.Lon), 0, 1.5);
        Assert.InRange(Math.Abs(Geo.HeadingDifference(end.HeadingTrueDeg, 70)), 0, 6);
    }

    [Fact]
    public void Manual_steering_while_towing_keeps_towing_forwards()
    {
        var rig = new TestRig(state: Start with { Stand = FakeSimBridge.DemoStand }, gsx: new FixedSource(PushAndPull()));
        rig.Run(0.1);
        Assert.True(rig.Orchestrator.RequestPlannedPushback("towed").Accepted);
        rig.Run(6);
        rig.SetParkingBrake(false);
        for (var t = 0; t < 600 && !rig.Orchestrator.Pushback.Detail.StartsWith("Towing forwards", StringComparison.Ordinal); t++)
        {
            rig.Run(0.5);
        }

        rig.Orchestrator.SteerPushback(PushbackSteering.TailLeft);
        var heading = rig.Sim.State.HeadingTrueDeg;
        rig.Run(4);

        var pushback = rig.Orchestrator.Pushback;
        Assert.Equal("Towing forwards, manual steering", pushback.Detail);
        Assert.True(pushback.SpeedMps > 0);
        Assert.True(pushback.SteeringDeg > 0); // tail left when towing: nose wheel right
        Assert.True(Geo.HeadingDifference(heading, rig.Sim.State.HeadingTrueDeg) > 0); // nose turns right, tail swings left
    }

    private static GsxStandMatch PushAndPull()
    {
        var target = new GsxPushbackTarget(
            "Towed",
            new[] { new GsxPose(Turn.Lat, Turn.Lon, 250), new GsxPose(TurnEnd.Lat, TurnEnd.Lon, 340) },
            new GsxPose(Towed.Lat, Towed.Lon, 70));
        return new GsxStandMatch("test", Match().Stand with { PushbackTargets = new[] { target } }, 0);
    }

    [Fact]
    public void Gsx_targets_replace_the_planned_options_and_are_flown_to_the_end()
    {
        var rig = new TestRig(state: Start with { Stand = FakeSimBridge.DemoStand }, gsx: new FixedSource(Match()));
        var messages = new List<string>();
        rig.Orchestrator.Message += (_, m) => messages.Add(m);
        rig.Run(0.1);

        var options = rig.Orchestrator.PushbackOptions();
        Assert.Equal(new[] { "Facing North - N1", "Facing North - N1 Blue" }, options.Select(o => o.Label));
        Assert.Contains(messages, m => m.Contains("2 of 2 pushback targets usable"));

        Assert.True(rig.Orchestrator.RequestPlannedPushback("facing north - n1").Accepted);
        rig.Run(6);
        rig.SetParkingBrake(false);
        for (var t = 0; t < 300 && !rig.Orchestrator.Pushback.Detail.Contains("set parking brake"); t++)
        {
            rig.Run(1);
        }

        var end = rig.Sim.State;
        Assert.InRange(StandLocator.DistanceMeters(end.Latitude, end.Longitude, Final.Lat, Final.Lon), 0, 1.5);
        Assert.InRange(Math.Abs(Geo.HeadingDifference(end.HeadingTrueDeg, 340)), 0, 5);
    }

    [Fact]
    public void Unknown_gsx_label_is_rejected_with_the_options()
    {
        var rig = new TestRig(state: Start with { Stand = FakeSimBridge.DemoStand }, gsx: new FixedSource(Match()));
        rig.Run(0.1);

        var result = rig.Orchestrator.RequestPlannedPushback("Facing South");

        Assert.False(result.Accepted);
        Assert.Contains("Facing North - N1 Blue", result.Message);
    }

    private static GsxStandMatch Match() => new("test", GsxProfile.Parse("test", ProfileText()).Stands[0], 0.5);

    private static ParkingSpot Spot(double lat, double lon, string _ = "") => new(0, 10, 121, 0, Start.HeadingTrueDeg, 20, lat, lon);

    private static (double Lat, double Lon) At((double Lat, double Lon) from, params (double Heading, double Meters)[] legs)
    {
        foreach (var (heading, meters) in legs)
        {
            from = Geo.Offset(from.Lat, from.Lon, heading, meters, 0);
        }

        return from;
    }

    private static string P((double Lat, double Lon) p, double heading) =>
        string.Create(CultureInfo.InvariantCulture, $"{p.Lat:R} {p.Lon:R} {heading:R}");

    private static string T((double Lat, double Lon) p, double? heading = null) => heading is { } h
        ? string.Create(CultureInfo.InvariantCulture, $"({p.Lat:R}, {p.Lon:R}, {h:R})")
        : string.Create(CultureInfo.InvariantCulture, $"({p.Lat:R}, {p.Lon:R})");

    /// <summary>A small profile in the layout GSX writes, with made-up values.</summary>
    private static string ProfileText()
    {
        var stand = (Start.Latitude, Start.Longitude);
        var stop = Geo.Offset(Start.Latitude, Start.Longitude, Start.HeadingTrueDeg, 1, 0);
        return $"""
            [general]
            creator = Rampside tests
            version = 1.0

            [jetway_rootfloor_heights]
            test_jetway_001 = 5.1

            [gate 121]
            hasjetway = 1
            pushback = 1
            pushbacktype = 0
            pushbackaddpos = [{"{"}'wws': 2, 'startEngines': 45.0, 'pos': {T(Final, -20.0)}, 'label': u'Facing North - N1 Blue', 'snap': False, 'approach': [{T(Turn, 250.0)}, {T(TurnEnd)}, None, None]{"}"}, {"{"}'wws': 2, 'startEngines': 45.0, 'pos': None, 'label': u'DO NOT USE - ', 'snap': False, 'approach': [None, None]{"}"}]
            pushbacklabels = Facing North - N1 |Facing South - N1
            maxwingspan = 36.0
            this_parking_pos = {P(stand, Start.HeadingTrueDeg)}
            parkingsystem_stopposition = {P(stop, Start.HeadingTrueDeg)}
            pushbackleftpos = {P(Final, 340)}
            pushbackleftapproachpos = {P(Turn, 250)}
            pushbackleftapproachpos2 = {P(TurnEnd, -20)}
            wingwalkersleftpushback = 2
            startenginesleftpushback = 0.0

            [deice pad 1 -max code f-]
            uiname = DeIce Pad 1 (max code F)
            is_deicearea = 1
            this_parking_pos = {P(Final, 0)}
            parkingsystem_stopposition = {P(Final, 0)}
            """;
    }

    private sealed class FixedSource(GsxStandMatch match) : IGsxStandSource
    {
        public GsxStandMatch? Find(string airportIcao, ParkingSpot? spot, string standName, GsxAircraft? aircraft = null) => match;
    }
}
