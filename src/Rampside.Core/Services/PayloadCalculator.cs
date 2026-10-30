using Rampside.Core.Profiles;

namespace Rampside.Core.Services;

/// <summary>Turns a passenger count into weights per payload station (passengers + their bags).</summary>
public static class PayloadCalculator
{
    public static IReadOnlyDictionary<int, double> Compute(AircraftProfile profile, int passengers)
    {
        var result = new Dictionary<int, double>();
        Spread(profile, PayloadKind.Passengers, passengers * profile.Passengers.WeightKg, result);
        Spread(profile, PayloadKind.Cargo, passengers * profile.Passengers.BagWeightKg, result);
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
