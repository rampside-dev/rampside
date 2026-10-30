using Rampside.Core.Aircraft.Pmdg;
using Rampside.Core.Services;
using Rampside.Core.Simulation;

namespace Rampside.Core.Tests;

/// <summary>PMDG stairs and GPU: requested on the right CDU, detected from the sim (stairs trucks) and the GRD POWER AVAILABLE light.</summary>
public class PmdgGroundServiceTests
{
    private static TestRig CreateRig(FakePmdgLink link) => new(TestRig.FastProfile(), pmdg: link);

    [Fact]
    public void Stairs_are_requested_on_the_right_cdu_and_reported_once_in_place()
    {
        var link = new FakePmdgLink(vehicleTime: TimeSpan.FromSeconds(3)) { CduPage = "LEGS" };
        var rig = CreateRig(link);

        var result = rig.Orchestrator.ToggleStairs();
        rig.Run(20);

        Assert.True(result.Accepted, result.Message);
        Assert.Equal(new[] { "STAIRS 1L", "STAIRS 2L" }, link.VehiclesInPlace);
        Assert.Equal(ServiceStatus.Active, rig.Orchestrator.Stairs.Status);
        Assert.Contains(link.SentEvents, e => e.StartsWith("EVT_CDU_R_", StringComparison.Ordinal));
        Assert.DoesNotContain(link.SentEvents, e => e.StartsWith("EVT_CDU_L_", StringComparison.Ordinal));
        Assert.DoesNotContain(SimEvents.ToggleStairs, rig.Sim.TriggeredEvents);
    }

    [Fact]
    public void Ground_power_counts_as_connected_only_when_the_gpu_delivers_power()
    {
        var link = new FakePmdgLink(vehicleTime: TimeSpan.FromSeconds(30));
        var rig = CreateRig(link);

        rig.Orchestrator.ToggleGpu();
        rig.Run(15);
        Assert.Equal(ServiceStatus.Starting, rig.Orchestrator.Gpu.Status);

        rig.Run(25);
        Assert.Equal(ServiceStatus.Active, rig.Orchestrator.Gpu.Status);
        Assert.True(link.GetBool(Pmdg737.GroundPowerIndicator));
        Assert.DoesNotContain(SimEvents.RequestGroundPower, rig.Sim.TriggeredEvents);
    }

    [Fact]
    public void Ground_power_is_released_on_the_cdu()
    {
        var link = new FakePmdgLink(vehicleTime: TimeSpan.FromSeconds(2));
        var rig = CreateRig(link);
        rig.Orchestrator.ToggleGpu();
        rig.Run(15);
        Assert.Equal(ServiceStatus.Active, rig.Orchestrator.Gpu.Status);

        var result = rig.Orchestrator.ToggleGpu();
        rig.Run(10);

        Assert.True(result.Accepted, result.Message);
        Assert.Empty(link.VehiclesInPlace);
        Assert.Equal(ServiceStatus.Idle, rig.Orchestrator.Gpu.Status);
    }

    [Fact]
    public void Stairs_requested_by_the_pilot_are_detected()
    {
        var link = new FakePmdgLink(vehicleTime: TimeSpan.FromSeconds(1)) { CduPage = "SERVICES 3" };
        var rig = CreateRig(link);

        link.SendEvent("EVT_CDU_R_L1");
        rig.Run(5);

        Assert.Equal(ServiceStatus.Active, rig.Orchestrator.Stairs.Status);
    }

    [Fact]
    public void Cold_and_dark_ground_power_is_requested_with_blind_keys_on_the_right_cdu()
    {
        // As seen in the sim: the CDU cannot be read without power, but MENU, R5, R1, L2 still brought the GPU.
        var link = new FakePmdgLink(vehicleTime: TimeSpan.FromSeconds(5)) { CduReadable = false, CduPage = "LEGS" };
        link.SetDcPower(false);
        var rig = CreateRig(link);

        var result = rig.Orchestrator.ToggleGpu();
        rig.Run(15);

        Assert.True(result.Accepted, result.Message);
        Assert.Equal(new[] { "EVT_CDU_R_MENU", "EVT_CDU_R_MENU", "EVT_CDU_R_R5", "EVT_CDU_R_R1", "EVT_CDU_R_L2" }, link.SentEvents);
        Assert.Equal(ServiceStatus.Active, rig.Orchestrator.Gpu.Status);
    }

    [Fact]
    public void A_dark_cdu_is_woken_up_before_the_blind_keys()
    {
        // New cold and dark flight: the right CDU is dark, its first key only wakes it and keys right after are lost.
        // Without the wake-up pause this ended on the DLNK page in the sim.
        var link = new FakePmdgLink(vehicleTime: TimeSpan.FromSeconds(5)) { CduReadable = false, CduDark = true };
        link.SetDcPower(false);
        var rig = CreateRig(link);

        rig.Orchestrator.ToggleGpu();
        rig.Run(25);

        Assert.Equal(new[] { "GROUND POWER" }, link.VehiclesInPlace);
        Assert.Equal(ServiceStatus.Active, rig.Orchestrator.Gpu.Status);
    }

    [Fact]
    public void Blind_request_skips_items_already_in_place()
    {
        // Pressing STAIRS 1L again would send that truck away, so only STAIRS 2L may be pressed.
        var link = new FakePmdgLink(vehicleTime: TimeSpan.FromSeconds(1)) { CduPage = "SERVICES 3" };
        var rig = CreateRig(link);
        link.SendEvent("EVT_CDU_R_L1"); // STAIRS 1L by hand
        rig.Run(3);
        rig.Orchestrator.Stairs.ApplyObserved(ObservedEquipment.Away); // pretend Rampside has not noticed yet
        link.CduReadable = false;
        var before = link.SentEvents.Count;

        var adapter = rig.Orchestrator.Adapter;
        adapter.SetEquipment(Aircraft.GroundEquipment.Stairs, connected: true, rig.Sim.State);
        rig.Run(15);

        Assert.Equal(new[] { "STAIRS 1L", "STAIRS 2L" }, link.VehiclesInPlace);
        Assert.Equal(
            new[] { "EVT_CDU_R_MENU", "EVT_CDU_R_MENU", "EVT_CDU_R_R5", "EVT_CDU_R_R1", "EVT_CDU_R_NEXT_PAGE", "EVT_CDU_R_NEXT_PAGE", "EVT_CDU_R_L2" },
            link.SentEvents.Skip(before));
    }

    [Fact]
    public void Ground_services_lines_are_found_on_the_real_screens()
    {
        // Copied from the PMDG 737-800 in MSFS 2024 (GROUND SERVICES pages 1 and 3).
        var page1 = new[]
        {
            "   AIRPLANE SERVICE 1/4 ", " GROUND POWER TYPE      ", "<GPU CART               ", " GROUND POWER     JETWAY",
            "<REQUEST           INHBT", " AIR START UNIT         ", "STAIRS1L INH            ",
        };
        var page3 = new[]
        {
            " PASSENGER SERVICE  3/4 ", " STAIRS 1L    PAX ENTRY ", "<RELEASE         STAIRS>", " STAIRS 2L        TARGET",
            "<REQUEST    174 PAX     ",
        };

        Assert.Equal(("L2", "<REQUEST"), PmdgCduDriver.FindItem(page1, "GROUND POWER"));
        Assert.Equal(("L1", "<RELEASE"), PmdgCduDriver.FindItem(page3, "STAIRS 1L"));
        Assert.Equal(("L2", "<REQUEST"), PmdgCduDriver.FindItem(page3, "STAIRS 2L"));
        Assert.Equal(("R1", "STAIRS>"), PmdgCduDriver.FindItem(page3, "PAX ENTRY"));
    }
}
