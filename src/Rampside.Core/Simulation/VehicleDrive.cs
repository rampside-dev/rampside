namespace Rampside.Core.Simulation;

/// <summary>
/// A ground vehicle driving along a path in a set time. It starts and stops gently (the distance follows a sin² curve
/// over the time) and points along the path, turned by <see cref="HeadingOffsetDeg"/>
/// (180 = drives backwards, e.g. the pushback tug cab first).
/// </summary>
public sealed class VehicleDrive
{
    /// <summary>The heading is taken over this much path before and after the vehicle, which rounds off corners.</summary>
    private const double HeadingSpanMeters = 3;

    private readonly double _originLatitude;
    private readonly double _originLongitude;
    private readonly Vec2[] _points;
    private readonly double[] _distanceAt;
    private readonly double _fallbackHeadingDeg;

    /// <param name="path">Positions to drive through, from the vehicle's current position.</param>
    /// <param name="fallbackHeadingDeg">Heading of the vehicle when the path has no length.</param>
    public VehicleDrive(IReadOnlyList<(double Latitude, double Longitude)> path, double seconds, double headingOffsetDeg, double fallbackHeadingDeg = 0)
    {
        if (path.Count == 0)
        {
            throw new ArgumentException("A drive needs at least one position.", nameof(path));
        }

        (_originLatitude, _originLongitude) = path[0];
        _points = path.Select(p => Geo.ToLocal(_originLatitude, _originLongitude, p.Latitude, p.Longitude)).ToArray();
        _distanceAt = new double[_points.Length];
        for (var i = 1; i < _points.Length; i++)
        {
            _distanceAt[i] = _distanceAt[i - 1] + (_points[i] - _points[i - 1]).Length;
        }

        Seconds = Math.Max(0, seconds);
        HeadingOffsetDeg = headingOffsetDeg;
        _fallbackHeadingDeg = fallbackHeadingDeg;
    }

    /// <summary>A drive whose time follows from its length: <paramref name="averageSpeedMps"/> on average.</summary>
    public static VehicleDrive AtSpeed(
        IReadOnlyList<(double Latitude, double Longitude)> path, double averageSpeedMps, double headingOffsetDeg, double fallbackHeadingDeg = 0)
    {
        var length = 0.0;
        for (var i = 1; i < path.Count; i++)
        {
            length += Geo.ToLocal(path[i - 1].Latitude, path[i - 1].Longitude, path[i].Latitude, path[i].Longitude).Length;
        }

        return new VehicleDrive(path, averageSpeedMps > 0 ? length / averageSpeedMps : 0, headingOffsetDeg, fallbackHeadingDeg);
    }

    public double Seconds { get; }
    public double HeadingOffsetDeg { get; }
    public double LengthMeters => _distanceAt[^1];

    public bool IsDone(double elapsedSeconds) => elapsedSeconds >= Seconds;

    public (double Latitude, double Longitude, double HeadingTrueDeg) At(double elapsedSeconds)
    {
        var t = Seconds <= 0 ? 1 : Math.Clamp(elapsedSeconds / Seconds, 0, 1);
        var eased = Math.Sin(Math.PI / 2 * t);
        var distance = LengthMeters * eased * eased;

        var position = PointAt(distance);
        var direction = PointAt(distance + HeadingSpanMeters) - PointAt(distance - HeadingSpanMeters);
        var heading = direction.Length > 1e-6 ? direction.HeadingDeg : _fallbackHeadingDeg - HeadingOffsetDeg;
        var (lat, lon) = Geo.FromLocal(_originLatitude, _originLongitude, position);
        return (lat, lon, Geo.NormalizeHeading(heading + HeadingOffsetDeg));
    }

    private Vec2 PointAt(double distance)
    {
        if (distance <= 0 || _points.Length == 1)
        {
            return _points[0];
        }

        if (distance >= LengthMeters)
        {
            return _points[^1];
        }

        var i = Array.BinarySearch(_distanceAt, distance);
        if (i >= 0)
        {
            return _points[i];
        }

        i = ~i; // first point further along
        var t = (distance - _distanceAt[i - 1]) / (_distanceAt[i] - _distanceAt[i - 1]);
        return _points[i - 1] + (_points[i] - _points[i - 1]) * t;
    }
}
