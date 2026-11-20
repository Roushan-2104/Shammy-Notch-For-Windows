using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using Shammy.Core.Music;

namespace Shammy.Modules.Music;

/// <summary>
/// Lyrics from LRCLIB, cached per track for the session. Network code never captures the UI context
/// (ConfigureAwait(false)), so the test pipe can block on it from the UI thread without deadlocking.
/// </summary>
internal sealed class LyricsSource
{
    const string UserAgent = "Shammy/0.1 (Windows notch app; lyrics via lrclib.net)";

    readonly ModuleContext _ctx;
    readonly ConcurrentDictionary<string, Task<TrackLyrics>> _cache = new();

    public LyricsSource(ModuleContext ctx) => _ctx = ctx;

    public static string Key(string title, string artist) => (title.Trim() + "\n" + artist.Trim()).ToLowerInvariant();

    public bool TryGetCached(string title, string artist, out TrackLyrics lyrics)
    {
        lyrics = TrackLyrics.None;
        if (!_cache.TryGetValue(Key(title, artist), out var task) || !task.IsCompletedSuccessfully) return false;
        lyrics = task.Result;
        return true;
    }

    public Task<TrackLyrics> GetAsync(string title, string artist, TimeSpan duration)
    {
        var key = Key(title, artist);
        var task = _cache.GetOrAdd(key, _ => Task.Run(() => FetchAsync(title, artist, duration)));
        // Failures (offline, server busy) aren't cached, so the next track change or "lyrics" retries.
        _ = task.ContinueWith(t => _cache.TryRemove(new KeyValuePair<string, Task<TrackLyrics>>(key, t)),
            CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Default);
        return task;
    }

    async Task<TrackLyrics> FetchAsync(string title, string artist, TimeSpan duration)
    {
        int answered = 0;
        async Task<LrclibRecord?> TryAsync(string url)
        {
            var records = await GetRecordsAsync(url).ConfigureAwait(false);
            if (records is null) return null;
            answered++;
            return Lrclib.PickBest(records, duration);
        }

        // Exact lookup first (fast, and right for clean titles from music apps). It needs both a title and an artist:
        // LRCLIB answers 400 without one, and players such as a browser tab often report no artist...
        if (Lrclib.CanGet(title, artist) && await TryAsync(Lrclib.GetUrl(title, artist, duration)).ConfigureAwait(false) is { } hit)
            return Lrclib.ToLyrics(hit);

        // ...then with video-title noise and featured artists removed...
        var (track, lead) = Lrclib.Clean(title, artist);
        if (track.Length == 0) return TrackLyrics.None;
        if (Lrclib.CanGet(track, lead) &&
            (!track.Equals(title, StringComparison.OrdinalIgnoreCase) || !lead.Equals(artist, StringComparison.OrdinalIgnoreCase)))
        {
            if (await TryAsync(Lrclib.GetUrl(track, lead, duration)).ConfigureAwait(false) is { } hit2) return Lrclib.ToLyrics(hit2);
        }

        // ...then a fuzzy search.
        if (await TryAsync(Lrclib.SearchUrl((track + " " + lead).Trim())).ConfigureAwait(false) is { } hit3) return Lrclib.ToLyrics(hit3);

        // Only a real "not found" is cached; if the server never answered, let a later look retry.
        if (answered == 0) throw new HttpRequestException("LRCLIB is busy");
        return TrackLyrics.None;
    }

    /// <returns>The records, an empty list for "not found", or null when the server stayed busy.</returns>
    async Task<List<LrclibRecord>?> GetRecordsAsync(string url)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // LRCLIB asks clients to identify themselves.
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var response = await _ctx.Http.SendAsync(request).ConfigureAwait(false);
            // 400 is LRCLIB refusing a lookup it can't make (a missing field): as good as "not found", so the
            // fuzzy search still gets its turn.
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) return new List<LrclibRecord>();
            // LRCLIB answers 503 "server busy" under load, and reliably so for exact lookups it can't
            // satisfy from its own database; retry once, then move on to the next kind of lookup.
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                if (attempt >= 1) return null;
                await Task.Delay(1000).ConfigureAwait(false);
                continue;
            }
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return Lrclib.ParseRecords(json);
        }
    }
}
