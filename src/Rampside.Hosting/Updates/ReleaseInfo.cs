using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rampside.Hosting.Updates;

/// <summary>
/// The latest release as described by latest.json on the website (<see cref="UpdateClient.DefaultManifestUrl"/>). The Website workflow writes that file
/// from GitHub's latest release (.github/scripts/stamp-download-links.py):
/// <code>
/// { "version": "0.2.0", "publishedAt": "...", "notesUrl": "https://github.com/rampside-dev/rampside/releases/tag/v0.2.0",
///   "msi": { "url": "https://github.com/rampside-dev/rampside/releases/download/v0.2.0/Rampside-0.2.0-x64.msi",
///            "size": 60683752, "sha256": "4ca4..." } }
/// </code>
/// </summary>
public sealed record ReleaseInfo(Version Version, Uri MsiUrl, long MsiSize, string MsiSha256, Uri NotesUrl, DateTimeOffset? PublishedAt)
{
    /// <summary>Installers are only taken from this repository's GitHub releases, wherever the manifest comes from.</summary>
    public const string ReleasePrefix = "https://github.com/rampside-dev/rampside/releases/";

    private const long MaxMsiSize = 1L << 30;
    private static readonly Regex VersionPattern = new(@"^\d{1,5}\.\d{1,5}\.\d{1,5}$");
    private static readonly Regex Sha256Pattern = new("^[0-9a-fA-F]{64}$");

    public string VersionText => Format(Version);

    /// <summary>The version of this build (from Directory.Build.props or the release tag), as major.minor.patch.</summary>
    public static Version CurrentVersion { get; } = Normalize(typeof(ReleaseInfo).Assembly.GetName().Version ?? new Version(0, 0, 0));

    public static string Format(Version version) => version.ToString(3);

    public bool IsNewerThan(Version current) => Version > Normalize(current);

    /// <summary>Reads latest.json. Anything missing, malformed or pointing outside the GitHub releases is an <see cref="UpdateException"/>.</summary>
    public static ReleaseInfo Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var version = Text(root, "version");
            if (!VersionPattern.IsMatch(version))
            {
                throw new UpdateException($"The update information has an unexpected version '{version}'.");
            }

            var msi = root.GetProperty("msi");
            var url = ReleaseUrl(Text(msi, "url"), "installer");
            var size = msi.GetProperty("size").GetInt64();
            if (size <= 0 || size > MaxMsiSize)
            {
                throw new UpdateException($"The update information has an unexpected installer size ({size} bytes).");
            }

            var sha256 = Text(msi, "sha256");
            if (!Sha256Pattern.IsMatch(sha256))
            {
                throw new UpdateException("The update information has no valid SHA-256 checksum for the installer.");
            }

            var notes = root.TryGetProperty("notesUrl", out var notesElement) && notesElement.ValueKind == JsonValueKind.String
                ? ReleaseUrl(notesElement.GetString()!, "release notes")
                : new Uri(ReleasePrefix + "latest");
            DateTimeOffset? published = root.TryGetProperty("publishedAt", out var publishedElement)
                && publishedElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(publishedElement.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
                ? at
                : null;

            return new ReleaseInfo(Version.Parse(version), url, size, sha256.ToLowerInvariant(), notes, published);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new UpdateException($"The update information could not be read ({ex.Message}).", ex);
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.GetProperty(name).GetString() ?? throw new UpdateException($"The update information has no {name}.");

    private static Uri ReleaseUrl(string text, string what)
    {
        if (!text.StartsWith(ReleasePrefix, StringComparison.Ordinal) || !Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            throw new UpdateException($"The update information points the {what} outside Rampside's GitHub releases.");
        }

        return uri;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}

/// <summary>An update check or download that failed; the message is shown to the user.</summary>
public sealed class UpdateException : Exception
{
    public UpdateException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
