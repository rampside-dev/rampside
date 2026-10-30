using System.Globalization;

namespace Rampside.Desktop.ViewModels;

/// <summary>Number formatting for the UI.</summary>
internal static class Format
{
    public const string None = "–";

    // Thin space as thousands separator: "12 500 kg" reads the same in every locale ("12,500" or "12.500" do not).
    private static readonly NumberFormatInfo Numbers = new()
    {
        NumberGroupSeparator = " ",
        NumberDecimalSeparator = ".",
    };

    public static string Whole(double value) => value.ToString("N0", Numbers);

    public static string Decimal(double value, int digits = 1) => value.ToString("F" + digits, Numbers);

    /// <summary>Parses a whole number typed by the user; spaces (also as thousands separators) are ignored.</summary>
    public static bool TryParseWhole(string? text, out int value)
    {
        var digits = new string((text ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>"forward left" → "Forward left".</summary>
    public static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
