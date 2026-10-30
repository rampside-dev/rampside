using Rampside.Core.Profiles;
using Rampside.Core.Simulation;

namespace Rampside.Core.Services;

public enum CompassDirection
{
    North,
    NorthEast,
    East,
    SouthEast,
    South,
    SouthWest,
    West,
    NorthWest,
}

public static class Compass
{
    private static readonly string[] Names = { "North", "North-East", "East", "South-East", "South", "South-West", "West", "North-West" };
    private static readonly string[] Abbreviations = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

    /// <summary>Nearest of the eight compass directions to a true heading.</summary>
    public static CompassDirection FromHeading(double headingDeg) =>
        (CompassDirection)((int)Math.Round(Geo.NormalizeHeading(headingDeg) / 45) % 8);

    public static string Name(CompassDirection direction) => Names[(int)direction];

    public static string Abbreviation(CompassDirection direction) => Abbreviations[(int)direction];

    /// <summary>Accepts "nw", "northwest", "north-west", "north west" (case-insensitive).</summary>
    public static bool TryParse(string? text, out CompassDirection direction)
    {
        direction = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var compact = new string(text.Where(char.IsLetter).ToArray());
        for (var i = 0; i < Names.Length; i++)
        {
            if (compact.Equals(Abbreviations[i], StringComparison.OrdinalIgnoreCase)
                || compact.Equals(Names[i].Replace("-", string.Empty), StringComparison.OrdinalIgnoreCase))
            {
                direction = (CompassDirection)i;
                return true;
            }
        }

        return false;
    }
}

/// <summary>One stretch of a pushback, driven in one direction (path of the reference point in the option's local frame).</summary>
/// <param name="Forward">True when the tug tows the aircraft forwards (nose first), false when it pushes it backwards.</param>
public sealed record PushbackLeg(IReadOnlyList<Vec2> Path, bool Forward);

/// <summary>
/// A planned pushback: the path of the aircraft reference point in a local frame around <see cref="OriginLatitude"/>/
/// <see cref="OriginLongitude"/> (the position where the pushback starts), ending on a taxi line with the nose
/// pointing <see cref="FinalHeadingDeg"/>.
/// </summary>
/// <param name="Path">The pushback itself: the tug pushes the aircraft backwards along it.</param>
/// <param name="Name">Name given by the source, e.g. a GSX profile's "Facing South - E1"; null for planned options.</param>
/// <param name="Source">Where the option comes from, e.g. "GSX profile eddm-simwings"; null when Rampside planned it.</param>
/// <param name="Then">
/// Stretches after <paramref name="Path"/>, each starting where the one before ends, e.g. GSX's push and pull:
/// pushed off the stand, then towed forwards onto the taxiway. The tug stops between them. Null = push only.
/// </param>
public sealed record PushbackOption(
    CompassDirection Facing,
    double FinalHeadingDeg,
    string Taxiway,
    double OriginLatitude,
    double OriginLongitude,
    IReadOnlyList<Vec2> Path,
    string? Name = null,
    string? Source = null,
    IReadOnlyList<PushbackLeg>? Then = null)
{
    public string Label => Name ?? (string.IsNullOrEmpty(Taxiway)
        ? $"facing {Compass.Name(Facing)}"
        : $"facing {Compass.Name(Facing)} on {Taxiway}");

    /// <summary>Every stretch in order: <see cref="Path"/> backwards, then <see cref="Then"/>.</summary>
    public IReadOnlyList<PushbackLeg> Legs => [new PushbackLeg(Path, Forward: false), .. Then ?? []];

    public double LengthMeters => Legs.Sum(l => PathLength(l.Path));

    /// <summary>How far the tug tows the aircraft forwards in all; 0 for a plain pushback.</summary>
    public double TowMeters => (Then ?? []).Where(l => l.Forward).Sum(l => PathLength(l.Path));

    internal static double PathLength(IReadOnlyList<Vec2> path)
    {
        var length = 0.0;
        for (var i = 1; i < path.Count; i++)
        {
            length += (path[i] - path[i - 1]).Length;
        }

        return length;
    }
}

/// <summary>
/// GSX-style automatic pushback targets: follow the line behind the tail to the first taxi line it crosses,
/// then turn onto that line in either direction. Each direction becomes one option, named by the compass
/// direction the nose points at the end ("facing North-West").
/// </summary>
public static class PushbackPlanner
{
    /// <summary>How far behind the aircraft a taxi line is searched.</summary>
    public const double SearchDistanceMeters = 200;

    /// <summary>Taxi lines closer than this behind the reference point are ignored (e.g. a lane the aircraft stands on).</summary>
    public const double MinDistanceMeters = 8;

    private const double SampleSpacingMeters = 1;
    private const double StraightToleranceDeg = 3;
    private const double MaxTurnDeg = 165;

    public static IReadOnlyList<PushbackOption> Plan(AircraftState state, TaxiwayNetwork taxiways, PushbackConfig config)
    {
        var originLat = state.Latitude;
        var originLon = state.Longitude;
        var back = Vec2.FromHeading(state.HeadingTrueDeg + 180);

        var crossings = new List<(double Distance, Vec2 Along, string Name)>();
        foreach (var (a, b, path) in taxiways.TaxiLines())
        {
            var pa = Geo.ToLocal(originLat, originLon, a.Latitude, a.Longitude);
            var pb = Geo.ToLocal(originLat, originLon, b.Latitude, b.Longitude);
            if (RayHitsSegment(back, pa, pb) is { } distance && distance is >= MinDistanceMeters and <= SearchDistanceMeters)
            {
                crossings.Add((distance, (pb - pa).Normalized, path.Name));
            }
        }

        // The nearest taxi line that allows at least one turn wins.
        foreach (var (distance, along, name) in crossings.OrderBy(c => c.Distance))
        {
            var options = new List<PushbackOption>();
            foreach (var facing in new[] { along, -along })
            {
                if (BuildPath(back, distance, -facing, config) is { } path)
                {
                    var heading = facing.HeadingDeg;
                    options.Add(new PushbackOption(Compass.FromHeading(heading), heading, name, originLat, originLon, path));
                }
            }

            if (options.Count > 0)
            {
                return options;
            }
        }

        return Array.Empty<PushbackOption>();
    }

    /// <summary>Distance along the ray from the origin to segment a–b, or null if they do not cross.</summary>
    private static double? RayHitsSegment(Vec2 direction, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        var denominator = Vec2.Cross(direction, ab);
        if (Math.Abs(denominator) < 1e-9)
        {
            return null; // parallel
        }

        var s = Vec2.Cross(a, ab) / denominator;
        var r = Vec2.Cross(a, direction) / denominator;
        return s > 0 && r is >= 0 and <= 1 ? s : null;
    }

    /// <summary>
    /// Straight back along <paramref name="back"/>, an arc onto the taxi line, then a short straight along it.
    /// <paramref name="lineMotion"/> is the direction the reference point moves on the taxi line (opposite to the final nose direction).
    /// </summary>
    private static IReadOnlyList<Vec2>? BuildPath(Vec2 back, double crossingDistance, Vec2 lineMotion, PushbackConfig config)
    {
        var crossing = back * crossingDistance;
        var turn = Math.Atan2(Vec2.Cross(back, lineMotion), Vec2.Dot(back, lineMotion)); // CCW positive
        var turnAbs = Math.Abs(turn);
        if (Units.RadiansToDegrees(turnAbs) > MaxTurnDeg)
        {
            return null;
        }

        var path = new List<Vec2> { new(0, 0) };
        if (Units.RadiansToDegrees(turnAbs) < StraightToleranceDeg)
        {
            AddLine(path, crossing + lineMotion * config.StraightAfterTurnMeters);
            return path;
        }

        // Arc tangent to both lines; shrink it if the taxi line is too close for the preferred radius.
        var radius = config.TurnRadiusMeters;
        var tangent = radius * Math.Tan(turnAbs / 2);
        if (tangent > crossingDistance)
        {
            tangent = crossingDistance;
            radius = tangent / Math.Tan(turnAbs / 2);
        }

        if (radius < MinTurnRadius(config))
        {
            return null;
        }

        var arcStart = crossing - back * tangent;
        var arcEnd = crossing + lineMotion * tangent;
        var side = Math.Sign(turn);
        var centre = arcStart + back.LeftNormal * (radius * side);

        AddLine(path, arcStart);
        var steps = Math.Max(2, (int)Math.Ceiling(radius * turnAbs / SampleSpacingMeters));
        for (var i = 1; i <= steps; i++)
        {
            path.Add(centre + (arcStart - centre).Rotate(turn * i / steps));
        }

        path[^1] = arcEnd;
        AddLine(path, arcEnd + lineMotion * config.StraightAfterTurnMeters);
        return path;
    }

    /// <summary>Tightest turn the nose wheel allows, with some margin for the steering rate.</summary>
    public static double MinTurnRadius(PushbackConfig config) =>
        config.WheelbaseMeters / Math.Tan(Units.DegreesToRadians(Math.Clamp(config.MaxSteeringDeg, 1, 85))) * 1.25;

    private static void AddLine(List<Vec2> path, Vec2 to)
    {
        var from = path[^1];
        var length = (to - from).Length;
        var steps = (int)Math.Ceiling(length / SampleSpacingMeters);
        for (var i = 1; i <= steps; i++)
        {
            path.Add(from + (to - from) * ((double)i / steps));
        }
    }
}
