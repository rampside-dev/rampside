using Rampside.Core.Simulation;

namespace Rampside.Core.Services;

/// <summary>Splits a total fuel load across tanks: wing tanks first (evenly by capacity), then the center tank.</summary>
public static class FuelPlanner
{
    private static readonly FuelTankId[][] FillOrder =
    {
        new[] { FuelTankId.LeftMain, FuelTankId.RightMain },
        new[] { FuelTankId.Center },
    };

    public static IReadOnlyDictionary<FuelTankId, double> Distribute(IReadOnlyList<FuelTankState> tanks, double totalKg)
    {
        var result = tanks.ToDictionary(t => t.Id, _ => 0.0);
        var remaining = Math.Clamp(totalKg, 0, tanks.Sum(t => t.CapacityKg));

        foreach (var group in FillOrder)
        {
            var groupTanks = tanks.Where(t => group.Contains(t.Id) && t.CapacityKg > 0).ToList();
            var groupCapacity = groupTanks.Sum(t => t.CapacityKg);
            if (groupCapacity <= 0)
            {
                continue;
            }

            var amount = Math.Min(remaining, groupCapacity);
            foreach (var tank in groupTanks)
            {
                result[tank.Id] = amount * tank.CapacityKg / groupCapacity;
            }

            remaining -= amount;
        }

        return result;
    }
}
