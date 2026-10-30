using Rampside.Core.Profiles;
using Rampside.Core.Services;
using Rampside.Core.Simulation;

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
        // Waits for the brake; with the nose wheel lifted the tug holds the aircraft still.
        Assert.Equal(0, rig.Sim.State.GroundSpeedKnots, 3);
        Assert.Equal(start.Latitude, rig.Sim.State.Latitude, 9);

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
        Assert.Equal(0, rig.Orchestrator.Pushback.SpeedMps);
        Assert.Equal(0, rig.Sim.State.GroundSpeedKnots, 3);

        // Held still while the nose wheel comes down, then the sim gets the aircraft back.
        rig.Run(rig.Orchestrator.Profile.Pushback.NoseLiftSeconds + 1);
        Assert.False(rig.Sim.GroundMotionActive);
    }

    [Fact]
    public void Tug_drives_up_to_the_nose_follows_the_aircraft_and_drives_back_to_the_stand()
    {
        var rig = new TestRig();
        var config = rig.Orchestrator.Profile.Pushback;
        var stand = rig.Sim.State;

        // Appears ahead of the aircraft, facing it, and drives up to the nose wheel.
        rig.Orchestrator.RequestPushback();
        var tug = Assert.Single(rig.Sim.Vehicles);
        Assert.Equal(config.TugTitle, tug.Title);
        var (ahead, _) = Geo.Relative(stand.Latitude, stand.Longitude, stand.HeadingTrueDeg, tug.Latitude, tug.Longitude);
        Assert.Equal(config.NoseGearMeters + config.TugDistanceMeters + PushbackTug.ApproachMeters, ahead, 1);
        Assert.Equal(0, Geo.HeadingDifference(stand.HeadingTrueDeg + 180, tug.HeadingTrueDeg), 1);

        rig.Run(config.ConnectSeconds / 2 - 0.5);
        var (halfway, _) = Geo.Relative(stand.Latitude, stand.Longitude, stand.HeadingTrueDeg, rig.Sim.Vehicles[0].Latitude, rig.Sim.Vehicles[0].Longitude);
        Assert.InRange(halfway, config.NoseGearMeters + config.TugDistanceMeters + 0.5, ahead - 0.5);

        rig.Run(1);
        AssertTugAtNose(rig, Assert.Single(rig.Sim.Vehicles));

        rig.Run(5);
        rig.SetParkingBrake(false);
        rig.Orchestrator.SteerPushback(PushbackSteering.TailLeft);
        rig.Run(15);
        tug = Assert.Single(rig.Sim.Vehicles);
        AssertTugAtNose(rig, tug);

        // Tail left turns the nose wheel left, so the tug (which faces the aircraft) turns with it.
        var state = rig.Sim.State;
        var turned = Geo.HeadingDifference(state.HeadingTrueDeg + 180, tug.HeadingTrueDeg);
        Assert.True(turned < -5, $"tug should turn with the nose wheel to the left, turned {turned:F1}°");

        rig.Orchestrator.StopPushback();
        rig.Run(10);
        rig.SetParkingBrake(true);
        rig.Run(Math.Max(config.DisconnectSeconds, config.NoseLiftSeconds + 0.5) + 0.5);
        Assert.Equal(ServiceStatus.Completed, rig.Orchestrator.Pushback.Status);

        // Drives away instead of vanishing, and is removed once back at the stand.
        tug = Assert.Single(rig.Sim.Vehicles);
        Assert.True(rig.Sim.IsDriving(tug.Id));
        for (var i = 0; i < 120 && rig.Sim.Vehicles.Count > 0; i++)
        {
            var last = rig.Sim.Vehicles[0];
            rig.Run(1);
            if (rig.Sim.Vehicles.Count > 0)
            {
                var moved = Geo.ToLocal(last.Latitude, last.Longitude, rig.Sim.Vehicles[0].Latitude, rig.Sim.Vehicles[0].Longitude).Length;
                Assert.True(moved < 6, $"tug jumped {moved:F1} m in one second");
            }
        }

        Assert.Empty(rig.Sim.Vehicles);
    }

    [Fact]
    public void Vehicle_drive_starts_and_stops_gently_and_points_along_the_path()
    {
        var start = (Lat: 48.35, Lon: 11.78);
        var corner = Geo.Offset(start.Lat, start.Lon, 0, 20, 0);
        var end = Geo.Offset(corner.Latitude, corner.Longitude, 90, 20, 0);
        var drive = new VehicleDrive(new[] { start, corner, end }, 20, 0);

        Assert.Equal(40, drive.LengthMeters, 1);
        Assert.Equal(0, drive.At(1).HeadingTrueDeg, 1);   // northbound leg
        Assert.Equal(90, drive.At(19).HeadingTrueDeg, 1); // eastbound leg
        var (endLat, endLon, _) = drive.At(20);
        Assert.Equal(0, Geo.ToLocal(end.Latitude, end.Longitude, endLat, endLon).Length, 2);

        double Along(double t) => Geo.ToLocal(start.Lat, start.Lon, drive.At(t).Latitude, drive.At(t).Longitude).Length;
        Assert.True(Along(1) < 0.3, "slow start");
        Assert.Equal(180, new VehicleDrive(new[] { start, corner }, 10, 180).At(5).HeadingTrueDeg, 1); // backwards
    }

    [Fact]
    public void Tug_lifts_the_nose_wheel_while_connected_and_sets_it_down_before_leaving()
    {
        var rig = new TestRig();
        var config = rig.Orchestrator.Profile.Pushback;
        var natural = rig.Sim.State.PitchDeg;

        rig.Orchestrator.RequestPushback();
        rig.Run(config.ConnectSeconds - 0.5);
        Assert.False(rig.Sim.PitchHeld); // still connecting

        rig.Run(0.5 + config.NoseLiftSeconds);
        Assert.Equal(natural - config.NoseLiftDeg, rig.Sim.State.PitchDeg, 3); // nose up = more negative

        rig.SetParkingBrake(false);
        rig.Run(5);
        rig.Orchestrator.StopPushback();
        rig.Run(10);
        rig.SetParkingBrake(true);
        rig.Run(0.2);
        Assert.True(rig.Sim.PitchHeld); // lowering
        Assert.True(rig.Sim.State.PitchDeg < natural);

        rig.Run(config.NoseLiftSeconds + 0.2);
        Assert.False(rig.Sim.PitchHeld);
        Assert.Equal(natural, rig.Sim.State.PitchDeg, 3);
    }

    [Fact]
    public void Aborting_the_pushback_sets_the_nose_wheel_down_gently()
    {
        var rig = new TestRig();
        var config = rig.Orchestrator.Profile.Pushback;
        rig.Orchestrator.RequestPushback();
        rig.Run(config.ConnectSeconds + config.NoseLiftSeconds + 0.5);

        Assert.True(rig.Orchestrator.Abort("pushback").Accepted);
        rig.Run(config.NoseLiftSeconds / 2);
        Assert.True(rig.Sim.PitchHeld);

        rig.Run(config.NoseLiftSeconds / 2 + 0.2);
        Assert.False(rig.Sim.PitchHeld);
    }

    [Fact]
    public void Crew_waits_a_moment_after_the_parking_brake_changes()
    {
        var fast = TestRig.FastProfile();
        var profile = new AircraftProfile
        {
            Id = fast.Id,
            Pushback = new PushbackConfig { ConnectSeconds = 5, DisconnectSeconds = 5, NoseLiftSeconds = 2, BrakeReactionSeconds = 3 },
        };
        var rig = new TestRig(profile);
        rig.Orchestrator.RequestPushback();
        rig.Run(5.5);

        rig.SetParkingBrake(false);
        rig.Run(2.5);
        Assert.Contains("release parking brake", rig.Orchestrator.Pushback.Detail); // still waiting

        // Set again within the 3 s: the wait starts over.
        rig.SetParkingBrake(true);
        rig.Run(0.5);
        rig.SetParkingBrake(false);
        rig.Run(2.5);
        Assert.Contains("release parking brake", rig.Orchestrator.Pushback.Detail);
        rig.Run(1);
        Assert.StartsWith("Pushing back", rig.Orchestrator.Pushback.Detail);

        rig.Orchestrator.StopPushback();
        rig.Run(10);
        rig.SetParkingBrake(true);
        rig.Run(2.5);
        Assert.Contains("set parking brake", rig.Orchestrator.Pushback.Detail);
        rig.Run(1);
        Assert.Contains("Disconnecting", rig.Orchestrator.Pushback.Detail);
    }

    [Fact]
    public void Pitch_ramp_follows_sin_squared()
    {
        var ramp = new PitchRamp(1, -1, 4);

        Assert.Equal(1, ramp.At(0), 6);
        Assert.Equal(0, ramp.At(2), 6);
        Assert.Equal(-1, ramp.At(4), 6);
        Assert.Equal(-1, ramp.At(9), 6);

        // Gentle at both ends: the first and last tenth move far less than the middle tenth.
        var start = ramp.At(0) - ramp.At(0.4);
        var middle = ramp.At(1.8) - ramp.At(2.2);
        var end = ramp.At(3.6) - ramp.At(4);
        Assert.True(start < middle / 5 && end < middle / 5, $"start {start:F3}, middle {middle:F3}, end {end:F3}");
    }

    [Fact]
    public void Aborting_the_pushback_removes_the_tug()
    {
        var rig = new TestRig();
        rig.Orchestrator.RequestPushback();
        rig.Run(1);
        Assert.Single(rig.Sim.Vehicles);

        Assert.True(rig.Orchestrator.Abort("pushback").Accepted);

        Assert.Empty(rig.Sim.Vehicles);
    }

    private static void AssertTugAtNose(TestRig rig, FakeVehicle tug)
    {
        var state = rig.Sim.State;
        var config = rig.Orchestrator.Profile.Pushback;
        var steering = rig.Orchestrator.Pushback.SteeringDeg;
        var (gearLat, gearLon) = Geo.Offset(state.Latitude, state.Longitude, state.HeadingTrueDeg, config.NoseGearMeters, 0);
        var distance = Geo.ToLocal(gearLat, gearLon, tug.Latitude, tug.Longitude).Length;

        // The tug follows the aircraft exactly (the bridge places it every frame), turned with the nose wheel.
        Assert.Equal(config.TugDistanceMeters, distance, 2);
        Assert.Equal(0, Geo.HeadingDifference(state.HeadingTrueDeg + steering + 180, tug.HeadingTrueDeg), 1);
        Assert.Equal(0, Geo.HeadingDifference(state.HeadingTrueDeg + steering + 180, tug.HeadingTrueDeg), 1);
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
