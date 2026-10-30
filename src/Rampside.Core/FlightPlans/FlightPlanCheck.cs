using System.Globalization;
using Rampside.Core.Profiles;
using Rampside.Core.Simulation;

namespace Rampside.Core.FlightPlans;

/// <summary>
/// Result of comparing a flight plan with the aircraft in the sim.
/// Blockers stop the plan from being used (unless forced), warnings are only shown.
/// </summary>
public sealed record FlightPlanReview(
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> NotChecked)
{
    public bool IsBlocked => Blockers.Count > 0;
}

public static class FlightPlanCheck
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>Empty weight difference between plan and sim that is still fine.</summary>
    public const double EmptyWeightToleranceKg = 500;

    public static FlightPlanReview Review(FlightPlan plan, AircraftState state, AircraftProfile profile, DateTimeOffset now)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();
        var notChecked = new List<string>();

        var simTypes = AircraftTypes(state, profile);
        if (simTypes.Count == 0)
        {
            notChecked.Add("aircraft type (add \"icaoTypes\" to the profile)");
        }
        else if (!simTypes.Contains(plan.AircraftIcao, StringComparer.OrdinalIgnoreCase))
        {
            blockers.Add($"The plan is for {plan.AircraftIcao}, the aircraft in the sim is {string.Join("/", simTypes)}.");
        }

        var airport = state.Stand?.AirportIcao ?? state.AirportIcao;
        if (string.IsNullOrEmpty(airport))
        {
            notChecked.Add("departure airport (no airport found near the aircraft)");
        }
        else if (!string.Equals(airport, plan.Origin, StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add($"The plan departs from {plan.Origin}, the aircraft is at {airport.ToUpperInvariant()}.");
        }

        if (plan.GeneratedAt > DateTimeOffset.MinValue && now - plan.GeneratedAt > MaxAge)
        {
            warnings.Add($"The plan is {(now - plan.GeneratedAt).TotalHours:F0} h old.");
        }

        if (plan.Passengers > profile.Passengers.Capacity)
        {
            warnings.Add($"{plan.Passengers} passengers planned, the profile seats {profile.Passengers.Capacity}; only {profile.Passengers.Capacity} will board.");
        }

        if (state.FuelCapacityKg > 0 && plan.BlockFuelKg > state.FuelCapacityKg)
        {
            warnings.Add(Invariant($"Block fuel {plan.BlockFuelKg:F0} kg is more than the tanks hold ({state.FuelCapacityKg:F0} kg)."));
        }

        if (state.EmptyWeightKg > 0 && plan.EmptyWeightKg > 0
            && Math.Abs(state.EmptyWeightKg - plan.EmptyWeightKg) > EmptyWeightToleranceKg)
        {
            var difference = state.EmptyWeightKg - plan.EmptyWeightKg;
            warnings.Add(Invariant(
                $"Empty weight in the sim is {state.EmptyWeightKg:F0} kg, the plan uses {plan.EmptyWeightKg:F0} kg: the zero fuel weight will be {Math.Abs(difference):F0} kg {(difference > 0 ? "higher" : "lower")} than planned."));
        }

        return new FlightPlanReview(blockers, warnings, notChecked);
    }

    /// <summary>ICAO types the sim aircraft can be: the profile's list, else what the sim reports.</summary>
    public static IReadOnlyList<string> AircraftTypes(AircraftState state, AircraftProfile profile)
    {
        if (profile.IcaoTypes.Count > 0)
        {
            return profile.IcaoTypes;
        }

        return IcaoTypeDesignator.FromAtcModel(state.AtcModel) is { } type ? new[] { type } : Array.Empty<string>();
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
