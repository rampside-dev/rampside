namespace Rampside.Core.Services;

/// <summary>
/// Something the ground crew says to the cockpit over the headset. <see cref="Phrases"/> are spoken one after
/// another; each names a clip in assets/audio/groundcrew without its variant suffix (see <see cref="CrewPhrases"/>).
/// </summary>
/// <param name="NewCrew">True when a new crew member starts talking (e.g. a new tug), so a voice may be chosen again.</param>
public sealed record CrewCall(string ServiceId, IReadOnlyList<string> Phrases, bool NewCrew = false);

/// <summary>Phrase names, matching the clip file names in assets/audio/groundcrew/&lt;voice&gt;/.</summary>
public static class CrewPhrases
{
    public const string TugOnTheWay = "tug_on_the_way";
    public const string TugConnected = "tug_connected";
    public const string PushStart = "push_start";
    public const string PushStopping = "push_stopping";
    public const string SetBrake = "set_brake";
    public const string Disconnecting = "disconnecting";
    public const string Disconnected = "disconnected";
    public const string Cancelled = "cancelled";
    public const string BrakeDuringPush = "brake_during_push";
    public const string SteerTailLeft = "steer_tail_left";
    public const string SteerTailRight = "steer_tail_right";
    public const string SteerStraight = "steer_straight";

    /// <summary>"facing_northwest" etc.</summary>
    public static string Facing(CompassDirection direction) =>
        "facing_" + Compass.Name(direction).Replace("-", string.Empty).ToLowerInvariant();

    public static string Steer(PushbackSteering steering) => steering switch
    {
        PushbackSteering.TailLeft => SteerTailLeft,
        PushbackSteering.TailRight => SteerTailRight,
        _ => SteerStraight,
    };

    /// <summary>Every phrase the services can say, for checking that the clips exist.</summary>
    public static IReadOnlyList<string> All { get; } =
        new[]
        {
            TugOnTheWay, TugConnected, PushStart, PushStopping, SetBrake, Disconnecting, Disconnected, Cancelled,
            BrakeDuringPush, SteerTailLeft, SteerTailRight, SteerStraight,
        }
        .Concat(Enum.GetValues<CompassDirection>().Select(Facing))
        .ToArray();
}
