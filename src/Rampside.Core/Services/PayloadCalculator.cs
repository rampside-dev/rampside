using Rampside.Core.FlightPlans;
using Rampside.Core.Profiles;

namespace Rampside.Core.Services;

/// <summary>What is on board right now: passengers (each with the same mass) and everything in the holds.</summary>
public sealed record PayloadLoad(int Passengers, double PassengerMassKg, double CargoKg);

/// <summary>
/// The full load of one boarding. While passengers board or leave, the holds are loaded or unloaded
/// in step with them, so <see cref="At"/> scales the cargo by the share of passengers on board.
/// </summary>
public sealed record LoadPlan(int Passengers, double PassengerMassKg, double CargoKg)
{
    /// <summary>Profile weights: one bag per passenger, no freight.</summary>
    public static LoadPlan FromProfile(PassengerConfig config, int passengers) =>
        new(passengers, config.WeightKg, passengers * config.BagWeightKg);

    /// <summary>
    /// Masses from the flight plan, so the zero fuel weight matches it. With fewer passengers than planned
    /// the bags shrink with them; the freight stays.
    /// </summary>
    public static LoadPlan FromFlightPlan(FlightPlan plan, int passengers)
    {
        var bags = plan.Passengers > 0 ? plan.BagsKg * passengers / plan.Passengers : plan.BagsKg;
        return new LoadPlan(passengers, plan.PassengerMassKg, plan.FreightKg + bags);
    }

    public PayloadLoad At(double passengersOnBoard)
    {
        var share = Passengers > 0 ? Math.Clamp(passengersOnBoard / Passengers, 0, 1) : 0;
        return new PayloadLoad((int)Math.Round(passengersOnBoard), PassengerMassKg, CargoKg * share);
    }
}

/// <summary>Turns a load into weights per payload station.</summary>
public static class PayloadCalculator
{
    public static IReadOnlyDictionary<int, double> Compute(AircraftProfile profile, PayloadLoad load)
    {
        var result = new Dictionary<int, double>();
        Spread(profile, PayloadKind.Passengers, load.Passengers * load.PassengerMassKg, result);
        Spread(profile, PayloadKind.Cargo, load.CargoKg, result);
        return result;
    }

    private static void Spread(AircraftProfile profile, PayloadKind kind, double massKg, Dictionary<int, double> result)
    {
        var stations = profile.PayloadStations.Where(s => s.Kind == kind && s.Share > 0).ToList();
        var totalShare = stations.Sum(s => s.Share);
        if (totalShare <= 0)
        {
            return;
        }

        foreach (var station in stations)
        {
            result[station.Index] = result.GetValueOrDefault(station.Index) + massKg * station.Share / totalShare;
        }
    }
}
