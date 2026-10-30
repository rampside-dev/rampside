using System.Net;
using Rampside.Core.FlightPlans;

namespace Rampside.Hosting;

/// <summary>
/// Fetches the user's latest SimBrief OFP (public xml.fetcher.php, no login needed).
/// Only called on request, never polled.
/// </summary>
public sealed class SimBriefClient : IDisposable
{
    private const string FetchUrl = "https://www.simbrief.com/api/xml.fetcher.php";

    private readonly HttpClient _http;

    public SimBriefClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Rampside/0.1");
        }
    }

    /// <summary>A number is the SimBrief pilot ID, anything else the username.</summary>
    public static Uri BuildUrl(string user)
    {
        var trimmed = user.Trim();
        var parameter = trimmed.All(char.IsAsciiDigit) ? "userid" : "username";
        return new Uri($"{FetchUrl}?{parameter}={Uri.EscapeDataString(trimmed)}");
    }

    public async Task<FlightPlan> FetchLatestAsync(string user, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(user))
        {
            throw new SimBriefException("No SimBrief user set. Enter your SimBrief pilot ID or username on the Settings tab (console: --simbrief).");
        }

        string body;
        HttpStatusCode status;
        try
        {
            using var response = await _http.GetAsync(BuildUrl(user), cancellationToken).ConfigureAwait(false);
            status = response.StatusCode;
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SimBriefException($"Cannot reach SimBrief: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SimBriefException("SimBrief did not answer in time.", ex);
        }

        // Errors (unknown user, no plan) come as HTTP 400 with a small XML message, which the parser reports.
        if (status != HttpStatusCode.OK && !body.TrimStart().StartsWith('<'))
        {
            throw new SimBriefException($"SimBrief answered with HTTP {(int)status}.");
        }

        return SimBriefOfpParser.Parse(body);
    }

    public void Dispose() => _http.Dispose();
}
