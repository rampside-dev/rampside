using System.Net;
using System.Security.Cryptography;
using System.Text;
using Rampside.Hosting.Updates;

namespace Rampside.Core.Tests;

public sealed class UpdateTests : IDisposable
{
    // What the Website workflow writes for release 0.1.0 (.github/scripts/stamp-download-links.py).
    private const string Manifest = """
        {
          "version": "0.1.0",
          "publishedAt": "2026-10-30T19:37:15Z",
          "notesUrl": "https://github.com/rampside-dev/rampside/releases/tag/v0.1.0",
          "msi": {
            "url": "https://github.com/rampside-dev/rampside/releases/download/v0.1.0/Rampside-0.1.0-x64.msi",
            "size": 60683752,
            "sha256": "4ca43b01a52433769117bdb771a3f69ca8619b4d94574c0c089ed3be6885cd69"
          }
        }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rampside-update-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void ReadsTheWebsiteManifest()
    {
        var release = ReleaseInfo.Parse(Manifest);

        Assert.Equal(new Version(0, 1, 0), release.Version);
        Assert.Equal("0.1.0", release.VersionText);
        Assert.Equal(60683752, release.MsiSize);
        Assert.Equal("4ca43b01a52433769117bdb771a3f69ca8619b4d94574c0c089ed3be6885cd69", release.MsiSha256);
        Assert.Equal("https://github.com/rampside-dev/rampside/releases/tag/v0.1.0", release.NotesUrl.AbsoluteUri);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 19, 37, 15, TimeSpan.Zero), release.PublishedAt);
    }

    [Theory]
    [InlineData("0.1.0", "0.2.0", true)]
    [InlineData("0.2.0", "0.2.0", false)]
    [InlineData("0.10.0", "0.9.9", false)]
    [InlineData("0.9.9", "0.10.0", true)]
    [InlineData("1.0.0", "0.99.0", false)]
    public void ComparesVersionsNumerically(string current, string latest, bool newer)
    {
        var release = ReleaseInfo.Parse(Manifest.Replace("\"0.1.0\"", $"\"{latest}\""));

        Assert.Equal(newer, release.IsNewerThan(Version.Parse(current)));
    }

    [Fact]
    public void FourPartAssemblyVersionsCompareLikeThreeParts()
    {
        var release = ReleaseInfo.Parse(Manifest);

        Assert.False(release.IsNewerThan(new Version(0, 1, 0, 0)));
    }

    [Theory]
    [InlineData("https://github.com/rampside-dev/rampside/releases/download/", "https://example.com/download/")]
    [InlineData("https://github.com/rampside-dev/rampside/releases/download/", "http://github.com/rampside-dev/rampside/releases/download/")]
    [InlineData("https://github.com/rampside-dev/rampside/releases/download/", "https://github.com/someone-else/rampside/releases/download/")]
    [InlineData("https://github.com/rampside-dev/rampside/releases/tag/", "https://evil.example/")]
    [InlineData("4ca43b01a52433769117bdb771a3f69ca8619b4d94574c0c089ed3be6885cd69", "not-a-hash")]
    [InlineData("\"0.1.0\"", "\"0.1.0-beta\"")]
    [InlineData("\"0.1.0\"", "\"1.0\"")]
    [InlineData("60683752", "0")]
    [InlineData("60683752", "\"big\"")]
    [InlineData("\"msi\"", "\"installer\"")]
    public void RejectsManifestsThatAreBrokenOrPointElsewhere(string find, string replace)
    {
        Assert.Throws<UpdateException>(() => ReleaseInfo.Parse(Manifest.Replace(find, replace)));
    }

    [Fact]
    public void RejectsNonJson()
    {
        Assert.Throws<UpdateException>(() => ReleaseInfo.Parse("<html>404</html>"));
    }

    [Fact]
    public async Task ReportsAMissingManifestAsUpdateException()
    {
        using var client = new UpdateClient(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => client.GetLatestAsync());
        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task FetchesTheManifest()
    {
        Uri? asked = null;
        using var client = new UpdateClient(new HttpClient(new FakeHandler(request =>
        {
            asked = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Manifest) };
        })));

        var release = await client.GetLatestAsync();

        Assert.Equal(UpdateClient.DefaultManifestUrl, asked);
        Assert.Equal("0.1.0", release.VersionText);
    }

    [Fact]
    public async Task DownloadsAndChecksTheInstaller()
    {
        var content = Encoding.UTF8.GetBytes(new string('x', 200_000));
        var release = Release(content, Convert.ToHexStringLower(SHA256.HashData(content)));
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "Rampside-0.0.9-x64.msi"), "old download");
        var reported = new List<double>();
        using var client = new UpdateClient(new HttpClient(new FakeHandler(_ => Ok(content))));

        var path = await client.DownloadAsync(release, _folder, new SyncProgress(reported.Add));

        Assert.Equal(Path.Combine(_folder, "Rampside-0.2.0-x64.msi"), path);
        Assert.Equal(content, File.ReadAllBytes(path));
        Assert.Equal([path], Directory.GetFiles(_folder));
        Assert.Equal(1.0, reported[^1]);
    }

    [Fact]
    public async Task DropsAnInstallerWithTheWrongChecksum()
    {
        var content = Encoding.UTF8.GetBytes("tampered installer");
        var release = Release(content, new string('0', 64));
        using var client = new UpdateClient(new HttpClient(new FakeHandler(_ => Ok(content))));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => client.DownloadAsync(release, _folder));

        Assert.Contains("SHA-256", ex.Message);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task DropsAnIncompleteOrOversizedDownload()
    {
        var content = Encoding.UTF8.GetBytes("short");
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        using var client = new UpdateClient(new HttpClient(new FakeHandler(_ => Ok(content))));

        await Assert.ThrowsAsync<UpdateException>(() => client.DownloadAsync(Release(content, hash) with { MsiSize = 100 }, _folder));
        await Assert.ThrowsAsync<UpdateException>(() => client.DownloadAsync(Release(content, hash) with { MsiSize = 2 }, _folder));
        Assert.Empty(Directory.GetFiles(_folder));
    }

    private static ReleaseInfo Release(byte[] content, string sha256) => new(
        new Version(0, 2, 0),
        new Uri("https://github.com/rampside-dev/rampside/releases/download/v0.2.0/Rampside-0.2.0-x64.msi"),
        content.Length,
        sha256,
        new Uri("https://github.com/rampside-dev/rampside/releases/tag/v0.2.0"),
        null);

    private static HttpResponseMessage Ok(byte[] content) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    // Progress<T> posts to the thread pool; this one reports at once, so the test sees every value.
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
