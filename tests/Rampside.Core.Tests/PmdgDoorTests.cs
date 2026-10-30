using Rampside.Core.Aircraft;
using Rampside.Core.Aircraft.Pmdg;
using Rampside.Core.Profiles;
using Rampside.Core.Services;

namespace Rampside.Core.Tests;

public class PmdgDoorTests
{
    private static TestRig CreateRig(FakePmdgLink? link = null) =>
        new(TestRig.FastProfile(), pmdg: link ?? new FakePmdgLink(TimeSpan.FromSeconds(2)));

    [Fact]
    public void Door_command_opens_the_door_through_the_pmdg_event()
    {
        var rig = CreateRig();

        var result = rig.Orchestrator.SetDoor(DoorId.ForwardLeft, open: true);
        rig.Run(3);

        Assert.True(result.Accepted, result.Message);
        Assert.Equal(new[] { "EVT_DOOR_FWD_L" }, rig.Pmdg!.DoorEvents);
        Assert.Equal(DoorState.Open, rig.Orchestrator.Adapter.GetDoorState(DoorId.ForwardLeft));
    }

    [Fact]
    public void Toggle_event_is_not_sent_twice_while_the_door_moves()
    {
        var rig = CreateRig();

        rig.Orchestrator.SetDoor(DoorId.CargoAft, open: true);
        rig.Run(1);
        rig.Orchestrator.SetDoor(DoorId.CargoAft, open: true);
        rig.Run(2);

        Assert.Single(rig.Pmdg!.DoorEvents);
        Assert.Equal(DoorState.Open, rig.Orchestrator.Adapter.GetDoorState(DoorId.CargoAft));
    }

    [Fact]
    public void Door_state_is_unknown_without_dc_power_and_no_event_is_sent()
    {
        var link = new FakePmdgLink();
        link.SetDcPower(false);
        var rig = CreateRig(link);

        var result = rig.Orchestrator.SetDoor(DoorId.ForwardLeft, open: true);

        Assert.False(result.Accepted);
        Assert.Empty(link.DoorEvents);
    }

    [Fact]
    public void Jetway_opens_the_forward_door_and_closes_it_before_leaving()
    {
        var rig = CreateRig();

        rig.Orchestrator.ToggleJetway();
        rig.Run(4);
        Assert.Equal(DoorState.Open, rig.Orchestrator.Adapter.GetDoorState(DoorId.ForwardLeft));

        rig.Orchestrator.ToggleJetway();
        rig.Run(12); // the door was opened moments ago; the PMDG ignores toggles while a door moves
        Assert.Equal(DoorState.Closed, rig.Orchestrator.Adapter.GetDoorState(DoorId.ForwardLeft));
        Assert.Equal(ServiceStatus.Idle, rig.Orchestrator.Jetway.Status);
    }

    [Fact]
    public void Door_shared_with_connected_stairs_stays_open_when_the_jetway_leaves()
    {
        var rig = CreateRig();
        rig.Orchestrator.ToggleJetway();
        rig.Orchestrator.ToggleStairs();
        rig.Run(4);

        rig.Orchestrator.ToggleJetway();
        rig.Run(3);

        Assert.Equal(DoorState.Open, rig.Orchestrator.Adapter.GetDoorState(DoorId.ForwardLeft));
    }

    [Fact]
    public void Cargo_doors_open_during_boarding_and_close_afterwards()
    {
        var rig = CreateRig();
        rig.Orchestrator.ToggleJetway();
        rig.Run(4);

        rig.Orchestrator.RequestBoarding(20);
        rig.Run(5);
        Assert.Equal(DoorState.Open, rig.Orchestrator.Adapter.GetDoorState(DoorId.CargoForward));
        Assert.Equal(DoorState.Open, rig.Orchestrator.Adapter.GetDoorState(DoorId.CargoAft));

        rig.Run(30);
        Assert.Equal(ServiceStatus.Completed, rig.Orchestrator.Passengers.Status);
        Assert.Equal(DoorState.Closed, rig.Orchestrator.Adapter.GetDoorState(DoorId.CargoForward));
    }

    [Fact]
    public void Pushback_is_refused_while_a_door_is_open()
    {
        var rig = CreateRig(new FakePmdgLink(TimeSpan.FromSeconds(2)) { ChocksSet = false }); // only the door is in the way
        rig.Orchestrator.SetDoor(DoorId.AftRight, open: true);
        rig.Run(3);

        var refused = rig.Orchestrator.RequestPushback();
        rig.Orchestrator.CloseAllDoors();
        rig.Run(13);
        var accepted = rig.Orchestrator.RequestPushback();

        Assert.False(refused.Accepted);
        Assert.Contains("aft right", refused.Message);
        Assert.True(accepted.Accepted, accepted.Message);
    }

    [Fact]
    public void Pmdg_adapter_never_writes_sim_fuel_or_payload()
    {
        var rig = CreateRig();
        rig.Orchestrator.ToggleJetway();
        rig.Run(4);

        var refuel = rig.Orchestrator.RequestRefuel(5000);
        var boarding = rig.Orchestrator.RequestBoarding(50);
        rig.Run(60);

        Assert.False(refuel.Accepted);
        Assert.True(boarding.Accepted);
        Assert.Contains("not transferred", boarding.Message);
        Assert.Empty(rig.Sim.PayloadStations);
        Assert.Equal(2000, rig.Sim.State.TotalFuelKg, 3);
    }

    [Fact]
    public void Adapter_is_not_ready_without_pmdg_data()
    {
        var link = new FakePmdgLink { HasData = false };
        var rig = CreateRig(link);
        rig.Orchestrator.ToggleJetway();
        rig.Run(4);

        var result = rig.Orchestrator.RequestBoarding(10);

        Assert.False(result.Accepted);
        Assert.Contains("Fake PMDG", result.Message);
    }

    [Fact]
    public void Pmdg_profile_matches_the_real_aircraft_title()
    {
        var repository = ProfileRepository.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "profiles"));

        var profile = repository.Resolve("737-800 PAX SSW SC");

        Assert.Equal("pmdg-737", profile.Id);
        Assert.Equal(AircraftProfile.Pmdg737Adapter, profile.Adapter);
        Assert.Equal(new[] { DoorId.ForwardLeft, DoorId.AftLeft }, profile.Doors.Stairs);
    }

    [Fact]
    public void Close_right_after_open_waits_until_the_door_has_finished_moving()
    {
        var rig = CreateRig();

        rig.Orchestrator.SetDoor(DoorId.ForwardRight, open: true);
        rig.Run(3);
        var close = rig.Orchestrator.SetDoor(DoorId.ForwardRight, open: false);
        rig.Run(5);
        Assert.Equal(DoorState.Open, rig.Orchestrator.Adapter.GetDoorState(DoorId.ForwardRight));
        Assert.Single(rig.Pmdg!.DoorEvents);

        rig.Run(8);

        Assert.True(close.Accepted, close.Message);
        Assert.Equal(DoorState.Closed, rig.Orchestrator.Adapter.GetDoorState(DoorId.ForwardRight));
        Assert.Equal(2, rig.Pmdg.DoorEvents.Count);
    }

    [Fact]
    public void Lost_door_event_is_sent_again()
    {
        var link = new FakePmdgLink(TimeSpan.FromSeconds(2)) { IgnoreNextDoorEvents = 1 };
        var rig = CreateRig(link);

        rig.Orchestrator.SetDoor(DoorId.AftLeft, open: true);
        rig.Run(5);
        Assert.Equal(DoorState.Closed, rig.Orchestrator.Adapter.GetDoorState(DoorId.AftLeft));

        rig.Run(10);

        Assert.Equal(2, link.DoorEvents.Count);
        Assert.Equal(DoorState.Open, rig.Orchestrator.Adapter.GetDoorState(DoorId.AftLeft));
    }
}
