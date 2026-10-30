using Rampside.Core.Services;

namespace Rampside.Core.Tests;

public class PushbackTests
{
    [Fact]
    public void Full_pushback_cycle()
    {
        var rig = new TestRig();
        var start = rig.Sim.State;

        Assert.True(rig.Orchestrator.RequestPushback().Accepted);
        rig.Run(6);
        Assert.Equal(ServiceStatus.Active, rig.Orchestrator.Pushback.Status);
        Assert.False(rig.Sim.GroundMotionActive); // waits for the brake

        rig.SetParkingBrake(false);
        rig.Run(15);
        Assert.True(rig.Orchestrator.Pushback.SpeedMps < 0);
        Assert.NotEqual(start.Latitude, rig.Sim.State.Latitude);

        rig.Orchestrator.SteerPushback(PushbackSteering.TailLeft);
        rig.Run(5);
        Assert.True(rig.Sim.State.HeadingTrueDeg > start.HeadingTrueDeg + 5, "tail left should turn the nose right");

        rig.Orchestrator.StopPushback();
        rig.Run(10);
        Assert.Equal(0, rig.Orchestrator.Pushback.SpeedMps, 3);
        Assert.Contains("set parking brake", rig.Orchestrator.Pushback.Detail);

        rig.SetParkingBrake(true);
        rig.Run(6);
        Assert.Equal(ServiceStatus.Completed, rig.Orchestrator.Pushback.Status);
        Assert.False(rig.Sim.GroundMotionActive);
    }

    [Fact]
    public void Pushback_is_rejected_while_the_jetway_is_connected()
    {
        var rig = new TestRig();
        rig.Orchestrator.ToggleJetway();
        rig.Run(1.5);

        Assert.False(rig.Orchestrator.RequestPushback().Accepted);
    }

    [Fact]
    public void Setting_the_brake_while_pushing_stops_immediately()
    {
        var rig = new TestRig();
        rig.Orchestrator.RequestPushback();
        rig.Run(6);
        rig.SetParkingBrake(false);
        rig.Run(5);

        rig.SetParkingBrake(true);
        rig.Run(0.2);

        Assert.Equal(ServiceStatus.Finishing, rig.Orchestrator.Pushback.Status);
        Assert.False(rig.Sim.GroundMotionActive);
    }

    [Theory]
    [InlineData(-1.0, -60.0, 1)]
    [InlineData(-1.0, 60.0, -1)]
    [InlineData(1.0, 60.0, 1)]
    [InlineData(1.0, 0.0, 0)]
    public void Yaw_rate_sign(double speed, double steering, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(PushbackKinematics.YawRate(speed, steering, 12.6)));
    }
}
