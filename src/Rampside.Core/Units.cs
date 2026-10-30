namespace Rampside.Core;

/// <summary>Unit conversions. Rampside.Core works in SI units (kg, m, s); the bridges convert.</summary>
public static class Units
{
    public const double PoundsPerKilogram = 2.2046226218;
    public const double FeetPerMeter = 3.280839895;
    public const double MetersPerSecondPerKnot = 0.514444;
    public const double MetersPerDegreeLatitude = 111_320.0;

    public static double KilogramsToPounds(double kg) => kg * PoundsPerKilogram;
    public static double PoundsToKilograms(double lb) => lb / PoundsPerKilogram;
    public static double MetersToFeet(double m) => m * FeetPerMeter;
    public static double DegreesToRadians(double deg) => deg * Math.PI / 180.0;
    public static double RadiansToDegrees(double rad) => rad * 180.0 / Math.PI;
}
