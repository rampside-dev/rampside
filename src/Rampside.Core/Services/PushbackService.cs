using Rampside.Core.Profiles;
using Rampside.Core.Simulation;

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
/// With a <see cref="PushbackOption"/> the tug follows the planned path and stops at its end by itself;
/// for a push and pull it stops after the push, turns the nose wheel and tows the aircraft forwards along the next leg.
/// A steering command takes over manually, in the direction the tug is going.
/// </summary>
public sealed class PushbackService : GroundService
{
    private enum Phase
    {
        None,
        ConnectingTug,
        WaitingForBrakeRelease,
        Pushing,
        ChangingDirection,
        Stopping,
        WaitingForBrakeSet,
        DisconnectingTug,
    }

    /// <summary>The tug stands still this long between a push and a tow, turning the nose wheel for the new direction.</summary>
    public const double DirectionChangeSeconds = 3;

    private Phase _phase;
    private TimeSpan _phaseElapsed;
    private bool _stopRequested;
    private PathFollower? _follower;
    private PathFollower? _next; // the leg after a change of direction
    private TimeSpan _stoppedFor;
    private bool _manualForward; // manual steering while towing forwards
    private PushbackTug? _tug;
    private double? _naturalPitchDeg; // set while the nose wheel is lifted
    private bool _lowering;
    private TimeSpan _brakeSteady; // how long the parking brake has been in the awaited position
    private bool _holdingStill;

    public PushbackService()
        : base("pushback", "Pushback")
    {
    }

    /// <summary>Current speed of the aircraft; negative = backwards (pushed), positive = towed forwards.</summary>
    public double SpeedMps { get; private set; }
    public double SteeringDeg { get; private set; }
    public PushbackSteering Steering { get; private set; }

    /// <summary>The planned pushback being followed, or null for manual steering.</summary>
    public PushbackOption? Plan => _follower?.Option;

    public void Start(ServiceContext context, PushbackOption? plan = null)
    {
        SpeedMps = 0;
        SteeringDeg = 0;
        Steering = PushbackSteering.Straight;
        _stopRequested = false;
        _follower = plan is null ? null : new PathFollower(plan, 0);
        _next = null;
        _manualForward = false;
        Progress = null;
        _tug?.Remove();
        _tug = PushbackTug.Spawn(context);
        Enter(Phase.ConnectingTug, ServiceStatus.Starting, plan is null ? "Tug on the way" : $"Tug on the way, pushback {plan.Label}");
        SayAsNewCrew(CrewPhrases.TugOnTheWay);
    }

    /// <summary>Manual steering; ends a planned pushback.</summary>
    public void Steer(PushbackSteering steering)
    {
        if (_phase == Phase.Pushing && (steering != Steering || _follower is not null))
        {
            Say(CrewPhrases.Steer(steering));
        }

        Steering = steering;
        if (_follower is not null)
        {
            _manualForward = _follower.Forward;
            _follower = null;
            _next = null;
            Progress = null;
            if (_phase is Phase.Pushing or Phase.ChangingDirection)
            {
                Enter(Phase.Pushing, ServiceStatus.Active, ManualDetail);
            }
        }
    }

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
                    _tug?.Connect(config);
                    LiftNoseWheel(context);
                    Say(CrewPhrases.TugConnected);
                }

                break;

            case Phase.WaitingForBrakeRelease:
                if (_naturalPitchDeg is not null)
                {
                    // Lifting the nose makes the aircraft creep despite the parking brake; the tug holds it.
                    HoldStill(context);
                }

                if (_stopRequested)
                {
                    Enter(Phase.DisconnectingTug, ServiceStatus.Finishing, "Pushback cancelled, disconnecting tug");
                    LowerNoseWheel(context);
                    Say(CrewPhrases.Cancelled);
                }
                else if (BrakeSteady(!state.ParkingBrakeSet, elapsed, config))
                {
                    Enter(Phase.Pushing, ServiceStatus.Active, _follower is null ? "Pushing back" : $"Pushing back, {_follower.Option.Label}");
                    if (_follower is null)
                    {
                        Say(CrewPhrases.PushStart);
                    }
                    else
                    {
                        Say(CrewPhrases.PushStart, CrewPhrases.Facing(_follower.Option.Facing));
                    }
                }

                break;

            case Phase.Pushing:
            case Phase.ChangingDirection:
                if (state.ParkingBrakeSet)
                {
                    // Brake set while moving: the tug stops immediately.
                    SpeedMps = 0;
                    context.Sim.ReleaseGroundMotion();
                    Enter(Phase.DisconnectingTug, ServiceStatus.Finishing, "Parking brake set, disconnecting tug");
                    LowerNoseWheel(context);
                    Say(CrewPhrases.BrakeDuringPush);
                    break;
                }

                if (_stopRequested)
                {
                    _next = null;
                    Enter(Phase.Stopping, ServiceStatus.Active, "Stopping");
                    Say(CrewPhrases.PushStopping);
                    break;
                }

                if (_phase == Phase.ChangingDirection)
                {
                    ChangeDirection(context, config, elapsed);
                    break;
                }

                if (_follower is { } follower)
                {
                    var remaining = follower.Update(state);
                    Progress = OverallProgress(follower);
                    if (remaining <= PathFollower.ArrivalMeters)
                    {
                        if (follower.Leg < follower.Option.Legs.Count - 1)
                        {
                            _next = new PathFollower(follower.Option, follower.Leg + 1);
                            _stoppedFor = TimeSpan.Zero;
                            Enter(Phase.ChangingDirection, ServiceStatus.Active, _next.Forward ? "Stopping, then towing forwards" : "Stopping, then pushing back");
                            Say(CrewPhrases.PushStopping);
                            break;
                        }

                        Enter(Phase.Stopping, ServiceStatus.Active, $"Pushback {follower.Option.Label} done, stopping");
                        Say(CrewPhrases.PushStopping);
                        break;
                    }

                    // Slow down so the tug can stop at the end of the leg.
                    var speed = Math.Min(config.SpeedMps, Math.Max(PathFollower.CreepSpeedMps, Math.Sqrt(2 * config.AccelerationMps2 * remaining)));
                    Drive(context, config, follower.Forward ? speed : -speed, dt, follower.SteeringDeg(state, config));
                    break;
                }

                Drive(context, config, _manualForward ? config.SpeedMps : -config.SpeedMps, dt);
                break;

            case Phase.Stopping:
                if (_follower is { } stopping)
                {
                    stopping.Update(state);
                    Drive(context, config, 0, dt, stopping.SteeringDeg(state, config));
                }
                else
                {
                    Drive(context, config, 0, dt);
                }

                if (Math.Abs(SpeedMps) < 0.01)
                {
                    Progress = null;
                    Enter(Phase.WaitingForBrakeSet, ServiceStatus.Active, "Stopped, set parking brake");
                    Say(CrewPhrases.SetBrake);
                }

                break;

            case Phase.WaitingForBrakeSet:
                context.Sim.SetGroundMotion(0, 0);
                if (BrakeSteady(state.ParkingBrakeSet, elapsed, config))
                {
                    context.Sim.ReleaseGroundMotion();
                    Enter(Phase.DisconnectingTug, ServiceStatus.Finishing, "Disconnecting tug");
                    LowerNoseWheel(context);
                    Say(CrewPhrases.Disconnecting);
                }

                break;

            case Phase.DisconnectingTug:
                if (_lowering && _phaseElapsed.TotalSeconds < config.NoseLiftSeconds + 0.5)
                {
                    HoldStill(context);
                }
                else
                {
                    _lowering = false;
                    StopHoldingStill(context);
                }

                // The tug leaves once the nose wheel is down again.
                if (_phaseElapsed.TotalSeconds >= config.DisconnectSeconds && !_lowering)
                {
                    _phase = Phase.None;
                    StopHoldingStill(context);
                    ReleaseTug(context);
                    SetStatus(ServiceStatus.Completed, "Pushback complete");
                    Say(CrewPhrases.Disconnected);
                }

                break;
        }

        _tug?.Follow(config, SteeringDeg);
    }

    public override void Abort(ServiceContext context)
    {
        if (IsBusy)
        {
            context.Sim.ReleaseGroundMotion();
            Say(CrewPhrases.Cancelled);
        }

        SpeedMps = 0;
        _phase = Phase.None;
        _follower = null;
        _next = null;
        Progress = null;
        LowerNoseWheel(context);
        _lowering = false;
        _holdingStill = false;
        ReleaseTug(context);
        base.Abort(context);
    }

    private string ManualDetail => _manualForward ? "Towing forwards, manual steering" : "Pushing back, manual steering";

    /// <summary>Share of the whole planned path (all legs) covered so far.</summary>
    private static double OverallProgress(PathFollower follower)
    {
        var legs = follower.Option.Legs;
        var done = legs.Take(follower.Leg).Sum(l => PushbackOption.PathLength(l.Path));
        var total = follower.Option.LengthMeters;
        return total > 0 ? Math.Clamp((done + follower.TravelledMeters) / total, 0, 1) : 1;
    }

    /// <summary>
    /// Between two legs: comes to a stop on the old leg, stands still for <see cref="DirectionChangeSeconds"/> while the
    /// nose wheel turns for the new leg, then goes on in the new direction.
    /// </summary>
    private void ChangeDirection(ServiceContext context, PushbackConfig config, TimeSpan elapsed)
    {
        var dt = elapsed.TotalSeconds;
        var state = context.State;
        if (_follower is null || _next is null)
        {
            _next = null;
            Enter(Phase.Pushing, ServiceStatus.Active, ManualDetail);
            return;
        }

        if (Math.Abs(SpeedMps) >= 0.01)
        {
            _follower.Update(state);
            Drive(context, config, 0, dt, _follower.SteeringDeg(state, config));
            return;
        }

        _stoppedFor += elapsed;
        _next.Update(state);
        Drive(context, config, 0, dt, _next.SteeringDeg(state, config));
        if (_stoppedFor.TotalSeconds >= DirectionChangeSeconds)
        {
            _follower = _next;
            _next = null;
            var label = _follower.Option.Label;
            Enter(Phase.Pushing, ServiceStatus.Active, _follower.Forward ? $"Towing forwards, {label}" : $"Pushing back, {label}");
        }
    }

    /// <summary>The towbarless tug lifts the nose wheel off the ground, so it does not scrub while the tug turns it.</summary>
    private void LiftNoseWheel(ServiceContext context)
    {
        var config = context.Profile.Pushback;
        if (_naturalPitchDeg is not null || config.NoseLiftDeg <= 0 || _tug is null)
        {
            return;
        }

        var pitch = context.State.PitchDeg;
        context.Sim.HoldPitch(new PitchRamp(pitch, pitch - config.NoseLiftDeg, config.NoseLiftSeconds));
        _naturalPitchDeg = pitch;
    }

    /// <summary>Sets the nose wheel down again; the sim gets the pitch back once it is down.</summary>
    private void LowerNoseWheel(ServiceContext context)
    {
        if (_naturalPitchDeg is not { } natural)
        {
            return;
        }

        var config = context.Profile.Pushback;
        context.Sim.HoldPitch(new PitchRamp(context.State.PitchDeg, natural, config.NoseLiftSeconds, ReleaseWhenDone: true));
        _naturalPitchDeg = null;
        _lowering = true;
    }

    /// <summary>Keeps the aircraft at rest while the nose wheel is lifted or lowered (called every tick).</summary>
    private void HoldStill(ServiceContext context)
    {
        context.Sim.SetGroundMotion(0, 0);
        _holdingStill = true;
    }

    private void StopHoldingStill(ServiceContext context)
    {
        if (_holdingStill)
        {
            context.Sim.ReleaseGroundMotion();
            _holdingStill = false;
        }
    }

    /// <summary>Sends the tug away (back to the stand) or removes it when it never connected.</summary>
    private void ReleaseTug(ServiceContext context)
    {
        _tug?.Leave(context.State, context.Profile.Pushback);
        _tug = null;
    }

    private void Drive(ServiceContext context, PushbackConfig config, double targetSpeedMps, double dt, double? steeringDeg = null)
    {
        SpeedMps = PushbackKinematics.Approach(SpeedMps, targetSpeedMps, config.AccelerationMps2 * dt);

        // Pushing backwards, tail left means the nose wheel turns left (negative angle); towing forwards, it turns right.
        var manual = Steering switch
        {
            PushbackSteering.TailLeft => -config.MaxSteeringDeg,
            PushbackSteering.TailRight => config.MaxSteeringDeg,
            _ => 0,
        };
        var targetSteering = steeringDeg ?? (_manualForward ? -manual : manual);
        targetSteering = Math.Clamp(targetSteering, -config.MaxSteeringDeg, config.MaxSteeringDeg);
        SteeringDeg = PushbackKinematics.Approach(SteeringDeg, targetSteering, config.SteeringRateDegPerSecond * dt);

        var yawRate = PushbackKinematics.YawRate(SpeedMps, SteeringDeg, config.WheelbaseMeters);
        context.Sim.SetGroundMotion(SpeedMps, yawRate);
    }

    /// <summary>
    /// True once the parking brake has been in the awaited position for <see cref="PushbackConfig.BrakeReactionSeconds"/>,
    /// like a ground crew that waits a moment after the pilot's call before acting.
    /// </summary>
    private bool BrakeSteady(bool inPosition, TimeSpan elapsed, PushbackConfig config)
    {
        _brakeSteady = inPosition ? _brakeSteady + elapsed : TimeSpan.Zero;
        return inPosition && _brakeSteady.TotalSeconds >= config.BrakeReactionSeconds;
    }

    private void Enter(Phase phase, ServiceStatus status, string detail)
    {
        _phase = phase;
        _phaseElapsed = TimeSpan.Zero;
        _brakeSteady = TimeSpan.Zero;
        SetStatus(status, detail);
    }
}

/// <summary>
/// The sim's own tug at the nose gear. The default tug is towbarless: the nose wheel sits in its cradle,
/// so the tug turns with the nose wheel and swings out when the pushback steers.
/// It drives up from ahead of the aircraft while connecting, is attached to the aircraft while connected
/// (the sim bridge places it every frame; this class only updates the place when the steering changes),
/// and afterwards drives away ahead of the aircraft and back to the stand, where it is removed.
/// </summary>
public sealed class PushbackTug
{
    /// <summary>Where the tug appears: this far ahead of its connected position.</summary>
    public const double ApproachMeters = 25;

    /// <summary>The tug arrives this long before it is connected.</summary>
    public const double ArriveBeforeConnectSeconds = 5;

    /// <summary>After disconnecting it first drives this far ahead of the aircraft.</summary>
    public const double ClearAheadMeters = 20;

    /// <summary>It parks this far ahead of where it was connected at the stand.</summary>
    public const double ParkAheadOfStandMeters = 10;

    /// <summary>Average driving speed when leaving.</summary>
    public const double LeaveSpeedMps = 2.5;

    private const double MinSteeringChangeDeg = 0.05;

    private readonly IGroundVehicleSpawner _spawner;
    private readonly int _vehicle;
    private readonly (double Latitude, double Longitude, double HeadingTrueDeg) _standPosition;
    private readonly double _standHeadingDeg;
    private double _steeringDeg;
    private bool _connected;

    private PushbackTug(IGroundVehicleSpawner spawner, int vehicle, (double, double, double) standPosition, double standHeadingDeg)
    {
        _spawner = spawner;
        _vehicle = vehicle;
        _standPosition = standPosition;
        _standHeadingDeg = standHeadingDeg;
    }

    /// <summary>
    /// Spawns the profile's tug ahead of the aircraft and lets it drive up to the nose, arriving a little before
    /// <see cref="PushbackConfig.ConnectSeconds"/>. Returns null when the sim cannot spawn vehicles or no tug is configured.
    /// </summary>
    public static PushbackTug? Spawn(ServiceContext context)
    {
        var config = context.Profile.Pushback;
        if (context.Sim is not IGroundVehicleSpawner spawner || string.IsNullOrWhiteSpace(config.TugTitle))
        {
            return null;
        }

        var state = context.State;
        var connected = Attachment(config, 0).Place(state.Latitude, state.Longitude, state.HeadingTrueDeg);
        var start = Geo.Offset(connected.Latitude, connected.Longitude, state.HeadingTrueDeg, ApproachMeters, 0);
        if (spawner.SpawnGroundVehicle(config.TugTitle, start.Latitude, start.Longitude, connected.HeadingTrueDeg) is not { } vehicle)
        {
            return null;
        }

        // Cradle first towards the nose wheel: the tug points the way it drives.
        var arriveAfter = config.ConnectSeconds > 2 * ArriveBeforeConnectSeconds
            ? config.ConnectSeconds - ArriveBeforeConnectSeconds
            : config.ConnectSeconds / 2;
        spawner.DriveGroundVehicle(
            vehicle,
            new VehicleDrive(new[] { start, (connected.Latitude, connected.Longitude) }, arriveAfter, 0, connected.HeadingTrueDeg),
            removeAtEnd: false);
        return new PushbackTug(spawner, vehicle, connected, state.HeadingTrueDeg);
    }

    /// <summary>Locks the tug to the nose wheel.</summary>
    public void Connect(PushbackConfig config)
    {
        _connected = true;
        _steeringDeg = 0;
        _spawner.AttachGroundVehicle(_vehicle, Attachment(config, 0));
    }

    /// <summary>Turns the tug with the nose wheel while connected.</summary>
    public void Follow(PushbackConfig config, double steeringDeg)
    {
        if (!_connected || Math.Abs(steeringDeg - _steeringDeg) < MinSteeringChangeDeg)
        {
            return;
        }

        _steeringDeg = steeringDeg;
        _spawner.AttachGroundVehicle(_vehicle, Attachment(config, steeringDeg));
    }

    /// <summary>
    /// Ends the tug's job: once connected it drives away ahead of the aircraft (cab first) and back to the stand,
    /// where the sim removes it; otherwise it is removed at once.
    /// </summary>
    public void Leave(AircraftState state, PushbackConfig config)
    {
        if (!_connected)
        {
            Remove();
            return;
        }

        _connected = false;
        var here = Attachment(config, _steeringDeg).Place(state.Latitude, state.Longitude, state.HeadingTrueDeg);
        var clear = Geo.Offset(here.Latitude, here.Longitude, state.HeadingTrueDeg, ClearAheadMeters, 0);
        var park = Geo.Offset(_standPosition.Latitude, _standPosition.Longitude, _standHeadingDeg, ParkAheadOfStandMeters, 0);
        var drive = VehicleDrive.AtSpeed(new[] { (here.Latitude, here.Longitude), clear, park }, LeaveSpeedMps, 180, here.HeadingTrueDeg);
        _spawner.DriveGroundVehicle(_vehicle, drive, removeAtEnd: true);
    }

    public void Remove() => _spawner.RemoveGroundVehicle(_vehicle);

    /// <summary>
    /// Tug reference point <see cref="PushbackConfig.TugDistanceMeters"/> ahead of the nose gear in the direction the
    /// nose wheel points, facing the aircraft. Positive steering = nose wheel turned right.
    /// </summary>
    public static VehicleAttachment Attachment(PushbackConfig config, double steeringDeg)
    {
        var steering = Units.DegreesToRadians(steeringDeg);
        return new VehicleAttachment(
            config.NoseGearMeters + config.TugDistanceMeters * Math.Cos(steering),
            config.TugDistanceMeters * Math.Sin(steering),
            steeringDeg + 180);
    }
}

/// <summary>
/// Pure pursuit along one leg of a planned pushback: steers the reference point towards a point a little further
/// along the leg, backwards when pushed and forwards when towed. Kinematically the reference point behaves like the
/// rear axle of a bicycle model, so this stays stable in reverse.
/// </summary>
internal sealed class PathFollower
{
    public const double ArrivalMeters = 0.5;
    public const double CreepSpeedMps = 0.3;
    private const double SearchWindowMeters = 20;

    private readonly IReadOnlyList<Vec2> _path;
    private readonly double[] _distanceAt;
    private int _index;

    public PathFollower(PushbackOption option, int leg)
    {
        Option = option;
        Leg = leg;
        var current = option.Legs[leg];
        _path = current.Path;
        Forward = current.Forward;
        _distanceAt = new double[_path.Count];
        for (var i = 1; i < _path.Count; i++)
        {
            _distanceAt[i] = _distanceAt[i - 1] + (_path[i] - _path[i - 1]).Length;
        }
    }

    public PushbackOption Option { get; }

    /// <summary>Index of the leg in <see cref="PushbackOption.Legs"/>.</summary>
    public int Leg { get; }

    /// <summary>True when this leg is towed forwards.</summary>
    public bool Forward { get; }
    public double TotalMeters => _distanceAt[^1];
    public double TravelledMeters { get; private set; }
    public double Progress => TotalMeters > 0 ? Math.Clamp(TravelledMeters / TotalMeters, 0, 1) : 1;

    /// <summary>Moves on to the path point nearest the aircraft. Returns the distance left on this leg.</summary>
    public double Update(AircraftState state)
    {
        var position = Local(state);
        var path = _path;
        var best = _index;
        var bestDistance = double.MaxValue;
        for (var i = _index; i < path.Count && _distanceAt[i] - _distanceAt[_index] <= SearchWindowMeters; i++)
        {
            var distance = (path[i] - position).Length;
            if (distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }

        _index = best;
        var travelled = _distanceAt[best];

        // Add the part of the next segment already covered, or how far the aircraft is past the end.
        if (path.Count > 1)
        {
            var next = Math.Min(best + 1, path.Count - 1);
            var segment = (path[next] - path[next - 1]).Normalized;
            var along = Vec2.Dot(position - path[best], segment);
            travelled += best == path.Count - 1 ? along : Math.Max(0, along);
        }

        TravelledMeters = travelled;
        return TotalMeters - travelled;
    }

    /// <summary>Nose wheel angle that steers the reference point onto the path (positive = right).</summary>
    public double SteeringDeg(AircraftState state, PushbackConfig config)
    {
        var position = Local(state);
        var lookahead = Math.Max(6, 0.6 * config.WheelbaseMeters);
        var target = PointAt(TravelledMeters + lookahead);

        var motion = Forward ? Vec2.FromHeading(state.HeadingTrueDeg) : -Vec2.FromHeading(state.HeadingTrueDeg);
        var toTarget = target - position;
        var squared = Vec2.Dot(toTarget, toTarget);
        if (squared < 1e-6)
        {
            return 0;
        }

        // Curvature to the right of the direction of motion; in reverse the nose wheel turns the other way.
        var lateralRight = -Vec2.Cross(motion, toTarget);
        var curvature = 2 * lateralRight / squared;
        var angle = Units.RadiansToDegrees(Math.Atan(config.WheelbaseMeters * curvature));
        return Forward ? angle : -angle;
    }

    private Vec2 Local(AircraftState state) =>
        Geo.ToLocal(Option.OriginLatitude, Option.OriginLongitude, state.Latitude, state.Longitude);

    private Vec2 PointAt(double distance)
    {
        var path = _path;
        if (distance >= TotalMeters)
        {
            var end = path[^1];
            var direction = path.Count > 1 ? (end - path[^2]).Normalized : new Vec2(0, 0);
            return end + direction * (distance - TotalMeters);
        }

        var i = Array.BinarySearch(_distanceAt, Math.Max(0, distance));
        if (i >= 0)
        {
            return path[i];
        }

        i = ~i; // first index with a larger distance
        var t = (distance - _distanceAt[i - 1]) / (_distanceAt[i] - _distanceAt[i - 1]);
        return path[i - 1] + (path[i] - path[i - 1]) * t;
    }
}
