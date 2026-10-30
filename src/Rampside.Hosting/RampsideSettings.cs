using System.Text.Json;

namespace Rampside.Hosting;

/// <summary>
/// User settings shared by the desktop window and the console, stored as JSON in
/// %APPDATA%\Rampside\settings.json. Command-line options win over the file.
/// </summary>
public sealed class RampsideSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rampside", "settings.json");

    /// <summary>SimBrief pilot ID (a number) or username, or null when not set.</summary>
    public string? SimBriefUser { get; set; }

    /// <summary>Whether the ground crew talks to the cockpit (pushback).</summary>
    public bool CrewVoice { get; set; } = true;

    /// <summary>Crew voice volume, 0..1.</summary>
    public double CrewVoiceVolume { get; set; } = 0.8;

    /// <summary>Reads the settings; a missing or broken file gives defaults (and an error text for broken files).</summary>
    public static RampsideSettings Load(string? path, out string? error)
    {
        path ??= DefaultPath;
        error = null;
        if (!File.Exists(path))
        {
            return new RampsideSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<RampsideSettings>(File.ReadAllText(path), JsonOptions) ?? new RampsideSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = $"Settings in {path} could not be read ({ex.Message}); using defaults.";
            return new RampsideSettings();
        }
    }

    /// <summary>Writes the settings through a temporary file, so a crash never leaves half a file.</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Trimmed value, or null for empty input.</summary>
    public static string? Normalize(string? simBriefUser) =>
        string.IsNullOrWhiteSpace(simBriefUser) ? null : simBriefUser.Trim();
}
