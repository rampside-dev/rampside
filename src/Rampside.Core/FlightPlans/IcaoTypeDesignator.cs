using System.Text.RegularExpressions;

namespace Rampside.Core.FlightPlans;

/// <summary>Gets an ICAO type designator ("B738") out of the sim's ATC MODEL variable.</summary>
public static partial class IcaoTypeDesignator
{
    /// <summary>
    /// ATC MODEL is set by the aircraft's aircraft.cfg (atc_model). Seen forms: a plain designator ("B738"),
    /// a localization key ("TT:ATCCOM.AC_MODEL_B738.0.text") or "$$:B738". Returns null if none fits.
    /// The exact MSFS 2024 output still needs checking per aircraft.
    /// </summary>
    public static string? FromAtcModel(string? atcModel)
    {
        if (string.IsNullOrWhiteSpace(atcModel))
        {
            return null;
        }

        var value = atcModel.Trim().ToUpperInvariant();
        var key = AcModelKey().Match(value);
        if (key.Success)
        {
            return key.Groups[1].Value;
        }

        if (value.StartsWith("$$:", StringComparison.Ordinal))
        {
            value = value[3..];
        }

        return Designator().IsMatch(value) ? value : null;
    }

    [GeneratedRegex(@"AC_MODEL[ _]([A-Z0-9]{2,4})\b")]
    private static partial Regex AcModelKey();

    // ICAO Doc 8643: 2-4 characters, starting with a letter.
    [GeneratedRegex(@"^[A-Z][A-Z0-9]{1,3}$")]
    private static partial Regex Designator();
}
