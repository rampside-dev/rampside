namespace Rampside.Core;

/// <summary>A point or direction in a local flat frame: X = east, Y = north, in metres.</summary>
public readonly record struct Vec2(double X, double Y)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);
    public static Vec2 operator *(Vec2 a, double k) => new(a.X * k, a.Y * k);

    public double Length => Math.Sqrt(X * X + Y * Y);
    public Vec2 Normalized => Length > 0 ? this * (1 / Length) : this;

    /// <summary>Unit vector to the left (counter-clockwise).</summary>
    public Vec2 LeftNormal => new(-Y, X);

    /// <summary>True heading of this direction in degrees (0 = north, 90 = east).</summary>
    public double HeadingDeg => Geo.NormalizeHeading(Units.RadiansToDegrees(Math.Atan2(X, Y)));

    public static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;

    /// <summary>Positive when <paramref name="b"/> is counter-clockwise (left) of <paramref name="a"/>.</summary>
    public static double Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;

    /// <summary>Unit vector pointing along a true heading.</summary>
    public static Vec2 FromHeading(double headingDeg)
    {
        var rad = Units.DegreesToRadians(headingDeg);
        return new Vec2(Math.Sin(rad), Math.Cos(rad));
    }

    /// <summary>Rotates counter-clockwise by <paramref name="radians"/>.</summary>
    public Vec2 Rotate(double radians)
    {
        var (sin, cos) = Math.SinCos(radians);
        return new Vec2(X * cos - Y * sin, X * sin + Y * cos);
    }
}
