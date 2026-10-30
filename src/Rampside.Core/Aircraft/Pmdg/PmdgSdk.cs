namespace Rampside.Core.Aircraft.Pmdg;

/// <summary>
/// Live connection to a PMDG aircraft through its SDK: data broadcast, CDU screens and control events.
/// Fields and events are addressed by their names in the PMDG header.
/// </summary>
public interface IPmdgLink
{
    /// <summary>True once data from the aircraft has arrived.</summary>
    bool HasData { get; }

    /// <summary>Why the link does not work, if known (header missing, broadcast disabled, ...).</summary>
    string? Problem { get; }

    /// <summary>Human-readable details for troubleshooting (header path, data age, ...).</summary>
    string Diagnostics { get; }

    bool? GetBool(string field, int index = 0);
    double? GetNumber(string field, int index = 0);

    /// <summary>Latest CDU screen, or null if the CDU broadcast is off.</summary>
    PmdgCduScreen? GetCdu(int index);

    /// <summary>Sends a PMDG control event by its header name (e.g. EVT_DOOR_FWD_L). False if the name is unknown.</summary>
    bool SendEvent(string eventName, uint parameter = 0);
}

/// <summary>Where a PMDG aircraft keeps its SDK and which names it uses.</summary>
public sealed record PmdgAircraftSdk(
    string DisplayName,
    IReadOnlyList<string> PackageFolders,
    string HeaderFile,
    string OptionsFile,
    string DataStruct,
    string DataNameDefine,
    string DataIdDefine,
    string DataDefinitionDefine,
    string CduStruct,
    IReadOnlyList<(string Name, string Id, string Definition)> CduDefines)
{
    public static PmdgAircraftSdk Pmdg737 { get; } = new(
        "PMDG 737",
        new[] { "pmdg-aircraft-738", "pmdg-aircraft-737", "pmdg-aircraft-736", "pmdg-aircraft-739" },
        "PMDG_NG3_SDK.h",
        "737_Options.ini",
        "PMDG_NG3_Data",
        "PMDG_NG3_DATA_NAME",
        "PMDG_NG3_DATA_ID",
        "PMDG_NG3_DATA_DEFINITION",
        "PMDG_NG3_CDU_Screen",
        new[]
        {
            ("PMDG_NG3_CDU_0_NAME", "PMDG_NG3_CDU_0_ID", "PMDG_NG3_CDU_0_DEFINITION"),
            ("PMDG_NG3_CDU_1_NAME", "PMDG_NG3_CDU_1_ID", "PMDG_NG3_CDU_1_DEFINITION"),
        });
}

/// <summary>Finds a PMDG SDK header in the user's MSFS 2024 package folders.</summary>
public static class PmdgSdkLocator
{
    private static readonly string[] PackageSubfolders = { "Community", @"Official\OneStore", @"Official\Steam" };

    public static string? FindHeader(PmdgAircraftSdk sdk, string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return File.Exists(overridePath) ? overridePath : null;
        }

        foreach (var root in PackageRoots())
        {
            foreach (var subfolder in PackageSubfolders)
            {
                foreach (var package in sdk.PackageFolders)
                {
                    var candidate = Path.Combine(root, subfolder, package, "Documentation", "SDK", sdk.HeaderFile);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Package roots of the Microsoft Store and Steam editions, including a custom InstalledPackagesPath.</summary>
    public static IEnumerable<string> PackageRoots()
    {
        var bases = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft Flight Simulator 2024"),
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var baseDir in bases)
        {
            var custom = ReadInstalledPackagesPath(Path.Combine(baseDir, "UserCfg.opt"));
            foreach (var root in new[] { custom, Path.Combine(baseDir, "Packages") })
            {
                if (root is not null && Directory.Exists(root) && seen.Add(root))
                {
                    yield return root;
                }
            }
        }
    }

    private static string? ReadInstalledPackagesPath(string userCfg)
    {
        if (!File.Exists(userCfg))
        {
            return null;
        }

        foreach (var line in File.ReadLines(userCfg))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("InstalledPackagesPath", StringComparison.OrdinalIgnoreCase))
            {
                var start = trimmed.IndexOf('"');
                var end = trimmed.LastIndexOf('"');
                return end > start ? trimmed.Substring(start + 1, end - start - 1) : null;
            }
        }

        return null;
    }
}

/// <summary>Stand-in when the PMDG SDK cannot be used, so the adapter can explain why.</summary>
public sealed class UnavailablePmdgLink : IPmdgLink
{
    public UnavailablePmdgLink(string problem) => Problem = problem;

    public bool HasData => false;
    public string? Problem { get; }
    public string Diagnostics => Problem ?? string.Empty;
    public bool? GetBool(string field, int index = 0) => null;
    public double? GetNumber(string field, int index = 0) => null;
    public PmdgCduScreen? GetCdu(int index) => null;
    public bool SendEvent(string eventName, uint parameter = 0) => false;
}
