namespace Rampside.Core;

/// <summary>Small flat-earth helpers, accurate enough for distances around one aircraft stand.</summary>
public static class Geo
{
    /// <summary>
    /// Moves a position by <paramref name="forwardM"/> along <paramref name="headingTrueDeg"/>
    /// and by <paramref name="rightM"/> to the right of it.
    /// </summary>
    public static (double Latitude, double Longitude) Offset(
        double latitude, double longitude, double headingTrueDeg, double forwardM, double rightM)
    {
        var heading = Units.DegreesToRadians(headingTrueDeg);
        var northM = forwardM * Math.Cos(heading) - rightM * Math.Sin(heading);
        var eastM = forwardM * Math.Sin(heading) + rightM * Math.Cos(heading);

        return (
            latitude + northM / Units.MetersPerDegreeLatitude,
            longitude + eastM / (Units.MetersPerDegreeLatitude * Math.Cos(Units.DegreesToRadians(latitude))));
    }

    /// <summary>Inverse of <see cref="Offset"/>: metres forward of and to the right of a position facing <paramref name="headingTrueDeg"/>.</summary>
    public static (double ForwardM, double RightM) Relative(
        double originLat, double originLon, double headingTrueDeg, double latitude, double longitude)
    {
        var local = ToLocal(originLat, originLon, latitude, longitude);
        var heading = Units.DegreesToRadians(headingTrueDeg);
        return (
            local.X * Math.Sin(heading) + local.Y * Math.Cos(heading),
            local.X * Math.Cos(heading) - local.Y * Math.Sin(heading));
    }

    public static double NormalizeHeading(double degrees) => ((degrees % 360) + 360) % 360;

    /// <summary>Signed difference <paramref name="toDeg"/> − <paramref name="fromDeg"/> in −180..180.</summary>
    public static double HeadingDifference(double fromDeg, double toDeg) => NormalizeHeading(toDeg - fromDeg + 180) - 180;

    /// <summary>Position relative to an origin in metres (X = east, Y = north).</summary>
    public static Vec2 ToLocal(double originLat, double originLon, double latitude, double longitude) =>
        new((longitude - originLon) * Units.MetersPerDegreeLatitude * Math.Cos(Units.DegreesToRadians(originLat)),
            (latitude - originLat) * Units.MetersPerDegreeLatitude);

    public static (double Latitude, double Longitude) FromLocal(double originLat, double originLon, Vec2 local) =>
        (originLat + local.Y / Units.MetersPerDegreeLatitude,
         originLon + local.X / (Units.MetersPerDegreeLatitude * Math.Cos(Units.DegreesToRadians(originLat))));
}
