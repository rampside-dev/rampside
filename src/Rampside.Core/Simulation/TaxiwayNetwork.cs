namespace Rampside.Core.Simulation;

/// <summary>TAXI_PATH types of the MSFS airport facility data.</summary>
public enum TaxiPathType
{
    None = 0,
    Taxi = 1,
    Runway = 2,
    Parking = 3,
    Path = 4,
    Closed = 5,
    Vehicle = 6,
    Road = 7,
    PaintedLine = 8,
}

/// <summary>One TAXI_POINT of an airport.</summary>
public sealed record TaxiPoint(double Latitude, double Longitude);

/// <summary>One TAXI_PATH of an airport. <see cref="Start"/> and <see cref="End"/> index <see cref="TaxiwayNetwork.Points"/>
/// (for <see cref="TaxiPathType.Parking"/> paths, <see cref="End"/> is a parking index instead).</summary>
/// <param name="Name">Taxiway name such as "N", or empty.</param>
public sealed record TaxiPath(TaxiPathType Type, int Start, int End, string Name, double WidthMeters)
{
    /// <summary>Lines an aircraft can be pushed onto: taxiways and apron taxi lanes.</summary>
    public bool IsTaxiLine => Type is TaxiPathType.Taxi or TaxiPathType.Path;
}

/// <summary>The taxiway graph of one airport, as the scenery defines it.</summary>
public sealed record TaxiwayNetwork(IReadOnlyList<TaxiPoint> Points, IReadOnlyList<TaxiPath> Paths)
{
    public static TaxiwayNetwork Empty { get; } = new(Array.Empty<TaxiPoint>(), Array.Empty<TaxiPath>());

    /// <summary>Taxi lines as point pairs, skipping paths with invalid point indexes.</summary>
    public IEnumerable<(TaxiPoint A, TaxiPoint B, TaxiPath Path)> TaxiLines()
    {
        foreach (var path in Paths)
        {
            if (path.IsTaxiLine
                && path.Start >= 0 && path.Start < Points.Count
                && path.End >= 0 && path.End < Points.Count
                && path.Start != path.End)
            {
                yield return (Points[path.Start], Points[path.End], path);
            }
        }
    }
}
