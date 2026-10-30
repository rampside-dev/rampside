using Rampside.Core.Simulation;

namespace Rampside.Core.Tests;

public class RepositionTests
{
    private static TestRig Rig() => new(state: FakeSimBridge.CreateDefaultState() with
    {
        Stand = FakeSimBridge.DemoStand,
        AirportIcao = "EDDM",
        AirportParkings = FakeSimBridge.DemoParkings(),
    });

    [Fact]
    public void Moves_the_aircraft_onto_the_chosen_stand()
    {
        var rig = Rig();
        var target = FakeSimBridge.DemoParkings()[6]; // "Parking 5", turned 90°

        var result = rig.Orchestrator.RepositionTo(target.Index);

        Assert.True(result.Accepted, result.Message);
        Assert.Contains("EDDM Parking 5", result.Message);
        var state = rig.Sim.State;
        Assert.True(StandLocator.DistanceMeters(state.Latitude, state.Longitude, target.Latitude, target.Longitude) < 0.1);
        Assert.Equal(Geo.NormalizeHeading(target.HeadingDeg), state.HeadingTrueDeg, 3);
        Assert.Equal("Parking 5", state.Stand?.Name);
    }

    [Fact]
    public void Unknown_stand_is_rejected()
    {
        Assert.False(Rig().Orchestrator.RepositionTo(999).Accepted);
    }

    [Fact]
    public void Not_while_moving()
    {
        var rig = Rig();
        rig.Sim.Update(s => s with { GroundSpeedKnots = 5 });

        Assert.False(rig.Orchestrator.RepositionTo(1).Accepted);
    }

    [Fact]
    public void Not_while_the_jetway_is_attached()
    {
        var rig = Rig();
        rig.Orchestrator.ToggleJetway();
        rig.Run(15);

        var result = rig.Orchestrator.RepositionTo(1);

        Assert.False(result.Accepted);
    }

    [Fact]
    public void Planned_pushback_works_from_the_new_stand()
    {
        var rig = Rig();
        rig.Orchestrator.RepositionTo(3);

        Assert.Equal(2, rig.Orchestrator.PushbackOptions().Count);
    }
}
