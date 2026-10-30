using Rampside.Core.Orchestration;
using Rampside.Core.Profiles;
using Rampside.Core.Simulation;

namespace Rampside.Core.Tests;

/// <summary>Fake sim + orchestrator, advanced in fixed time steps.</summary>
internal sealed class TestRig
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(100);

    public TestRig(AircraftProfile? profile = null, AircraftState? state = null)
    {
        Sim = new FakeSimBridge(state);
        Sim.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        Orchestrator = new GroundServiceOrchestrator(Sim, profile ?? FastProfile());
    }

    public FakeSimBridge Sim { get; }
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
        Pushback = new PushbackConfig { ConnectSeconds = 5, DisconnectSeconds = 5 },
        Jetway = new ConnectableConfig { ConnectSeconds = 1, DisconnectSeconds = 1 },
        Stairs = new ConnectableConfig { ConnectSeconds = 1, DisconnectSeconds = 1 },
        Gpu = new ConnectableConfig { ConnectSeconds = 1, DisconnectSeconds = 1 },
    };

    public void Run(double seconds)
    {
        for (var t = 0.0; t < seconds - 1e-9; t += Step.TotalSeconds)
        {
            Sim.Advance(Step);
            Orchestrator.Tick(Step);
        }
    }

    public void SetParkingBrake(bool set) => Sim.Update(s => s with { ParkingBrakeSet = set });
}
