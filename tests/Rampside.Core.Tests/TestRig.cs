using Rampside.Core.Aircraft.Pmdg;
using Rampside.Core.Aircraft.Procedures;
using Rampside.Core.Gsx;
using Rampside.Core.Orchestration;
using Rampside.Core.Profiles;
using Rampside.Core.Simulation;

namespace Rampside.Core.Tests;

/// <summary>Fake sim + orchestrator, advanced in fixed time steps.</summary>
internal sealed class TestRig
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(100);

    public TestRig(AircraftProfile? profile = null, AircraftState? state = null, FakePmdgLink? pmdg = null, IGsxStandSource? gsx = null)
    {
        Sim = new FakeSimBridge(state);
        Sim.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        Pmdg = pmdg;
        profile ??= FastProfile();
        var adapter = pmdg is null
            ? null
            : AircraftProcedureCatalog.Create(profile, new ProcedureContext(Sim, profile, pmdg), AircraftProcedureCatalog.Pmdg737Family);
        Orchestrator = new GroundServiceOrchestrator(Sim, profile, adapter, gsx);
    }

    public FakeSimBridge Sim { get; }
    public FakePmdgLink? Pmdg { get; }
    public GroundServiceOrchestrator Orchestrator { get; }

    /// <summary>Profile with short delays so tests stay quick.</summary>
    public static AircraftProfile FastProfile() => new()
    {
        Id = "test",
        Passengers = new PassengerConfig
        {
            Capacity = 100,
            WeightKg = 84,
            BagWeightKg = 15,
            BoardingPerMinute = 60,
            DeboardingPerMinute = 120,
            PreparationSeconds = 0,
        },
        PayloadStations = new List<PayloadStationConfig>
        {
            new() { Index = 1, Kind = PayloadKind.Passengers, Share = 1 },
            new() { Index = 2, Kind = PayloadKind.Passengers, Share = 1 },
            new() { Index = 3, Kind = PayloadKind.Cargo, Share = 1 },
        },
        Fuel = new FuelConfig { RateKgPerMinute = 600, TruckArrivalSeconds = 10 },
        Pushback = new PushbackConfig { ConnectSeconds = 5, DisconnectSeconds = 5, NoseLiftSeconds = 2, BrakeReactionSeconds = 0 },
        Jetway = new ConnectableConfig { ConnectSeconds = 1, DisconnectSeconds = 1 },
        Stairs = new ConnectableConfig { ConnectSeconds = 1, DisconnectSeconds = 1 },
        Gpu = new ConnectableConfig { ConnectSeconds = 1, DisconnectSeconds = 1 },
        Doors = new DoorConfig { JetwayOpenDelaySeconds = 0 },
    };

    public void Run(double seconds)
    {
        for (var t = 0.0; t < seconds - 1e-9; t += Step.TotalSeconds)
        {
            Sim.Advance(Step);
            Pmdg?.Advance(Step);
            if (Pmdg is not null)
            {
                Sim.Update(s => s with { NearbyObjects = Pmdg.NearbyObjects(s) });
            }

            Orchestrator.Tick(Step);
        }
    }

    public void SetParkingBrake(bool set) => Sim.Update(s => s with { ParkingBrakeSet = set });
}
