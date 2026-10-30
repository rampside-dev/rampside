using Rampside.Core.Aircraft.Pmdg;
using Rampside.Core.Services;

namespace Rampside.Core.Tests;

/// <summary>PMDG wheel chocks: SET / REMOVED at R6 on GROUND SERVICES, the selected option in the large font.</summary>
public class PmdgChocksTests
{
    private static TestRig CreateRig(FakePmdgLink link) => new(TestRig.FastProfile(), pmdg: link);

    [Fact]
    public void Chocks_state_is_looked_up_on_the_right_cdu_without_pressing_r6()
    {
        var link = new FakePmdgLink { CduPage = "LEGS" };
        var rig = CreateRig(link);
        Assert.Equal(ConnectableService.UnknownDetail, rig.Orchestrator.Chocks.Detail);

        rig.Run(10);

        Assert.Equal(ServiceStatus.Active, rig.Orchestrator.Chocks.Status);
        Assert.Equal("Set", rig.Orchestrator.Chocks.Detail);
        Assert.True(link.ChocksSet);
        Assert.DoesNotContain("EVT_CDU_R_R6", link.SentEvents);
        Assert.DoesNotContain(link.SentEvents, e => e.StartsWith("EVT_CDU_L_", StringComparison.Ordinal));
    }

    [Fact]
    public void Chocks_are_removed_and_set_again_with_r6()
    {
        var link = new FakePmdgLink { CduPage = "LEGS" };
        var rig = CreateRig(link);
        rig.Run(10);

        var remove = rig.Orchestrator.ToggleChocks();
        rig.Run(10);
        Assert.True(remove.Accepted, remove.Message);
        Assert.False(link.ChocksSet);
        Assert.Equal("Removed", rig.Orchestrator.Chocks.Detail);

        link.CduPage = "LEGS"; // the pilot used the right CDU meanwhile
        var set = rig.Orchestrator.ToggleChocks();
        rig.Run(10);
        Assert.True(set.Accepted, set.Message);
        Assert.True(link.ChocksSet);
        Assert.Equal("Set", rig.Orchestrator.Chocks.Detail);
        Assert.Equal(2, link.SentEvents.Count(e => e == "EVT_CDU_R_R6"));
    }

    [Fact]
    public void Pushback_waits_until_the_chocks_are_removed()
    {
        var link = new FakePmdgLink { CduPage = "SERVICES 1" };
        var rig = CreateRig(link);
        rig.Run(2);

        var result = rig.Orchestrator.RequestPushback();

        Assert.False(result.Accepted);
        Assert.Contains("chocks", result.Message);
    }

    [Fact]
    public void Chocks_are_not_toggled_blind_while_their_state_is_unknown()
    {
        // Cold and dark: the CDU cannot be read, and nothing else shows the chocks.
        var link = new FakePmdgLink { CduReadable = false };
        var rig = CreateRig(link);

        var result = rig.Orchestrator.ToggleChocks();
        rig.Run(15);

        Assert.False(result.Accepted);
        Assert.Contains("unknown", result.Message);
        Assert.Empty(link.SentEvents);
        Assert.True(link.ChocksSet);
    }

    [Fact]
    public void Aircraft_without_a_chocks_procedure_do_not_show_chocks()
    {
        var rig = new TestRig();

        Assert.DoesNotContain(rig.Orchestrator.Chocks, rig.Orchestrator.Services);
        Assert.False(rig.Orchestrator.ToggleChocks().Accepted);
    }
}
