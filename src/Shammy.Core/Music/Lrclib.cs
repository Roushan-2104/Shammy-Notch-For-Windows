using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shammy.Core.Music;

public enum LyricsKind { None, Synced, Plain, Instrumental }

/// <summary>Lyrics for one track: time-synced lines, plain text, or nothing.</summary>
public sealed record TrackLyrics(LyricsKind Kind, IReadOnlyList<LyricLine> Lines, string Plain)
{
    public static readonly TrackLyrics None = new(LyricsKind.None, Array.Empty<LyricLine>(), "");
}

public sealed record LrclibRecord(string Track, string Artist, double Duration, bool Instrumental, string? Plain, string? Synced);

/// <summary>Helpers for LRCLIB (lrclib.net), a free, keyless lyrics database with synced LRC.</summary>
public static class Lrclib
{
    const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    // Titles come from whatever app or web page is playing; a crafted one must not burn CPU.
    const int MaxTitle = 300;
    static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    // Video-style decorations that never appear in a lyrics database title.
    static readonly Regex Decoration = new(
        @"\s*[\(\[][^\)\]]*\b(?:official|lyrics?|audio|video|visuali[sz]er|remaster(?:ed)?|hd|4k|mv|m/v|live)\b[^\)\]]*[\)\]]", Opts, MatchTimeout);
    static readonly Regex Feat = new(@"\s*[\(\[]?\s*\b(?:feat\.?|ft\.?|featuring)\s.*$", Opts, MatchTimeout);

    /// <summary>The exact lookup (<see cref="GetUrl"/>) needs a title and an artist; without either LRCLIB answers 400.</summary>
    public static bool CanGet(string track, string artist) => !string.IsNullOrWhiteSpace(track) && !string.IsNullOrWhiteSpace(artist);

    public static string GetUrl(string track, string artist, TimeSpan duration)
    {
        var url = "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(track) + "&artist_name=" + Uri.EscapeDataString(artist);
        if (duration > TimeSpan.Zero) url += "&duration=" + (int)Math.Round(duration.TotalSeconds);
        return url;
    }

    public static string SearchUrl(string query) => "https://lrclib.net/api/search?q=" + Uri.EscapeDataString(query);

    /// <summary>
    /// Media sessions from browsers report YouTube video titles ("Artist - Song (Official Video)") and
    /// channel names ("Artist - Topic", "ArtistVEVO"); reduce them to what a lyrics database stores.
    /// </summary>
    public static (string Track, string Artist) Clean(string title, string artist)
    {
        if (title.Length > MaxTitle) title = title[..MaxTitle];
        if (artist.Length > MaxTitle) artist = artist[..MaxTitle];
        string t;
        try
        {
            t = Decoration.Replace(title, "");
            t = Feat.Replace(t, "").Trim();
        }
        catch (RegexMatchTimeoutException)
        {
            t = title.Trim();
        }
        var a = artist.Trim();
        if (a.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase)) a = a[..^8].Trim();
        if (a.EndsWith("VEVO", StringComparison.Ordinal) && a.Length > 4) a = a[..^4].Trim();

        // "Artist - Song" from a video title, when the uploader is the artist or unknown.
        int dash = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            var left = t[..dash].Trim();
            var right = t[(dash + 3)..].Trim();
            if (a.Length == 0 || left.Contains(a, StringComparison.OrdinalIgnoreCase) || a.Contains(left, StringComparison.OrdinalIgnoreCase))
            {
                if (a.Length == 0) a = left;
                t = right;
            }
        }
        // "Artist, Other & Another" -> the lead artist matches LRCLIB far more often. (Duos such as
        // "Simon & Garfunkel" still match because callers try the uncleaned names first.)
        var lead = Regex.Split(a, @"\s*[,&]\s*", Opts)[0].Trim();
        return (t.Trim(), lead.Length > 0 ? lead : a);
    }

    /// <summary>
    /// Whether a fuzzy search is worth making for this (cleaned) track. A bare title with no artist and no length
    /// is too little to go on: the search returns whatever song shares the word, as "WhatsApp" did.
    /// </summary>
    public static bool WorthSearching(string track, string artist, TimeSpan duration) =>
        !string.IsNullOrWhiteSpace(track) && !NotMusic.IsAppName(track) &&
        (!string.IsNullOrWhiteSpace(artist) || duration > TimeSpan.Zero);

    /// <summary>
    /// Keeps the search results that can be this track: the title must match (one contains the other, ignoring case
    /// and punctuation), and with no artist to check, the length must be close too.
    /// </summary>
    public static IEnumerable<LrclibRecord> SearchMatches(IEnumerable<LrclibRecord> records, string track, string artist, TimeSpan duration)
    {
        var want = Letters(track);
        if (want.Length == 0) yield break;
        bool noArtist = string.IsNullOrWhiteSpace(artist);
        foreach (var r in records)
        {
            var got = Letters(r.Track);
            if (got.Length == 0 || !(want.Contains(got, StringComparison.Ordinal) || got.Contains(want, StringComparison.Ordinal))) continue;
            if (noArtist && (r.Duration <= 0 || Math.Abs(r.Duration - duration.TotalSeconds) > 30)) continue;
            yield return r;
        }
    }

    static string Letters(string s) => new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public static LrclibRecord? ParseRecord(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        string? Str(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double duration = e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
        bool instrumental = e.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True;
        return new LrclibRecord(Str("trackName") ?? Str("name") ?? "", Str("artistName") ?? "", duration, instrumental,
            Str("plainLyrics"), Str("syncedLyrics"));
    }

    /// <summary>Parses a /api/get object or a /api/search array.</summary>
    /// <returns>
    /// The records (empty for LRCLIB's "not found"), or null when the body isn't LRCLIB's JSON at all: a
    /// captive portal's login page or a proxy error. That must not be cached as "no lyrics".
    /// </returns>
    public static List<LrclibRecord>? ParseRecords(string json)
    {
        var list = new List<LrclibRecord>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object)) return null;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in doc.RootElement.EnumerateArray())
                    if (ParseRecord(e) is { } r) list.Add(r);
            }
            else if (ParseRecord(doc.RootElement) is { } r && (r.Plain is not null || r.Synced is not null || r.Instrumental))
            {
                list.Add(r);
            }
        }
        catch (JsonException)
        {
            return null;
        }
        return list;
    }

    /// <summary>
    /// Search results are fuzzy: prefer synced lyrics, then a duration within a few seconds of the
    /// playing track (a different edit or live version has different timing).
    /// </summary>
    public static LrclibRecord? PickBest(IEnumerable<LrclibRecord> records, TimeSpan duration)
    {
        double want = duration.TotalSeconds;
        LrclibRecord? best = null;
        double bestScore = double.MinValue;
        foreach (var r in records)
        {
            if (r.Synced is null && r.Plain is null && !r.Instrumental) continue;
            double score = 0;
            if (!string.IsNullOrWhiteSpace(r.Synced)) score += 10;
            else if (!string.IsNullOrWhiteSpace(r.Plain)) score += 4;
            if (want > 0 && r.Duration > 0)
            {
                double off = Math.Abs(r.Duration - want);
                if (off > 10) score -= 20; // almost certainly another version
                else score -= off * 0.5;
            }
            if (score > bestScore)
            {
                bestScore = score;
                best = r;
            }
        }
        return best;
    }

    public static TrackLyrics ToLyrics(LrclibRecord? r)
    {
        if (r is null) return TrackLyrics.None;
        if (!string.IsNullOrWhiteSpace(r.Synced))
        {
            var lines = Lrc.Parse(r.Synced);
            if (lines.Count > 0) return new TrackLyrics(LyricsKind.Synced, lines, r.Plain ?? "");
        }
        if (!string.IsNullOrWhiteSpace(r.Plain)) return new TrackLyrics(LyricsKind.Plain, Array.Empty<LyricLine>(), r.Plain.Trim());
        if (r.Instrumental) return new TrackLyrics(LyricsKind.Instrumental, Array.Empty<LyricLine>(), "");
        return TrackLyrics.None;
    }
}
