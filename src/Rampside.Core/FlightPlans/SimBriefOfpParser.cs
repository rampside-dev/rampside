using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Rampside.Core.FlightPlans;

public sealed class SimBriefException : Exception
{
    public SimBriefException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Reads the XML of a SimBrief OFP (xml.fetcher.php). Weights are in the plan's units ("kgs" or "lbs"),
/// times in Unix seconds. Checked against a real OFP (30 Oct 2026):
/// payload = pax_count_actual × pax_weight + cargo, and est_zfw = oew + payload.
/// </summary>
public static class SimBriefOfpParser
{
    public const string SourceName = "SimBrief";

    public static FlightPlan Parse(string xml)
    {
        XElement root;
        try
        {
            root = XElement.Parse(xml);
        }
        catch (XmlException ex)
        {
            throw new SimBriefException("SimBrief sent no valid flight plan.", ex);
        }

        var status = root.Element("fetch")?.Element("status")?.Value.Trim();
        if (root.Name.LocalName != "OFP" || (status is not null && !status.Equals("Success", StringComparison.OrdinalIgnoreCase)))
        {
            throw new SimBriefException($"SimBrief: {(string.IsNullOrEmpty(status) ? "no flight plan found" : status)}");
        }

        var units = Text(root, "params", "units") ?? "kgs";
        var toKg = units.StartsWith("lb", StringComparison.OrdinalIgnoreCase) ? 1 / Units.PoundsPerKilogram : 1.0;
        double Kg(string section, string field) => Number(root, section, field) * toKg;

        var passengers = (int)Math.Round(NumberOrNull(root, "weights", "pax_count_actual") ?? Number(root, "weights", "pax_count"));
        var bags = (int)Math.Round(NumberOrNull(root, "weights", "bag_count_actual") ?? Number(root, "weights", "bag_count"));
        var airline = Text(root, "general", "icao_airline");
        var flight = Text(root, "general", "flight_number");

        return new FlightPlan(
            Source: SourceName,
            Id: Text(root, "params", "request_id") ?? string.Empty,
            GeneratedAt: UnixTime(root, "params", "time_generated") ?? DateTimeOffset.MinValue,
            FlightNumber: flight is null ? null : $"{airline}{flight}",
            Origin: Required(root, "origin", "icao_code").ToUpperInvariant(),
            Destination: Required(root, "destination", "icao_code").ToUpperInvariant(),
            AircraftIcao: Required(root, "aircraft", "icaocode").ToUpperInvariant(),
            Registration: Text(root, "aircraft", "reg"),
            Passengers: passengers,
            PassengerMassKg: Kg("weights", "pax_weight"),
            Bags: bags,
            CargoKg: Kg("weights", "cargo"),
            FreightKg: Kg("weights", "freight_added"),
            BlockFuelKg: Kg("fuel", "plan_ramp"),
            EmptyWeightKg: Kg("weights", "oew"),
            ZeroFuelWeightKg: Kg("weights", "est_zfw"),
            ScheduledOffBlock: UnixTime(root, "times", "sched_out"));
    }

    private static string? Text(XElement root, string section, string field)
    {
        var value = root.Element(section)?.Element(field)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string Required(XElement root, string section, string field) =>
        Text(root, section, field) ?? throw new SimBriefException($"SimBrief flight plan has no {section}/{field}.");

    private static double? NumberOrNull(XElement root, string section, string field) =>
        Text(root, section, field) is { } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static double Number(XElement root, string section, string field) => NumberOrNull(root, section, field) ?? 0;

    private static DateTimeOffset? UnixTime(XElement root, string section, string field) =>
        NumberOrNull(root, section, field) is { } seconds and > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)seconds) : null;
}
