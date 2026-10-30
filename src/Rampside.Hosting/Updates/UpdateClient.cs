using System.Net;
using System.Security.Cryptography;

namespace Rampside.Hosting.Updates;

/// <summary>
/// Asks the website for the latest release and downloads its installer, checking size and SHA-256.
/// Installing is up to the caller (the desktop window runs the MSI after asking the user).
/// </summary>
public sealed class UpdateClient : IDisposable
{
    // The website on GitHub Pages. With a custom domain set up later, GitHub redirects this address there.
    public static Uri DefaultManifestUrl { get; } = new("https://rampside-dev.github.io/rampside/latest.json");

    // A download that receives nothing for this long is given up, so the button never hangs.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly Uri _manifestUrl;

    public UpdateClient(HttpClient? http = null, Uri? manifestUrl = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // RAMPSIDE_UPDATE_URL points the check at another latest.json, e.g. to try a release before the website has it.
        // The installer itself must still come from Rampside's GitHub releases (see ReleaseInfo.Parse).
        _manifestUrl = manifestUrl
            ?? (Uri.TryCreate(Environment.GetEnvironmentVariable("RAMPSIDE_UPDATE_URL"), UriKind.Absolute, out var custom)
                && (custom.Scheme == Uri.UriSchemeHttps || custom.Scheme == Uri.UriSchemeHttp) ? custom : null)
            ?? DefaultManifestUrl;
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Rampside/{ReleaseInfo.Format(ReleaseInfo.CurrentVersion)}");
        }
    }

    /// <summary>The latest published release, whether or not it is newer than this build.</summary>
    public async Task<ReleaseInfo> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        string body;
        try
        {
            using var response = await _http.GetAsync(_manifestUrl, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new UpdateException($"{_manifestUrl.Host} answered with HTTP {(int)response.StatusCode}.");
            }

            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException($"Cannot reach {_manifestUrl.Host}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException($"{_manifestUrl.Host} did not answer in time.", ex);
        }

        return ReleaseInfo.Parse(body);
    }

    /// <summary>
    /// Downloads the installer into <paramref name="folder"/> (older downloads there are removed) and returns its path.
    /// The file only appears under its final name once size and checksum match.
    /// </summary>
    public async Task<string> DownloadAsync(ReleaseInfo release, string folder, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.EnumerateFiles(folder))
        {
            TryDelete(old);
        }

        var name = Path.GetFileName(release.MsiUrl.AbsolutePath);
        if (!name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            name = $"Rampside-{release.VersionText}-x64.msi";
        }

        var target = Path.Combine(folder, name);
        var partial = target + ".part";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            using (var response = await _http.GetAsync(release.MsiUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new UpdateException($"The download answered with HTTP {(int)response.StatusCode}.");
                }

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buffer = new byte[81920];
                while (true)
                {
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    stall.CancelAfter(StallTimeout);
                    int read;
                    try
                    {
                        read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new UpdateException("The download stopped.", ex);
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    received += read;
                    if (received > release.MsiSize)
                    {
                        throw new UpdateException("The download is larger than announced.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report((double)received / release.MsiSize);
                }
            }

            if (received != release.MsiSize)
            {
                throw new UpdateException($"The download is incomplete ({received:N0} of {release.MsiSize:N0} bytes).");
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (actual != release.MsiSha256)
            {
                throw new UpdateException("The downloaded installer does not match its SHA-256 checksum, so it was not used.");
            }

            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException($"Download failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException("The download did not start in time.", ex);
        }
        catch (IOException ex)
        {
            throw new UpdateException($"Could not save the installer in {folder}: {ex.Message}", ex);
        }
        finally
        {
            TryDelete(partial);
        }
    }

    public void Dispose() => _http.Dispose();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An old download still open somewhere; it is replaced next time.
        }
    }
}
