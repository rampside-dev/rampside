using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rampside.Core.Profiles;

public sealed class ProfileRepository
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly List<AircraftProfile> _profiles;

    public ProfileRepository(IEnumerable<AircraftProfile> profiles)
    {
        _profiles = profiles.ToList();
    }

    public IReadOnlyList<AircraftProfile> Profiles => _profiles;

    /// <summary>Loads every *.json file in <paramref name="directory"/>. Broken files are reported and skipped.</summary>
    public static ProfileRepository LoadFromDirectory(string directory, Action<string>? onError = null)
    {
        var profiles = new List<AircraftProfile>();
        if (!Directory.Exists(directory))
        {
            onError?.Invoke($"Profile directory not found: {directory}");
            return new ProfileRepository(profiles);
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var profile = JsonSerializer.Deserialize<AircraftProfile>(File.ReadAllText(file), JsonOptions);
                if (profile is not null)
                {
                    profiles.Add(profile);
                }
            }
            catch (JsonException ex)
            {
                onError?.Invoke($"Skipping profile {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        return new ProfileRepository(profiles);
    }

    /// <summary>Returns the first profile whose match list hits the aircraft title, else the generic one.</summary>
    public AircraftProfile Resolve(string aircraftTitle)
    {
        foreach (var profile in _profiles)
        {
            if (profile.Match.Any(m => !string.IsNullOrWhiteSpace(m)
                && aircraftTitle.Contains(m, StringComparison.OrdinalIgnoreCase)))
            {
                return profile;
            }
        }

        return _profiles.FirstOrDefault(p => p.Id == "generic") ?? new AircraftProfile();
    }
}
