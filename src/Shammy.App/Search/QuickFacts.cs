using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shammy.Infrastructure;

namespace Shammy.Search;

/// <summary>
/// A short fact for a circled name or term ("Ada Lovelace", "photosynthesis"): Wikipedia's one-paragraph summary, so
/// the answer can sit in the island instead of a full results card. Only the circled words are sent (no picture), it
/// gives up after a moment, and anything that isn't a plain article (a disambiguation page, nothing found) means the
/// usual full results.
/// </summary>
internal static class QuickFacts
{
    internal sealed record Fact(string Title, string? Description, string Summary, Uri Page);

    static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(1400);

    static readonly Lazy<HttpClient> Http = new(() =>
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4), MaxResponseContentBufferSize = 512 * 1024 };
        // Wikipedia asks API clients to name themselves; nothing about the user is in it.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Shammy/1.0 (Windows desktop app; circle to search)");
        return http;
    });

    /// <summary>Worth asking: one to four words that read like a name or a term (letters, maybe a hyphen), nothing more.</summary>
    internal static bool Worth(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().Trim('.', ',', ':', ';', '"', '\'', '(', ')');
        if (t.Length is < 3 or > 48) return false;
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 4) return false;
        // Latin letters only (the English Wikipedia), and not a stray code or a number.
        return Regex.IsMatch(t, @"^[\p{IsBasicLatin}\p{IsLatin-1Supplement}\p{IsLatinExtended-A}]+$") && Regex.IsMatch(t, "[A-Za-z]{3,}")
            && !Regex.IsMatch(t, @"\d") && !t.Contains('@') && !t.Contains('/');
    }

    /// <summary>The fact, or null (nothing worth showing, too slow, offline). Never throws.</summary>
    internal static async Task<Fact?> TryAsync(string text, CancellationToken cancel)
    {
        if (!Worth(text)) return null;
        var title = Regex.Replace(text.Trim().Trim('.', ',', ':', ';', '"', '\''), @"\s+", "_");
        var url = "https://en.wikipedia.org/api/rest_v1/page/summary/" + Uri.EscapeDataString(title) + "?redirect=true";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(Wait);
            using var response = await Http.Value.GetAsync(url, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            return Parse(doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException)
        {
            if (!cancel.IsCancellationRequested) Log.Info($"No quick fact ({ex.GetType().Name})");
            return null;
        }
    }

    internal static Fact? Parse(JsonElement root)
    {
        if (root.TryGetProperty("type", out var type) && type.GetString() != "standard") return null;
        var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
        var extract = root.TryGetProperty("extract", out var e) ? e.GetString() : null;
        var description = root.TryGetProperty("description", out var d) ? d.GetString() : null;
        string? page = null;
        if (root.TryGetProperty("content_urls", out var urls) && urls.TryGetProperty("desktop", out var desk) && desk.TryGetProperty("page", out var p))
            page = p.GetString();
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(extract) || !Uri.TryCreate(page, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
            return null;
        return new Fact(title, string.IsNullOrWhiteSpace(description) ? null : description, FirstSentences(extract, 190), uri);
    }

    /// <summary>Whole sentences up to about <paramref name="max"/> characters (at least the first, cut if it's very long).</summary>
    internal static string FirstSentences(string text, int max)
    {
        text = Regex.Replace(text, @"\s+", " ").Trim();
        var sentences = Regex.Split(text, @"(?<=[.!?])\s+(?=[A-Z])");
        var said = sentences[0];
        foreach (var s in sentences.Skip(1))
        {
            if (said.Length + 1 + s.Length > max) break;
            said += " " + s;
        }
        return said.Length <= max + 40 ? said : said[..max].TrimEnd() + "…";
    }
}
