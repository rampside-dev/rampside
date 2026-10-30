namespace Rampside.Core.FlightPlans;

/// <summary>
/// The load sheet part of a flight plan, in kg. Today it comes from SimBrief; other planners can fill it too.
/// </summary>
/// <param name="Id">Identifies one generated plan (SimBrief request id), to notice a new plan.</param>
/// <param name="AircraftIcao">ICAO type designator, e.g. "B738".</param>
/// <param name="Passengers">Passengers on the load sheet.</param>
/// <param name="PassengerMassKg">Mass of one passenger without bags.</param>
/// <param name="CargoKg">Everything in the holds: bags plus freight.</param>
/// <param name="FreightKg">Freight part of <paramref name="CargoKg"/>.</param>
/// <param name="EmptyWeightKg">Operating empty weight the plan assumes, 0 when unknown.</param>
public sealed record FlightPlan(
    string Source,
    string Id,
    DateTimeOffset GeneratedAt,
    string? FlightNumber,
    string Origin,
    string Destination,
    string AircraftIcao,
    string? Registration,
    int Passengers,
    double PassengerMassKg,
    int Bags,
    double CargoKg,
    double FreightKg,
    double BlockFuelKg,
    double EmptyWeightKg,
    double ZeroFuelWeightKg,
    DateTimeOffset? ScheduledOffBlock)
{
    public double BagsKg => Math.Max(0, CargoKg - FreightKg);
    public double PayloadKg => Passengers * PassengerMassKg + CargoKg;
    public string Route => $"{Origin}-{Destination}";
}
