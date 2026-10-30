using Rampside.Core.Aircraft;
using Rampside.Core.Aircraft.Pmdg;
using Rampside.Core.Services;
using Rampside.Core.Simulation;

namespace Rampside.Core.Tests;

/// <summary>Asking for the jetway while the stairs are connected (or the other way round) swaps them in order.</summary>
public class CabinAccessSwapTests
{
    /// <summary>PMDG at a gate with a jetway, its stairs at L1 already in place and the L1 door open.</summary>
    private static (TestRig Rig, FakePmdgLink Link) AtGateWithStairs()
    {
        var link = new FakePmdgLink(doorMoveTime: TimeSpan.FromSeconds(2), vehicleTime: TimeSpan.FromSeconds(3)) { CduPage = "SERVICES 3" };
        var state = FakeSimBridge.CreateDefaultState() with { Stand = FakeSimBridge.DemoStand with { Jetway = new JetwayInfo(0, false, 0) } };
        var rig = new TestRig(TestRig.FastProfile(), state, link);

        link.SendEvent("EVT_CDU_R_L1"); // stairs 1L, requested by the pilot
        rig.Run(8);
        Assert.Equal(ServiceStatus.Active, rig.Orchestrator.Stairs.Status);
        Assert.Equal(DoorState.Open, rig.Orchestrator.Adapter.GetDoorState(DoorId.ForwardLeft));
        return (rig, link);
    }

    [Fact]
    public void Jetway_comes_only_after_the_door_is_closed_and_the_stairs_are_gone()
    {
        var (rig, link) = AtGateWithStairs();

        var result = rig.Orchestrator.ToggleJetway();
        Assert.True(result.Accepted, result.Message);
        Assert.StartsWith("Closing forward left", result.Message);

        // Watch the moment the jetway is called.
        (bool StairsGone, DoorState L1)? whenCalled = null;
        for (var t = 0; t < 120 && whenCalled is null; t++)
        {
            rig.Run(0.5);
            if (rig.Sim.TriggeredEvents.Contains(SimEvents.ToggleJetway))
            {
                whenCalled = (link.VehiclesInPlace.Count == 0, rig.Orchestrator.Adapter.GetDoorState(DoorId.ForwardLeft));
            }
        }

        Assert.NotNull(whenCalled);
        Assert.True(whenCalled.Value.StairsGone);
        Assert.Equal(DoorState.Closed, whenCalled.Value.L1);

        rig.Run(20);
        Assert.Equal(ServiceStatus.Active, rig.Orchestrator.Jetway.Status);
        Assert.Equal(ServiceStatus.Idle, rig.Orchestrator.Stairs.Status);
        Assert.Equal(DoorState.Open, rig.Orchestrator.Adapter.GetDoorState(DoorId.ForwardLeft)); // opened again for the jetway
        Assert.False(rig.Orchestrator.IsSwappingCabinAccess);
    }

    [Fact]
    public void Jetway_shows_that_it_waits_for_the_stairs()
    {
        var (rig, _) = AtGateWithStairs();

        rig.Orchestrator.ToggleJetway();
        rig.Run(1);

        Assert.Equal(ServiceStatus.Idle, rig.Orchestrator.Jetway.Status);
        Assert.Equal("Waiting for the stairs to leave", rig.Orchestrator.Jetway.Detail);
        Assert.DoesNotContain(SimEvents.ToggleJetway, rig.Sim.TriggeredEvents);
    }

    [Fact]
    public void Asking_again_cancels_the_swap_before_the_stairs_leave()
    {
        var (rig, link) = AtGateWithStairs();

        rig.Orchestrator.ToggleJetway();
        rig.Run(1);
        var result = rig.Orchestrator.ToggleJetway();
        rig.Run(30);

        Assert.True(result.Accepted);
        Assert.Equal(new[] { "STAIRS 1L" }, link.VehiclesInPlace);
        Assert.Equal(ServiceStatus.Active, rig.Orchestrator.Stairs.Status);
        Assert.Equal("Disconnected", rig.Orchestrator.Jetway.Detail);
        Assert.DoesNotContain(SimEvents.ToggleJetway, rig.Sim.TriggeredEvents);
    }
}
