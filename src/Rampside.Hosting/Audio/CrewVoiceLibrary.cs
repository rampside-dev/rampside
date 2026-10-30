using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rampside.Hosting.Audio;

/// <summary>
/// The ground crew clips on disk: one folder per voice (audio/groundcrew/&lt;voice&gt;/), one file per phrase
/// variant (<c>push_start_1.wav</c>, <c>push_start_2.wav</c>, <c>facing_north.wav</c>), and an optional
/// phrases.json with the spoken text per file name, used for the log.
/// </summary>
public sealed partial class CrewVoiceLibrary
{
    private readonly Dictionary<string, Dictionary<string, string[]>> _voices;
    private readonly Dictionary<string, string> _texts;
    private readonly ConcurrentDictionary<string, WavClip> _cache = new();

    private CrewVoiceLibrary(Dictionary<string, Dictionary<string, string[]>> voices, Dictionary<string, string> texts)
    {
        _voices = voices;
        _texts = texts;
    }

    public static string DefaultFolder { get; } = Path.Combine(AppContext.BaseDirectory, "audio", "groundcrew");

    public IReadOnlyCollection<string> Voices => _voices.Keys;

    public bool IsEmpty => _voices.Count == 0;

    /// <summary>Scans <paramref name="folder"/>; a missing folder gives an empty library.</summary>
    public static CrewVoiceLibrary Load(string folder)
    {
        var voices = new Dictionary<string, Dictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase);
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(folder))
        {
            return new CrewVoiceLibrary(voices, texts);
        }

        foreach (var voiceFolder in Directory.GetDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            var phrases = Directory.GetFiles(voiceFolder, "*.wav")
                .GroupBy(file => PhraseOf(Path.GetFileNameWithoutExtension(file)), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Order(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
            if (phrases.Count > 0)
            {
                voices[Path.GetFileName(voiceFolder)] = phrases;
            }
        }

        var textFile = Path.Combine(folder, "phrases.json");
        if (File.Exists(textFile))
        {
            try
            {
                foreach (var (name, text) in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(textFile)) ?? new())
                {
                    texts[name] = text;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Texts are only for the log; play without them.
            }
        }

        return new CrewVoiceLibrary(voices, texts);
    }

    /// <summary>"push_start_2" → "push_start"; "facing_north" stays.</summary>
    public static string PhraseOf(string fileName) => VariantSuffix().Replace(fileName, string.Empty);

    /// <summary>Clip files of <paramref name="phrase"/> spoken by <paramref name="voice"/>, empty when there are none.</summary>
    public IReadOnlyList<string> Variants(string voice, string phrase) =>
        _voices.TryGetValue(voice, out var phrases) && phrases.TryGetValue(phrase, out var files) ? files : Array.Empty<string>();

    /// <summary>Spoken text of a clip file, or null when phrases.json does not list it.</summary>
    public string? Text(string file) => _texts.GetValueOrDefault(Path.GetFileNameWithoutExtension(file));

    public WavClip Clip(string file) => _cache.GetOrAdd(file, WavClip.Load);

    [GeneratedRegex(@"_\d+$")]
    private static partial Regex VariantSuffix();
}
