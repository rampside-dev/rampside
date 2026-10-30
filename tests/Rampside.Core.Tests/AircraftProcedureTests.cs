using System.Text.Json;
using Rampside.Core.Aircraft;
using Rampside.Core.Aircraft.Pmdg;
using Rampside.Core.Aircraft.Procedures;
using Rampside.Core.Orchestration;
using Rampside.Core.Profiles;
using Rampside.Core.Services;
using Rampside.Core.Simulation;

namespace Rampside.Core.Tests;

/// <summary>Adapters are put together from one procedure per job: family defaults plus profile overrides.</summary>
public class AircraftProcedureTests
{
    private static AircraftProfile Profile(string json) =>
        JsonSerializer.Deserialize<AircraftProfile>(json, ProfileRepository.JsonOptions)!;

    private static (FakeSimBridge Sim, ComposedAircraftAdapter Adapter, GroundServiceOrchestrator Orchestrator) Build(
        AircraftProfile profile, FakePmdgLink? pmdg = null)
    {
        var sim = new FakeSimBridge();
        sim.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        var adapter = AircraftProcedureCatalog.Create(profile, new ProcedureContext(sim, profile, pmdg));
        return (sim, adapter, new GroundServiceOrchestrator(sim, profile, adapter));
    }

    [Fact]
    public void Pmdg_family_uses_sdk_doors_cdu_stairs_and_ground_power_and_leaves_fuel_to_the_aircraft()
    {
        var (_, adapter, _) = Build(Profile("""{ "id": "p", "adapter": "pmdg-737" }"""), new FakePmdgLink());

        Assert.Empty(adapter.Warnings);
        Assert.Equal("PMDG 737", adapter.Name);
        Assert.Equal("doors pmdg-sdk, jetway sim, stairs pmdg-cdu, ground power pmdg-cdu, chocks pmdg-cdu, fuel none, payload none", adapter.Summary);
        Assert.False(adapter.SupportsFuel);
    }

    [Fact]
    public void Default_family_uses_the_sims_ground_services()
    {
        var (sim, adapter, orchestrator) = Build(Profile("""{ "id": "d" }"""));

        orchestrator.ToggleStairs();
        orchestrator.ToggleGpu();

        Assert.Equal("doors none, jetway sim, stairs sim, ground power sim, chocks none, fuel sim, payload sim", adapter.Summary);
        Assert.Equal(new[] { SimEvents.ToggleStairs, SimEvents.RequestGroundPower }, sim.TriggeredEvents);
    }

    [Fact]
    public void A_profile_can_change_single_jobs_of_its_family()
    {
        var link = new FakePmdgLink();
        var (sim, adapter, orchestrator) = Build(Profile("""{ "id": "p", "adapter": "pmdg-737", "procedures": { "stairs": "sim" } }"""), link);

        orchestrator.ToggleStairs();
        adapter.Tick(TimeSpan.FromSeconds(1));

        Assert.Equal("sim", adapter.Procedures.Equipment[GroundEquipment.Stairs].Name);
        Assert.Equal("pmdg-cdu", adapter.Procedures.Equipment[GroundEquipment.GroundPower].Name);
        Assert.Equal(new[] { SimEvents.ToggleStairs }, sim.TriggeredEvents);
        Assert.Empty(link.SentEvents);
    }

    [Fact]
    public void Pmdg_profile_may_not_write_sim_fuel_variables()
    {
        var (_, adapter, _) = Build(Profile("""{ "id": "p", "adapter": "pmdg-737", "procedures": { "fuel": "sim" } }"""), new FakePmdgLink());

        Assert.Equal("none", adapter.Procedures.Fuel.Name);
        Assert.Contains(adapter.Warnings, w => w.Contains("fuel 'sim'"));
    }

    [Fact]
    public void Unknown_procedure_names_fall_back_to_none_and_are_reported()
    {
        var (sim, adapter, orchestrator) = Build(Profile("""{ "id": "x", "procedures": { "groundPower": "gsx-magic" } }"""));
        var messages = new List<string>();
        orchestrator.Message += (_, text) => messages.Add(text);

        orchestrator.Tick(TimeSpan.FromSeconds(0.1));
        var result = orchestrator.ToggleGpu();

        Assert.Contains(messages, m => m.Contains("gsx-magic"));
        Assert.False(result.Accepted);
        Assert.Contains("aircraft's own menu", result.Message);
        Assert.Equal(ServiceStatus.Idle, orchestrator.Gpu.Status);
        Assert.Empty(sim.TriggeredEvents);
    }

    [Fact]
    public void Profile_event_override_is_used_by_the_sim_procedure()
    {
        var (sim, _, orchestrator) = Build(Profile("""{ "id": "x", "gpu": { "event": "" } }"""));

        var result = orchestrator.ToggleGpu();

        Assert.True(result.Accepted, result.Message);
        Assert.Empty(sim.TriggeredEvents);
    }
}
