using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shammy.Infrastructure;

namespace Shammy.Search;

/// <summary>A short answer lifted from a results page (AI Mode's first lines), and the page it came from.</summary>
internal sealed record ShortAnswer(string Text, string PageUrl);

/// <summary>
/// The small online helpers behind the island's answers: the town for an Indian PIN code or the bank branch for an IFSC
/// code from free public lookups, and ChatGPT's address with a question filled in. Each lookup sends only the code it is
/// given, gives up after a few seconds, and returns null rather than throwing.
/// </summary>
internal static class ShortAnswers
{
    static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(7),
    };

    static ShortAnswers() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Shammy");

    /// <summary>The longest text sent for translation (a few paragraphs). Translations come through
    /// <see cref="HiddenPage.TranslateAsync"/>, with the browser's own Google cookies.</summary>
    public const int MaxTranslate = 1500;

    /// <summary>"Gothapatna, Khordha, Odisha" for a PIN code (India Post's public lookup), or null.</summary>
    public static async Task<string?> PinPlaceAsync(string pin, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://api.postalpincode.in/pincode/" + Uri.EscapeDataString(pin), ct));
            var first = doc.RootElement[0];
            if (first.GetProperty("Status").GetString() != "Success") return null;
            var offices = first.GetProperty("PostOffice");
            if (offices.GetArrayLength() == 0) return null;
            var o = offices[0];
            // The service pads some names ("Bangalore "): trimmed, so the same name isn't shown twice.
            string Get(string name) => o.TryGetProperty(name, out var v) ? (v.GetString() ?? "").Trim() : "";
            var parts = new[] { Get("Name"), Get("District"), Get("State") }.Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
            return string.Join(", ", parts);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Log.Warn("PIN code lookup failed", ex);
            return null;
        }
    }

    /// <summary>("HDFC Bank", "Bandra West, Mumbai") for an IFSC code (Razorpay's public IFSC lookup), or null.</summary>
    public static async Task<(string Bank, string Branch)?> IfscAsync(string code, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://ifsc.razorpay.com/" + Uri.EscapeDataString(code), ct));
            var r = doc.RootElement;
            string Get(string name) => r.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
            var bank = Get("BANK");
            if (bank.Length == 0) return null;
            var branch = string.Join(", ", new[] { Get("BRANCH"), Get("CITY") }.Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
            return (bank, branch);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Log.Warn("IFSC lookup failed", ex);
            return null;
        }
    }

    /// <summary>chatgpt.com with <paramref name="question"/> already filled in, opened in the user's browser.</summary>
    public static Uri ChatGptUrl(string question) =>
        new("https://chatgpt.com/?q=" + Uri.EscapeDataString(question.Length > 1800 ? question[..1800] : question));
}
