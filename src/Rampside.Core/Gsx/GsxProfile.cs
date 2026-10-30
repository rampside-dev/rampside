using System.Globalization;

namespace Rampside.Core.Gsx;

/// <summary>A position from a GSX profile. <see cref="HeadingDeg"/> is the aircraft heading (true) there, null when the profile leaves it out.</summary>
public readonly record struct GsxPose(double Latitude, double Longitude, double? HeadingDeg);

/// <summary>
/// One pushback target of a stand: the aircraft reference point is pushed through <see cref="Waypoints"/>
/// and stops at <see cref="Final"/>.
/// </summary>
/// <param name="StartEnginesPercent">How far into the pushback (0..100 %) engine start is allowed, when the profile says.</param>
public sealed record GsxPushbackTarget(
    string Label,
    IReadOnlyList<GsxPose> Waypoints,
    GsxPose Final,
    int? Wingwalkers = null,
    double? StartEnginesPercent = null);

/// <summary>One stand ("gate"/"parking" section) of a GSX airport profile.</summary>
/// <param name="Section">Section name as in the file, e.g. "gate a 11".</param>
/// <param name="ParkingPosition">The MSFS parking spot this section belongs to (<c>this_parking_pos</c>); missing in older profiles.</param>
/// <param name="StopPosition">Where the aircraft stops (<c>parkingsystem_stopposition</c>).</param>
public sealed record GsxStand(
    string Section,
    string Name,
    GsxPose? ParkingPosition,
    GsxPose StopPosition,
    double? MaxWingspanMeters,
    bool HasJetway,
    IReadOnlyList<GsxPushbackTarget> PushbackTargets);

/// <summary>
/// A GSX Pro airport profile (<c>%APPDATA%\Virtuali\GSX\MSFS\&lt;icao&gt;-&lt;name&gt;.ini</c>), reduced to what Rampside uses:
/// stand positions and the named pushback targets. The profile's Python companion file is not read.
/// </summary>
public sealed record GsxProfile(string Name, IReadOnlyList<GsxStand> Stands)
{
    /// <summary>Reads a profile. Broken stand sections are skipped and listed in <paramref name="problems"/>.</summary>
    public static GsxProfile Parse(string name, string text, ICollection<string>? problems = null)
    {
        var stands = new List<GsxStand>();
        foreach (var (section, values) in ReadIni(text))
        {
            if (!values.ContainsKey("parkingsystem_stopposition") || values.GetValueOrDefault("is_deicearea") == "1")
            {
                continue; // general settings, jetway heights, de-ice pads
            }

            try
            {
                stands.Add(ReadStand(section, values));
            }
            catch (FormatException ex)
            {
                problems?.Add($"[{section}]: {ex.Message}");
            }
        }

        return new GsxProfile(name, stands);
    }

    /// <summary>"gate a 11" → "Gate A11", "gate 121" → "Gate 121", "parking 701" → "Parking 701".</summary>
    public static string StandName(string section)
    {
        var parts = section.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && parts[1].Length == 1 && char.IsLetter(parts[1][0]))
        {
            return $"{Capitalize(parts[0])} {parts[1].ToUpperInvariant()}{parts[2]}";
        }

        return string.Join(' ', parts.Select(Capitalize));
    }

    private static string Capitalize(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];

    private static GsxStand ReadStand(string section, IReadOnlyDictionary<string, string> values)
    {
        var targets = new List<GsxPushbackTarget>();
        var labels = values.GetValueOrDefault("pushbacklabels")?.Split('|') ?? Array.Empty<string>();

        // "pushback" is a bit mask: 1 = custom left target, 2 = custom right target.
        var mask = (int)(Number(values.GetValueOrDefault("pushback")) ?? 0);
        if ((mask & 1) != 0 && Side(values, "left", Label(labels, 0, "Tail left")) is { } left)
        {
            targets.Add(left with
            {
                Wingwalkers = Int(values.GetValueOrDefault("wingwalkersleftpushback")),
                StartEnginesPercent = Number(values.GetValueOrDefault("startenginesleftpushback")),
            });
        }

        if ((mask & 2) != 0 && Side(values, "right", Label(labels, 1, "Tail right")) is { } right)
        {
            targets.Add(right with
            {
                Wingwalkers = Int(values.GetValueOrDefault("wingwalkersrightpushback")),
                StartEnginesPercent = Number(values.GetValueOrDefault("startenginesrightpushback")),
            });
        }

        if (values.GetValueOrDefault("pushbackaddpos") is { Length: > 0 } extra)
        {
            targets.AddRange(ExtraTargets(extra));
        }

        return new GsxStand(
            section,
            StandName(section),
            values.GetValueOrDefault("this_parking_pos") is { Length: > 0 } parking ? Pose(parking) : null,
            Pose(values["parkingsystem_stopposition"]),
            Number(values.GetValueOrDefault("maxwingspan")),
            values.GetValueOrDefault("hasjetway") == "1",
            targets);
    }

    private static string Label(string[] labels, int index, string fallback) =>
        index < labels.Length && !string.IsNullOrWhiteSpace(labels[index]) ? labels[index].Trim() : fallback;

    /// <summary>pushback{side}approachpos, pushback{side}approachpos2, ... then pushback{side}pos.</summary>
    private static GsxPushbackTarget? Side(IReadOnlyDictionary<string, string> values, string side, string label)
    {
        if (values.GetValueOrDefault($"pushback{side}pos") is not { Length: > 0 } final)
        {
            return null;
        }

        var waypoints = new List<GsxPose>();
        for (var i = 1; i < 10; i++)
        {
            var key = i == 1 ? $"pushback{side}approachpos" : $"pushback{side}approachpos{i}";
            if (values.GetValueOrDefault(key) is { Length: > 0 } point)
            {
                waypoints.Add(Pose(point));
            }
        }

        return new GsxPushbackTarget(label, waypoints, Pose(final));
    }

    /// <summary><c>pushbackaddpos</c>: a Python list of dicts with label, pos, approach, wws and startEngines.</summary>
    private static IEnumerable<GsxPushbackTarget> ExtraTargets(string literal)
    {
        if (PythonLiteral.Parse(literal) is not List<object?> list)
        {
            throw new FormatException("pushbackaddpos is not a list");
        }

        foreach (var item in list)
        {
            if (item is not Dictionary<string, object?> target || PoseOf(target.GetValueOrDefault("pos")) is not { } final)
            {
                continue; // "DO NOT USE" placeholders have no position
            }

            var waypoints = (target.GetValueOrDefault("approach") as List<object?> ?? new List<object?>())
                .Select(PoseOf)
                .OfType<GsxPose>()
                .ToList();
            var label = (target.GetValueOrDefault("label") as string)?.Trim();
            yield return new GsxPushbackTarget(
                string.IsNullOrEmpty(label) ? "Pushback" : label,
                waypoints,
                final,
                target.GetValueOrDefault("wws") is double wws ? (int)wws : null,
                target.GetValueOrDefault("startEngines") as double?);
        }
    }

    private static GsxPose? PoseOf(object? value) =>
        value is List<object?> { Count: >= 2 } t && t[0] is double lat && t[1] is double lon
            ? new GsxPose(lat, lon, t.Count > 2 ? t[2] as double? : null)
            : null;

    /// <summary>"lat lon heading [extra]" as written by GSX.</summary>
    private static GsxPose Pose(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || Number(parts[0]) is not { } lat || Number(parts[1]) is not { } lon)
        {
            throw new FormatException($"invalid position '{text}'");
        }

        return new GsxPose(lat, lon, parts.Length > 2 ? Number(parts[2]) : null);
    }

    private static double? Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static int? Int(string? text) => Number(text) is { } value ? (int)value : null;

    /// <summary>Sections with their keys (lower case); a repeated section or key overwrites the earlier one.</summary>
    private static List<(string Section, Dictionary<string, string> Values)> ReadIni(string text)
    {
        var sections = new List<(string, Dictionary<string, string>)>();
        var bySection = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                var name = line[1..^1].Trim().ToLowerInvariant();
                if (!bySection.TryGetValue(name, out current))
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    bySection[name] = current;
                    sections.Add((name, current));
                }

                continue;
            }

            var equals = line.IndexOf('=');
            if (current is not null && equals > 0)
            {
                current[line[..equals].Trim()] = line[(equals + 1)..].Trim();
            }
        }

        return sections;
    }
}
