using Rampside.Core.Profiles;
using Rampside.Core.Services;
using Rampside.Core.Simulation;

namespace Rampside.Core.Tests;

public class PushbackPlannerTests
{
    private static readonly AircraftState Start = FakeSimBridge.CreateDefaultState(); // heading 250

    [Theory]
    [InlineData(0, CompassDirection.North)]
    [InlineData(22, CompassDirection.North)]
    [InlineData(23, CompassDirection.NorthEast)]
    [InlineData(160, CompassDirection.South)]
    [InlineData(315, CompassDirection.NorthWest)]
    [InlineData(340, CompassDirection.North)]
    [InlineData(-90, CompassDirection.West)]
    public void Compass_from_heading(double heading, CompassDirection expected)
    {
        Assert.Equal(expected, Compass.FromHeading(heading));
    }

    [Theory]
    [InlineData("nw")]
    [InlineData("NorthWest")]
    [InlineData("north-west")]
    [InlineData("north west")]
    public void Compass_parses_common_spellings(string text)
    {
        Assert.True(Compass.TryParse(text, out var direction));
        Assert.Equal(CompassDirection.NorthWest, direction);
    }

    [Fact]
    public void Compass_rejects_other_words()
    {
        Assert.False(Compass.TryParse("left", out _));
    }

    [Fact]
    public void Taxiway_at_right_angles_gives_both_directions()
    {
        var options = PushbackPlanner.Plan(Start, FakeSimBridge.DemoTaxiways(), new PushbackConfig());

        Assert.Equal(2, options.Count);
        Assert.Contains(options, o => o.Facing == CompassDirection.North && Math.Abs(Geo.HeadingDifference(o.FinalHeadingDeg, 340)) < 0.5);
        Assert.Contains(options, o => o.Facing == CompassDirection.South && Math.Abs(Geo.HeadingDifference(o.FinalHeadingDeg, 160)) < 0.5);
        Assert.All(options, o => Assert.Equal("facing " + Compass.Name(o.Facing) + " on N", o.Label));
    }

    [Fact]
    public void Path_ends_on_the_taxi_line()
    {
        var config = new PushbackConfig();
        var option = PushbackPlanner.Plan(Start, FakeSimBridge.DemoTaxiways(), config).First(o => o.Facing == CompassDirection.North);

        var end = option.Path[^1];
        Assert.True(DistanceFromDemoLine(end) < 0.01);

        // Straight back, 90° arc (radius 20), then 10 m along the line.
        var expectedLength = (50 - 20) + Math.PI / 2 * 20 + 10;
        Assert.Equal(expectedLength, option.LengthMeters, 0);
    }

    [Fact]
    public void Close_taxiway_shrinks_the_turn()
    {
        var taxiways = FakeSimBridge.DemoTaxiways(distanceMeters: 12);
        var options = PushbackPlanner.Plan(Start, taxiways, new PushbackConfig());

        Assert.Equal(2, options.Count);
    }

    [Fact]
    public void Taxiway_closer_than_the_tightest_turn_is_not_offered()
    {
        // Tightest turn for the default profile is about 9 m.
        var options = PushbackPlanner.Plan(Start, FakeSimBridge.DemoTaxiways(distanceMeters: 8.5), new PushbackConfig());

        Assert.Empty(options);
    }

    [Fact]
    public void Angled_taxiway_offers_a_short_and_a_long_turn()
    {
        var options = PushbackPlanner.Plan(Start, FakeSimBridge.DemoTaxiways(angleDeg: 45), new PushbackConfig());

        Assert.Equal(2, options.Count);
        // The line runs 115°/295°; from heading 250 that is a 45° and a 135° turn.
        Assert.Contains(options, o => o.Facing == CompassDirection.NorthWest);
        Assert.Contains(options, o => o.Facing == CompassDirection.SouthEast);
    }

    [Fact]
    public void Nothing_behind_the_aircraft_gives_no_options()
    {
        var turnedAway = Start with { HeadingTrueDeg = 70 }; // the taxiway is now in front
        var options = PushbackPlanner.Plan(turnedAway, FakeSimBridge.DemoTaxiways(), new PushbackConfig());

        Assert.Empty(options);
    }

    [Fact]
    public void Service_roads_are_ignored()
    {
        var demo = FakeSimBridge.DemoTaxiways();
        var roads = demo with { Paths = demo.Paths.Select(p => p with { Type = TaxiPathType.Vehicle }).ToArray() };

        Assert.Empty(PushbackPlanner.Plan(Start, roads, new PushbackConfig()));
    }

    [Theory]
    [InlineData(CompassDirection.North, 340)]
    [InlineData(CompassDirection.South, 160)]
    public void Planned_pushback_ends_on_the_line_facing_the_chosen_direction(CompassDirection facing, double expectedHeading)
    {
        var rig = new TestRig(state: Start with { Stand = FakeSimBridge.DemoStand });

        Assert.True(rig.Orchestrator.RequestPushback(facing).Accepted);
        rig.Run(6);
        rig.SetParkingBrake(false);
        RunUntil(rig, () => rig.Orchestrator.Pushback.Detail.Contains("set parking brake"), 300);

        var end = rig.Sim.State;
        Assert.InRange(Math.Abs(Geo.HeadingDifference(end.HeadingTrueDeg, expectedHeading)), 0, 3);
        Assert.InRange(DistanceFromDemoLine(Geo.ToLocal(Start.Latitude, Start.Longitude, end.Latitude, end.Longitude)), 0, 1.0);

        rig.SetParkingBrake(true);
        rig.Run(6);
        Assert.Equal(ServiceStatus.Completed, rig.Orchestrator.Pushback.Status);
        Assert.False(rig.Sim.GroundMotionActive);
    }

    [Theory]
    [InlineData(15, 90)]
    [InlineData(50, 45)]
    [InlineData(30, 60)]
    public void Planned_pushback_follows_tight_and_angled_turns(double distance, double angle)
    {
        var taxiways = FakeSimBridge.DemoTaxiways(distanceMeters: distance, angleDeg: angle);
        foreach (var option in PushbackPlanner.Plan(Start, taxiways, new PushbackConfig()))
        {
            var rig = new TestRig(state: Start with { Stand = FakeSimBridge.DemoStand with { Taxiways = taxiways } });
            Assert.True(rig.Orchestrator.RequestPushback(option.Facing).Accepted);
            rig.Run(6);
            rig.SetParkingBrake(false);
            RunUntil(rig, () => rig.Orchestrator.Pushback.Detail.Contains("set parking brake"), 300);

            var end = rig.Sim.State;
            var local = Geo.ToLocal(Start.Latitude, Start.Longitude, end.Latitude, end.Longitude);
            var linePoint = Vec2.FromHeading(Start.HeadingTrueDeg + 180) * distance;
            var offLine = Math.Abs(Vec2.Cross(Vec2.FromHeading(Start.HeadingTrueDeg + 180 + angle), local - linePoint));
            Assert.InRange(Math.Abs(Geo.HeadingDifference(end.HeadingTrueDeg, option.FinalHeadingDeg)), 0, 5);
            Assert.InRange(offLine, 0, 1.5);
        }
    }

    [Fact]
    public void Direction_that_is_not_offered_is_rejected()
    {
        var rig = new TestRig(state: Start with { Stand = FakeSimBridge.DemoStand });

        var result = rig.Orchestrator.RequestPushback(CompassDirection.East);

        Assert.False(result.Accepted);
        Assert.Contains("facing North on N", result.Message);
    }

    [Fact]
    public void Manual_steering_takes_over_a_planned_pushback()
    {
        var rig = new TestRig(state: Start with { Stand = FakeSimBridge.DemoStand });
        rig.Orchestrator.RequestPushback(CompassDirection.North);
        rig.Run(6);
        rig.SetParkingBrake(false);
        rig.Run(5);
        Assert.NotNull(rig.Orchestrator.Pushback.Plan);

        rig.Orchestrator.SteerPushback(PushbackSteering.Straight);

        Assert.Null(rig.Orchestrator.Pushback.Plan);
        Assert.Contains("manual", rig.Orchestrator.Pushback.Detail);
    }

    /// <summary>Distance from the demo taxiway: 50 m behind the start position, running 340°/160°.</summary>
    private static double DistanceFromDemoLine(Vec2 local)
    {
        var linePoint = Vec2.FromHeading(Start.HeadingTrueDeg + 180) * 50;
        return Math.Abs(Vec2.Cross(Vec2.FromHeading(340), local - linePoint));
    }

    private static void RunUntil(TestRig rig, Func<bool> condition, double maxSeconds)
    {
        for (var t = 0; t < maxSeconds && !condition(); t++)
        {
            rig.Run(1);
        }

        Assert.True(condition(), "condition not reached in time");
    }
}
