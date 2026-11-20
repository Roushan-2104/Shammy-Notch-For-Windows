using System.Net.Http;
using System.Text;
using Shammy.Core.Reports;

namespace Shammy.Infrastructure.Reports;

/// <summary>
/// Sends a report the user chose to send to Shammy's relay (the Cloudflare Worker in relay/), which files it on GitHub.
/// Nothing is sent any other way, and nothing at all without the user's Send.
/// </summary>
internal static class ReportRelay
{
    /// <summary>The relay's address (relay/README.md, step 5); empty until it's set up, and then nothing can be sent.</summary>
    const string Relay = "";

    /// <summary>Test pipe only: SHAMMY_RELAY points a test copy at a local stand-in, so a test never files a real issue.</summary>
    public static string Address { get; } =
        CommandServer.IsEnabled && Environment.GetEnvironmentVariable("SHAMMY_RELAY") is { Length: > 0 } test ? test : Relay;

    public static bool IsSetUp => Address.Length > 0;

    static readonly Lazy<HttpClient> Http = new(() =>
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 64 * 1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Shammy/{ProblemWatch.Version} (problem reports)");
        return http;
    });

    /// <summary>True once the relay has it.</summary>
    public static async Task<bool> SendAsync(Report report, string? note)
    {
        if (!IsSetUp) return false;
        try
        {
            using var body = new StringContent(report.ToJson(note, ProblemWatch.PrivateWords), Encoding.UTF8, "application/json");
            using var response = await Http.Value.PostAsync(Address.TrimEnd('/') + "/report", body).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return true;
            Log.Info($"The report relay answered {(int)response.StatusCode}");
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Not Log.Warn with the exception: a failed report must not count as a problem of its own.
            Log.Info($"Sending a report failed: {ex.GetType().Name}");
            return false;
        }
    }
}
