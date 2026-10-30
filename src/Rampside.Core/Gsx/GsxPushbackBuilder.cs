using Rampside.Core.Services;
using Rampside.Core.Simulation;

namespace Rampside.Core.Gsx;

/// <summary>
/// Turns the pushback targets of a GSX stand into <see cref="PushbackOption"/>s that start at the aircraft.
/// Between two points the path is a curve that leaves and arrives in the direction of the aircraft heading
/// given for each point, so the tug follows the shape the profile author drew.
/// GSX targets may go on with the tug towing the aircraft forwards (push and pull, e.g. along a taxiway to a
/// remote area); such a target becomes a pushback followed by forward legs (<see cref="PushbackOption.Then"/>).
/// </summary>
public static partial class GsxPushbackBuilder
{
    /// <summary>The first waypoint must be at least this far behind the aircraft.</summary>
    public const double MinFirstLegMeters = 2;

    /// <summary>The aircraft heading at the first waypoint may differ this much from the current heading (profiles often start with a 90° turn; 180° means the aircraft faces the other way).</summary>
    public const double MaxStartHeadingDifferenceDeg = 135;

    /// <summary>The aircraft must stand at most this far from the GSX stop position, or the targets do not apply.</summary>
    public const double MaxStandDistanceMeters = 40;

    private const double SampleSpacingMeters = 1;
    private const double DuplicatePointMeters = 0.5;

    /// <summary>
    /// Options for every target that starts behind the aircraft. Targets that cannot be used are left out,
    /// and they and shortened targets are described in <paramref name="notes"/>.
    /// </summary>
    public static IReadOnlyList<PushbackOption> Build(AircraftState state, GsxStandMatch match, ICollection<string>? notes = null)
    {
        var options = new List<PushbackOption>();
        var stop = match.StopPosition;
        if (StandLocator.DistanceMeters(state.Latitude, state.Longitude, stop.Latitude, stop.Longitude) > MaxStandDistanceMeters)
        {
            notes?.Add($"aircraft is more than {MaxStandDistanceMeters:F0} m from the stand's stop position");
            return options;
        }

        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var source = $"GSX profile {match.ProfileName}";
        foreach (var target in match.Stand.PushbackTargets)
        {
            if (BuildPath(state, target, out var reason) is not { } built)
            {
                notes?.Add($"{target.Label}: skipped, {reason}");
                continue;
            }

            var legs = built.Legs;
            if (legs.Any(l => l.Forward))
            {
                notes?.Add($"{target.Label}: push and pull, towed forwards {legs.Where(l => l.Forward).Sum(l => PushbackOption.PathLength(l.Path)):F0} m");
            }

            var label = target.Label;
            for (var n = 2; !labels.Add(label); n++)
            {
                label = $"{target.Label} ({n})";
            }

            options.Add(new PushbackOption(
                Compass.FromHeading(built.FinalHeadingDeg),
                built.FinalHeadingDeg,
                string.Empty,
                state.Latitude,
                state.Longitude,
                legs[0].Path,
                label,
                source,
                legs.Count > 1 ? legs.Skip(1).ToArray() : null));
        }

        // Profile authors list the targets in any order; group them by where they end, then by direction.
        return options
            .OrderBy(o => SortKey(o.Label).Taxiway, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => SortKey(o.Label).Direction)
            .ToList();
    }

    /// <summary>
    /// Splits a GSX label like "Facing North - E1 Blue" into the part after the direction ("E1 Blue") and the
    /// direction. Labels without a leading "Facing &lt;direction&gt;" keep their full text and sort before the directions.
    /// </summary>
    public static (string Taxiway, int Direction) SortKey(string label)
    {
        var match = FacingLabel().Match(label);
        return match.Success && Compass.TryParse(match.Groups["dir"].Value, out var direction)
            ? (match.Groups["rest"].Value.Trim(), (int)direction)
            : (label.Trim(), -1);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*facing\s+(?<dir>(north|south)(\s*-?\s*(east|west))?|east|west)\b\s*[-–:,]?\s*(?<rest>.*)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex FacingLabel();

    private static (IReadOnlyList<PushbackLeg> Legs, double FinalHeadingDeg)? BuildPath(
        AircraftState state, GsxPushbackTarget target, out string reason)
    {
        var originLat = state.Latitude;
        var originLon = state.Longitude;
        var poses = new List<(Vec2 Point, double? Heading)> { (new Vec2(0, 0), state.HeadingTrueDeg) };
        foreach (var pose in target.Waypoints.Append(target.Final))
        {
            var point = Geo.ToLocal(originLat, originLon, pose.Latitude, pose.Longitude);
            if ((point - poses[^1].Point).Length >= DuplicatePointMeters)
            {
                poses.Add((point, pose.HeadingDeg));
            }
        }

        if (poses.Count < 2)
        {
            reason = "ends where the aircraft stands";
            return null;
        }

        var first = poses[1];
        var forward = Vec2.Dot(first.Point, Vec2.FromHeading(state.HeadingTrueDeg));
        if (forward > -MinFirstLegMeters)
        {
            reason = "does not start behind the aircraft";
            return null;
        }

        if (first.Heading is { } firstHeading && Math.Abs(Geo.HeadingDifference(state.HeadingTrueDeg, firstHeading)) > MaxStartHeadingDifferenceDeg)
        {
            reason = $"starts with the nose at {Geo.NormalizeHeading(firstHeading):F0}°, the aircraft points {Geo.NormalizeHeading(state.HeadingTrueDeg):F0}°";
            return null;
        }

        // A stretch that runs along the nose at both of its ends is the tug towing forwards; every other one is pushed.
        var towed = new bool[poses.Count - 1];
        for (var i = 0; i < towed.Length; i++)
        {
            var stretch = poses[i + 1].Point - poses[i].Point;
            towed[i] = Vec2.Dot(stretch, Backwards(poses, i) + Backwards(poses, i + 1)) < 0;
        }

        if (towed[0])
        {
            reason = "does not start behind the aircraft";
            return null;
        }

        // Consecutive stretches in one direction make one leg; the tug stops where the direction changes.
        var legs = new List<PushbackLeg>();
        var legStart = 0;
        for (var i = 1; i <= towed.Length; i++)
        {
            if (i < towed.Length && towed[i] == towed[legStart])
            {
                continue;
            }

            var path = new List<Vec2> { poses[legStart].Point };
            for (var k = legStart + 1; k <= i; k++)
            {
                AddCurve(path, poses[k - 1].Point, Travel(poses, k - 1, towed[legStart]), poses[k].Point, Travel(poses, k, towed[legStart]));
            }

            legs.Add(new PushbackLeg(path, towed[legStart]));
            legStart = i;
        }

        reason = string.Empty;
        var last = legs[^1].Forward;
        var travel = Travel(poses, poses.Count - 1, last);
        var finalHeading = Geo.NormalizeHeading((last ? travel : -travel).HeadingDeg);
        return (legs, finalHeading);
    }

    /// <summary>
    /// Direction the reference point moves at a pose when pushed: backwards along the given heading. A point without
    /// heading is in practice the last one before the target, on the target's line, so it takes the heading of the next
    /// point that has one (or, failing that, the line through its neighbours).
    /// </summary>
    private static Vec2 Backwards(List<(Vec2 Point, double? Heading)> poses, int i) =>
        Heading(poses, i) is { } h ? -Vec2.FromHeading(h) : Line(poses, i);

    /// <summary>Direction the reference point moves at a pose on a leg that is towed forwards or pushed.</summary>
    private static Vec2 Travel(List<(Vec2 Point, double? Heading)> poses, int i, bool towed) =>
        Heading(poses, i) is { } h ? (towed ? Vec2.FromHeading(h) : -Vec2.FromHeading(h)) : Line(poses, i);

    private static double? Heading(List<(Vec2 Point, double? Heading)> poses, int i) =>
        poses.Skip(i).Select(p => p.Heading).FirstOrDefault(h => h is not null);

    private static Vec2 Line(List<(Vec2 Point, double? Heading)> poses, int i) =>
        (poses[Math.Min(i + 1, poses.Count - 1)].Point - poses[Math.Max(i - 1, 0)].Point).Normalized;

    /// <summary>
    /// Cubic Bézier from <paramref name="a"/> to <paramref name="b"/>, tangent to the motion at both ends.
    /// The handle length is the one that makes a symmetric turn a circular arc, so the curve is no tighter than needed.
    /// </summary>
    private static void AddCurve(List<Vec2> path, Vec2 a, Vec2 motionA, Vec2 b, Vec2 motionB)
    {
        var length = (b - a).Length;
        var turn = Math.Min(Math.Abs(Math.Atan2(Vec2.Cross(motionA, motionB), Vec2.Dot(motionA, motionB))), Units.DegreesToRadians(170));
        var handle = turn < 0.02 ? length / 3 : length * 2 / 3 * Math.Tan(turn / 4) / Math.Sin(turn / 2);
        var c1 = a + motionA * handle;
        var c2 = b - motionB * handle;
        var steps = Math.Max(2, (int)Math.Ceiling(length / SampleSpacingMeters));
        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            var u = 1 - t;
            path.Add(a * (u * u * u) + c1 * (3 * u * u * t) + c2 * (3 * u * t * t) + b * (t * t * t));
        }
    }
}
