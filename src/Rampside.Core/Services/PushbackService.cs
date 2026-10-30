using Rampside.Core.Profiles;

namespace Rampside.Core.Services;

/// <summary>Direction the tail should move, as a pushback crew would phrase it.</summary>
public enum PushbackSteering
{
    Straight,
    TailLeft,
    TailRight,
}

public static class PushbackKinematics
{
    private const double MaxSteeringDeg = 85;

    /// <summary>Bicycle model: yaw rate from speed, nose wheel angle and wheelbase. Positive = nose turns right.</summary>
    public static double YawRate(double speedMps, double steeringDeg, double wheelbaseMeters)
    {
        if (wheelbaseMeters <= 0)
        {
            return 0;
        }

        var steering = Math.Clamp(steeringDeg, -MaxSteeringDeg, MaxSteeringDeg);
        return speedMps / wheelbaseMeters * Math.Tan(Units.DegreesToRadians(steering));
    }

    public static double Approach(double current, double target, double maxStep)
    {
        if (Math.Abs(target - current) <= maxStep)
        {
            return target;
        }

        return current + Math.Sign(target - current) * maxStep;
    }
}

/// <summary>
/// Tug connects, waits for the parking brake to be released, pushes back with steering,
/// stops on request, waits for the parking brake and disconnects.
/// </summary>
public sealed class PushbackService : GroundService
{
    private enum Phase
    {
        None,
        ConnectingTug,
        WaitingForBrakeRelease,
        Pushing,
        Stopping,
        WaitingForBrakeSet,
        DisconnectingTug,
    }

    private Phase _phase;
    private TimeSpan _phaseElapsed;
    private bool _stopRequested;

    public PushbackService()
        : base("pushback", "Pushback")
    {
    }

    /// <summary>Current tug speed; negative = backwards.</summary>
    public double SpeedMps { get; private set; }
    public double SteeringDeg { get; private set; }
    public PushbackSteering Steering { get; private set; }

    public void Start(ServiceContext context)
    {
        SpeedMps = 0;
        SteeringDeg = 0;
        Steering = PushbackSteering.Straight;
        _stopRequested = false;
        Enter(Phase.ConnectingTug, ServiceStatus.Starting, "Tug on the way");
    }

    public void Steer(PushbackSteering steering) => Steering = steering;

    public void RequestStop() => _stopRequested = true;

    public override void Tick(ServiceContext context, TimeSpan elapsed)
    {
        if (!IsBusy)
        {
            return;
        }

        var config = context.Profile.Pushback;
        var state = context.State;
        var dt = elapsed.TotalSeconds;
        _phaseElapsed += elapsed;

        switch (_phase)
        {
            case Phase.ConnectingTug:
                if (_phaseElapsed.TotalSeconds >= config.ConnectSeconds)
                {
                    Enter(Phase.WaitingForBrakeRelease, ServiceStatus.Active, "Tug connected, release parking brake");
                }

                break;

            case Phase.WaitingForBrakeRelease:
                if (_stopRequested)
                {
                    Enter(Phase.DisconnectingTug, ServiceStatus.Finishing, "Pushback cancelled, disconnecting tug");
                }
                else if (!state.ParkingBrakeSet)
                {
                    Enter(Phase.Pushing, ServiceStatus.Active, "Pushing back");
                }

                break;

            case Phase.Pushing:
                if (state.ParkingBrakeSet)
                {
                    // Brake set while moving: the tug stops immediately.
                    SpeedMps = 0;
                    context.Sim.ReleaseGroundMotion();
                    Enter(Phase.DisconnectingTug, ServiceStatus.Finishing, "Parking brake set, disconnecting tug");
                    break;
                }

                if (_stopRequested)
                {
                    Enter(Phase.Stopping, ServiceStatus.Active, "Stopping");
                    break;
                }

                Drive(context, config, -config.SpeedMps, dt);
                break;

            case Phase.Stopping:
                Drive(context, config, 0, dt);
                if (Math.Abs(SpeedMps) < 0.01)
                {
                    Enter(Phase.WaitingForBrakeSet, ServiceStatus.Active, "Stopped, set parking brake");
                }

                break;

            case Phase.WaitingForBrakeSet:
                context.Sim.SetGroundMotion(0, 0);
                if (state.ParkingBrakeSet)
                {
                    context.Sim.ReleaseGroundMotion();
                    Enter(Phase.DisconnectingTug, ServiceStatus.Finishing, "Disconnecting tug");
                }

                break;

            case Phase.DisconnectingTug:
                if (_phaseElapsed.TotalSeconds >= config.DisconnectSeconds)
                {
                    _phase = Phase.None;
                    SetStatus(ServiceStatus.Completed, "Pushback complete");
                }

                break;
        }
    }

    public override void Abort(ServiceContext context)
    {
        if (IsBusy)
        {
            context.Sim.ReleaseGroundMotion();
        }

        SpeedMps = 0;
        _phase = Phase.None;
        base.Abort(context);
    }

    private void Drive(ServiceContext context, PushbackConfig config, double targetSpeedMps, double dt)
    {
        SpeedMps = PushbackKinematics.Approach(SpeedMps, targetSpeedMps, config.AccelerationMps2 * dt);

        // Pushing backwards: tail left means the nose wheel turns left (negative angle).
        var targetSteering = Steering switch
        {
            PushbackSteering.TailLeft => -config.MaxSteeringDeg,
            PushbackSteering.TailRight => config.MaxSteeringDeg,
            _ => 0,
        };
        SteeringDeg = PushbackKinematics.Approach(SteeringDeg, targetSteering, config.SteeringRateDegPerSecond * dt);

        var yawRate = PushbackKinematics.YawRate(SpeedMps, SteeringDeg, config.WheelbaseMeters);
        context.Sim.SetGroundMotion(SpeedMps, yawRate);
    }

    private void Enter(Phase phase, ServiceStatus status, string detail)
    {
        _phase = phase;
        _phaseElapsed = TimeSpan.Zero;
        SetStatus(status, detail);
    }
}
