using System.Globalization;

namespace Rampside.Core.FlightPlans;

/// <summary>Short texts about a flight plan for messages and status lines.</summary>
public static class FlightPlanFormat
{
    /// <summary>"SimBrief plan UAE7KP WSSS-YSSY (B77W)"</summary>
    public static string Title(FlightPlan plan) =>
        $"{plan.Source} plan {(plan.FlightNumber is { } flight ? flight + " " : string.Empty)}{plan.Route} ({plan.AircraftIcao})";

    /// <summary>"296 pax, 7281 kg cargo (0 kg freight), block fuel 61526 kg, ZFW 198318 kg"</summary>
    public static string Load(FlightPlan plan) => string.Create(CultureInfo.InvariantCulture,
        $"{plan.Passengers} pax, {plan.CargoKg:F0} kg cargo ({plan.FreightKg:F0} kg freight), block fuel {plan.BlockFuelKg:F0} kg, ZFW {plan.ZeroFuelWeightKg:F0} kg");
}
