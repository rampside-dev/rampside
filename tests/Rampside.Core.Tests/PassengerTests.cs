using Rampside.Core.Services;

namespace Rampside.Core.Tests;

public class PassengerTests
{
    [Fact]
    public void Boarding_needs_jetway_or_stairs()
    {
        var rig = new TestRig();

        var result = rig.Orchestrator.RequestBoarding(50);

        Assert.False(result.Accepted);
    }

    [Fact]
    public void Boarding_writes_passenger_and_bag_weights()
    {
        var rig = new TestRig();
        Assert.True(rig.Orchestrator.ToggleStairs().Accepted);
        rig.Run(1.5);
        Assert.True(rig.Orchestrator.Stairs.IsConnected);

        Assert.True(rig.Orchestrator.RequestBoarding(100).Accepted);
        rig.Run(110); // 100 pax at 60/min

        Assert.Equal(ServiceStatus.Completed, rig.Orchestrator.Passengers.Status);
        var stations = rig.Sim.PayloadStations;
        Assert.Equal(50 * 84, stations[1], 3);
        Assert.Equal(50 * 84, stations[2], 3);
        Assert.Equal(100 * 15, stations[3], 3);
    }

    [Fact]
    public void Deboarding_empties_the_cabin()
    {
        var rig = new TestRig();
        rig.Orchestrator.ToggleJetway();
        rig.Run(1.5);
        rig.Orchestrator.RequestBoarding(60);
        rig.Run(70);

        Assert.True(rig.Orchestrator.RequestDeboarding().Accepted);
        rig.Run(40); // 60 pax at 120/min

        Assert.Equal(0, rig.Orchestrator.Passengers.PassengersOnBoard);
        Assert.All(rig.Sim.PayloadStations.Values, w => Assert.Equal(0, w, 3));
    }

    [Fact]
    public void Last_cabin_access_cannot_be_removed_while_boarding()
    {
        var rig = new TestRig();
        rig.Orchestrator.ToggleJetway();
        rig.Run(1.5);
        rig.Orchestrator.RequestBoarding(100);
        rig.Run(5);

        Assert.False(rig.Orchestrator.ToggleJetway().Accepted);
    }
}
