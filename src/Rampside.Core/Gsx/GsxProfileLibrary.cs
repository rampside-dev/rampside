using Rampside.Core.Simulation;

namespace Rampside.Core.Gsx;

/// <summary>A GSX stand found for the stand the aircraft is parked on.</summary>
/// <param name="ProfileName">File name of the profile without extension, e.g. "eddm-simwings".</param>
/// <param name="DistanceMeters">Distance between the MSFS parking spot and the GSX stand; null when matched by name only.</param>
/// <param name="StopOffset">The profile script's stop offset for the aircraft; null without script, function or known aircraft.</param>
public sealed record GsxStandMatch(string ProfileName, GsxStand Stand, double? DistanceMeters, GsxStopOffset? StopOffset = null)
{
    /// <summary>
    /// Where this aircraft stops: the profile's stop position, moved back along the stand's heading by
    /// <see cref="StopOffset"/>. That positive offsets mean "stop short" is read off the scripts (the bigger the aircraft,
    /// the bigger the value, roughly its extra length ahead of the reference point); it is not yet checked in the sim.
    /// </summary>
    public GsxPose StopPosition => StopOffset is { Meters: not 0 } offset && Stand.StopPosition.HeadingDeg is { } heading
        ? Moved(Stand.StopPosition, heading, -offset.Meters)
        : Stand.StopPosition;

    private static GsxPose Moved(GsxPose pose, double heading, double forwardM)
    {
        var (lat, lon) = Geo.Offset(pose.Latitude, pose.Longitude, heading, forwardM, 0);
        return pose with { Latitude = lat, Longitude = lon };
    }
}

/// <summary>Looks up the GSX stand for an MSFS parking spot.</summary>
public interface IGsxStandSource
{
    /// <summary>
    /// The GSX stand for <paramref name="spot"/> (or, without position, for <paramref name="standName"/>), or null.
    /// With <paramref name="aircraft"/>, the match carries the profile script's stop offset for it.
    /// </summary>
    GsxStandMatch? Find(string airportIcao, ParkingSpot? spot, string standName, GsxAircraft? aircraft = null);
}

/// <summary>
/// The user's installed GSX Pro airport profiles. Profiles are read from the user's own GSX folder at run time
/// (they belong to their authors and are never shipped with Rampside), parsed once and re-read when the file changes.
/// A profile's script (same name, .py) is read for the per-aircraft stop positions, see <see cref="GsxStopOffsets"/>;
/// the separate *_handler.py scripts (operators, vehicle choice) are not.
/// </summary>
public sealed class GsxProfileLibrary : IGsxStandSource
{
    /// <summary>How far the GSX stand's parking position may be from the MSFS parking spot.</summary>
    public const double MatchToleranceMeters = 5;

    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTime Written, GsxProfile Profile)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime Written, GsxStopOffsets Offsets)> _scripts = new(StringComparer.OrdinalIgnoreCase);

    public GsxProfileLibrary(string folder)
    {
        Folder = folder;
    }

    /// <summary>%APPDATA%\Virtuali\GSX\MSFS, where GSX Pro keeps installed airport profiles.</summary>
    public static string DefaultFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Virtuali", "GSX", "MSFS");

    public string Folder { get; }

    /// <summary>Notes for the log, e.g. a profile that could not be read.</summary>
    public event EventHandler<string>? Message;

    /// <summary>Profile files for an airport: "&lt;icao&gt;-&lt;name&gt;.ini" or "&lt;icao&gt;.ini" (case-insensitive).</summary>
    public IReadOnlyList<string> ProfileFiles(string airportIcao)
    {
        if (string.IsNullOrWhiteSpace(airportIcao) || !Directory.Exists(Folder))
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory.EnumerateFiles(Folder, "*.ini")
                .Where(path => IsProfileFor(Path.GetFileNameWithoutExtension(path), airportIcao))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message?.Invoke(this, $"GSX profiles in {Folder} could not be listed: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    public GsxStandMatch? Find(string airportIcao, ParkingSpot? spot, string standName, GsxAircraft? aircraft = null)
    {
        GsxStandMatch? best = null;
        string? bestPath = null;
        foreach (var path in ProfileFiles(airportIcao))
        {
            if (Load(path) is not { } profile || Match(profile, spot, standName) is not { } match)
            {
                continue;
            }

            // Several profiles for one airport (different sceneries): the one whose stand lies closest wins.
            if (best is null || (match.DistanceMeters ?? double.MaxValue) < (best.DistanceMeters ?? double.MaxValue))
            {
                best = match;
                bestPath = path;
            }
        }

        if (best is null || aircraft is null || LoadScript(Path.ChangeExtension(bestPath!, ".py")) is not { } script)
        {
            return best;
        }

        var problems = new List<string>();
        var offset = script.For(best.Stand.Section, aircraft, problems);
        foreach (var problem in problems)
        {
            Message?.Invoke(this, $"GSX script {script.Name}: {problem}");
        }

        return best with { StopOffset = offset };
    }

    /// <summary>
    /// The stand for the MSFS spot: first one with the same name whose parking position lies within the spot's radius,
    /// then the one whose parking position lies on the spot (within <see cref="MatchToleranceMeters"/>).
    /// Profile positions are often a few metres off the scenery's spots (at Simwings EDDM 77 of 180 stands are 5–17 m off,
    /// while neighbouring stands are 24 m or more apart); the nearest stand alone can be the wrong one there
    /// (EDDM Gate A113: "parking 801" lies closer than "gate a 113"), so the name decides first.
    /// Stands without a parking position in the profile are matched by name, but only when their stop position lies
    /// on the spot too (names like "gate a 317" are ambiguous).
    /// </summary>
    public static GsxStandMatch? Match(GsxProfile profile, ParkingSpot? spot, string standName)
    {
        if (spot is not null)
        {
            var positioned = profile.Stands
                .Where(s => s.ParkingPosition is not null)
                .Select(s => (Stand: s, Distance: StandLocator.DistanceMeters(spot.Latitude, spot.Longitude, s.ParkingPosition!.Value.Latitude, s.ParkingPosition.Value.Longitude)))
                .OrderBy(x => x.Distance)
                .ToList();
            var named = positioned.FirstOrDefault(x =>
                string.Equals(x.Stand.Name, standName, StringComparison.OrdinalIgnoreCase)
                && x.Distance <= spot.RadiusMeters + StandLocator.ToleranceMeters);
            var nearest = named.Stand is not null ? named : positioned.FirstOrDefault(x => x.Distance <= MatchToleranceMeters);
            if (nearest.Stand is not null)
            {
                return new GsxStandMatch(profile.Name, nearest.Stand, nearest.Distance);
            }
        }

        var byName = profile.Stands.FirstOrDefault(s =>
            s.ParkingPosition is null
            && string.Equals(s.Name, standName, StringComparison.OrdinalIgnoreCase)
            && (spot is null || StandLocator.DistanceMeters(spot.Latitude, spot.Longitude, s.StopPosition.Latitude, s.StopPosition.Longitude)
                <= spot.RadiusMeters + StandLocator.ToleranceMeters));
        return byName is null ? null : new GsxStandMatch(profile.Name, byName, null);
    }

    private static bool IsProfileFor(string fileName, string airportIcao) =>
        fileName.StartsWith(airportIcao, StringComparison.OrdinalIgnoreCase)
        && (fileName.Length == airportIcao.Length || !char.IsLetterOrDigit(fileName[airportIcao.Length]));

    /// <summary>The profile's script, or null when there is none.</summary>
    private GsxStopOffsets? LoadScript(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var written = File.GetLastWriteTimeUtc(path);
            lock (_gate)
            {
                if (_scripts.TryGetValue(path, out var cached) && cached.Written == written)
                {
                    return cached.Offsets;
                }
            }

            var problems = new List<string>();
            var script = GsxStopOffsets.Parse(Path.GetFileNameWithoutExtension(path), File.ReadAllText(path), problems);
            foreach (var problem in problems.Take(5))
            {
                Message?.Invoke(this, $"GSX script {script.Name}: skipped {problem}");
            }

            lock (_gate)
            {
                _scripts[path] = (written, script);
            }

            return script;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message?.Invoke(this, $"GSX script {path} could not be read: {ex.Message}");
            return null;
        }
    }

    private GsxProfile? Load(string path)
    {
        try
        {
            var written = File.GetLastWriteTimeUtc(path); // follows the symlinks some sceneries install
            lock (_gate)
            {
                if (_cache.TryGetValue(path, out var cached) && cached.Written == written)
                {
                    return cached.Profile;
                }
            }

            var problems = new List<string>();
            var profile = GsxProfile.Parse(Path.GetFileNameWithoutExtension(path), File.ReadAllText(path), problems);
            foreach (var problem in problems.Take(5))
            {
                Message?.Invoke(this, $"GSX profile {profile.Name}: skipped {problem}");
            }

            lock (_gate)
            {
                _cache[path] = (written, profile);
            }

            return profile;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message?.Invoke(this, $"GSX profile {path} could not be read: {ex.Message}");
            return null;
        }
    }
}
