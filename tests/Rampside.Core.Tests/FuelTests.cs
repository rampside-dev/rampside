using Rampside.Core.Services;
using Rampside.Core.Simulation;

namespace Rampside.Core.Tests;

public class FuelTests
{
    private static readonly FuelTankState[] Tanks =
    {
        new(FuelTankId.LeftMain, 6000, 0),
        new(FuelTankId.RightMain, 6000, 0),
        new(FuelTankId.Center, 8000, 0),
    };

    [Fact]
    public void Planner_fills_wing_tanks_first()
    {
        var plan = FuelPlanner.Distribute(Tanks, 10_000);

        Assert.Equal(5000, plan[FuelTankId.LeftMain], 3);
        Assert.Equal(5000, plan[FuelTankId.RightMain], 3);
        Assert.Equal(0, plan[FuelTankId.Center], 3);
    }

    [Fact]
    public void Planner_puts_the_rest_into_the_center_tank()
    {
        var plan = FuelPlanner.Distribute(Tanks, 15_000);

        Assert.Equal(6000, plan[FuelTankId.LeftMain], 3);
        Assert.Equal(6000, plan[FuelTankId.RightMain], 3);
        Assert.Equal(3000, plan[FuelTankId.Center], 3);
    }

    [Fact]
    public void Planner_clamps_to_capacity()
    {
        var plan = FuelPlanner.Distribute(Tanks, 99_000);

        Assert.Equal(20_000, plan.Values.Sum(), 3);
    }

    [Fact]
    public void Refueling_waits_for_the_truck_then_reaches_the_target()
    {
        var rig = new TestRig();
        var result = rig.Orchestrator.RequestRefuel(5000);
        Assert.True(result.Accepted, result.Message);

        rig.Run(9);
        Assert.Equal(ServiceStatus.Starting, rig.Orchestrator.Refueling.Status);
        Assert.Equal(2000, rig.Sim.State.TotalFuelKg, 3);

        // 3000 kg at 600 kg/min = 300 s
        rig.Run(1 + 301);
        Assert.Equal(ServiceStatus.Completed, rig.Orchestrator.Refueling.Status);
        Assert.Equal(5000, rig.Sim.State.TotalFuelKg, 1);
    }

    [Fact]
    public void Refueling_is_rejected_with_engines_running()
    {
        var rig = new TestRig();
        rig.Sim.Update(s => s with { EnginesRunning = true });

        var result = rig.Orchestrator.RequestRefuel(5000);

        Assert.False(result.Accepted);
    }
}
