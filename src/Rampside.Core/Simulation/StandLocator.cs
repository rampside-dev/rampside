namespace Rampside.Core.Simulation;

/// <summary>One TAXI_PARKING entry of an airport's facility data.</summary>
/// <param name="Index">Position in the airport's parking list; jetways refer to stands by this index.</param>
/// <param name="NameCode">MSFS parking name code (10 = GATE, 1 = PARKING, ...).</param>
public sealed record ParkingSpot(
    int Index,
    int NameCode,
    uint Number,
    int Suffix,
    double HeadingDeg,
    double RadiusMeters,
    double Latitude,
    double Longitude)
{
    public string DisplayName => StandNames.Format(NameCode, Number, Suffix);
}

public static class StandLocator
{
    /// <summary>How far the aircraft reference point may be outside the parking circle and still count as parked there.</summary>
    public const double ToleranceMeters = 15;

    /// <summary>Nearest parking spot whose circle (plus tolerance) contains the aircraft, or null.</summary>
    public static (ParkingSpot Spot, double DistanceMeters)? FindStand(IEnumerable<ParkingSpot> spots, double latitude, double longitude)
    {
        (ParkingSpot Spot, double DistanceMeters)? best = null;
        foreach (var spot in spots)
        {
            var distance = DistanceMeters(latitude, longitude, spot.Latitude, spot.Longitude);
            if (distance <= spot.RadiusMeters + ToleranceMeters && (best is null || distance < best.Value.DistanceMeters))
            {
                best = (spot, distance);
            }
        }

        return best;
    }

    /// <summary>Short-range distance (equirectangular), accurate enough on an apron.</summary>
    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var dy = (lat2 - lat1) * Units.MetersPerDegreeLatitude;
        var dx = (lon2 - lon1) * Units.MetersPerDegreeLatitude * Math.Cos(Units.DegreesToRadians(lat1));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Position of a parking spot from the airport reference point and its BIAS_X (east) / BIAS_Z (north) offsets in meters.</summary>
    public static (double Latitude, double Longitude) Offset(double airportLat, double airportLon, double biasXMeters, double biasZMeters) =>
        (airportLat + biasZMeters / Units.MetersPerDegreeLatitude,
         airportLon + biasXMeters / (Units.MetersPerDegreeLatitude * Math.Cos(Units.DegreesToRadians(airportLat))));
}

/// <summary>Turns MSFS parking name codes into readable stand names.</summary>
public static class StandNames
{
    private static readonly string[] Directions = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

    // Codes as in the MSFS/FSX airport format: 0 NONE, 1 PARKING, 2..9 N..NW_PARKING, 10 GATE, 11 DOCK, 12..37 GATE_A..GATE_Z.
    // Code 10 = GATE is confirmed (EDDM stand 121). Suffix letters (1 = A ...) are an assumption.
    public static string Format(int nameCode, uint number, int suffix)
    {
        var name = nameCode switch
        {
            1 => $"Parking {number}",
            >= 2 and <= 9 => $"{Directions[nameCode - 2]} Parking {number}",
            10 => $"Gate {number}",
            11 => $"Dock {number}",
            >= 12 and <= 37 => $"Gate {(char)('A' + nameCode - 12)}{number}",
            _ => $"Stand {number}",
        };

        return suffix is >= 1 and <= 26 ? $"{name}{(char)('A' + suffix - 1)}" : name;
    }
}
