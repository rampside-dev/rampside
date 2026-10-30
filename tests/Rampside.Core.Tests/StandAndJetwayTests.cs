using Rampside.Core.Aircraft;
using Rampside.Core.Aircraft.Pmdg;
using Rampside.Core.Profiles;
using Rampside.Core.Services;
using Rampside.Core.Simulation;

namespace Rampside.Core.Tests;

public class StandAndJetwayTests
{
    private static AircraftState AtGate(JetwayInfo? jetway) =>
        FakeSimBridge.CreateDefaultState() with { Stand = FakeSimBridge.DemoStand with { Jetway = jetway } };

    [Fact]
    public void Stand_is_the_nearest_spot_whose_circle_contains_the_aircraft()
    {
        var spots = new[]
        {
            new ParkingSpot(156, 10, 121, 0, 83.4, 11, 48.348680, 11.783900),
            new ParkingSpot(116, 10, 120, 0, 83.7, 30, 48.348300, 11.784300),
        };

        var found = StandLocator.FindStand(spots, 48.348633, 11.783834);

        Assert.NotNull(found);
        Assert.Equal(156, found.Value.Spot.Index);
        Assert.Equal("Gate 121", found.Value.Spot.DisplayName);
        Assert.InRange(found.Value.DistanceMeters, 5, 10);
    }

    [Fact]
    public void Aircraft_on_a_taxiway_is_not_on_a_stand()
    {
        var spots = new[] { new ParkingSpot(1, 1, 5, 0, 0, 20, 48.0, 11.0) };

        Assert.Null(StandLocator.FindStand(spots, 48.001, 11.0)); // ~111 m away
    }

    [Theory]
    [InlineData(10, 121u, 0, "Gate 121")]
    [InlineData(1, 7u, 0, "Parking 7")]
    [InlineData(4, 3u, 0, "E Parking 3")]
    [InlineData(12, 5u, 0, "Gate A5")]
    [InlineData(10, 12u, 2, "Gate 12B")]
    public void Stand_names(int code, uint number, int suffix, string expected)
    {
        Assert.Equal(expected, StandNames.Format(code, number, suffix));
    }

    [Fact]
    public void Parking_offset_moves_north_and_east()
    {
        var (lat, lon) = StandLocator.Offset(48.0, 11.0, biasXMeters: 100, biasZMeters: 100);

        Assert.InRange(StandLocator.DistanceMeters(48.0, 11.0, lat, lon), 141, 142);
        Assert.True(lat > 48.0 && lon > 11.0);
    }

    [Fact]
    public void Jetway_follows_the_sim_through_attach_and_detach()
    {
        var rig = new TestRig(state: AtGate(new JetwayInfo(0, false, 0)));

        Assert.True(rig.Orchestrator.ToggleJetway().Accepted);
        rig.Run(5);
        Assert.Equal(ServiceStatus.Starting, rig.Orchestrator.Jetway.Status); // fake needs 10 s, the 1 s timer is ignored
        rig.Run(6);
        Assert.Equal(ServiceStatus.Active, rig.Orchestrator.Jetway.Status);
        Assert.True(rig.Sim.State.Stand!.Jetway!.AttachedToUser);

        Assert.True(rig.Orchestrator.ToggleJetway().Accepted);
        rig.Run(4);
        Assert.Equal(ServiceStatus.Finishing, rig.Orchestrator.Jetway.Status);
        rig.Run(6);
        Assert.Equal(ServiceStatus.Idle, rig.Orchestrator.Jetway.Status);
        Assert.Equal(JetwayStatus.Rest, rig.Sim.State.Stand!.Jetway!.Status);
    }

    [Fact]
    public void Jetway_attached_by_someone_else_is_recognised()
    {
        var rig = new TestRig(state: AtGate(new JetwayInfo(7, true, 0)));

        rig.Run(0.2);

        Assert.Equal(ServiceStatus.Active, rig.Orchestrator.Jetway.Status);
        Assert.False(rig.Orchestrator.RequestPushback().Accepted);
    }

    [Fact]
    public void Jetway_removed_by_someone_else_frees_the_stand()
    {
        var rig = new TestRig(state: AtGate(new JetwayInfo(7, true, 0)));
        rig.Run(0.2);

        rig.Sim.Update(s => s with { Stand = s.Stand! with { Jetway = new JetwayInfo(0, false, 0) } });
        rig.Run(0.2);

        Assert.Equal(ServiceStatus.Idle, rig.Orchestrator.Jetway.Status);
    }

    [Fact]
    public void Stand_without_jetway_refuses_the_jetway()
    {
        var rig = new TestRig(state: AtGate(jetway: null));

        var result = rig.Orchestrator.ToggleJetway();

        Assert.False(result.Accepted);
        Assert.Contains("Gate 121 has no jetway", result.Message);
    }

    [Fact]
    public void Jetway_door_opens_only_after_the_delay()
    {
        var profile = new AircraftProfile
        {
            Doors = new DoorConfig { JetwayOpenDelaySeconds = 8 },
            Jetway = new ConnectableConfig { ConnectSeconds = 1 },
        };
        var link = new FakePmdgLink(TimeSpan.FromSeconds(1));
        var rig = new TestRig(profile, pmdg: link);

        rig.Orchestrator.ToggleJetway();
        rig.Run(5);
        Assert.Empty(link.DoorEvents);

        rig.Run(6);
        Assert.Equal(new[] { "EVT_DOOR_FWD_L" }, link.DoorEvents);
        Assert.Equal(DoorState.Open, rig.Orchestrator.Adapter.GetDoorState(DoorId.ForwardLeft));
    }
}
