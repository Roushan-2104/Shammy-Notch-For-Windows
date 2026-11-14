using System.Net.Http;
using Shammy.Core.Ambient;

namespace Shammy.Modules.Ambient;

/// <summary>
/// A source of short ticker lines. Adding a provider (football, F1, weather alerts...) means one class
/// here and a <see cref="TickerKind"/>; the feature handles scheduling, backoff and display.
/// Implementations run off the UI thread and throw when the source is unreachable.
/// </summary>
internal interface ITickerProvider
{
    TickerKind Kind { get; }
    string Name { get; }

    Task<IReadOnlyList<TickerItem>> RefreshAsync(HttpClient http, CancellationToken ct);
}

/// <summary>Live and recent matches from ESPNcricinfo's public RSS, optionally for one team.</summary>
internal sealed class CricketProvider(Func<string?> team) : ITickerProvider
{
    public TickerKind Kind => TickerKind.Cricket;
    public string Name => "cricket";

    public async Task<IReadOnlyList<TickerItem>> RefreshAsync(HttpClient http, CancellationToken ct)
    {
        var xml = await CricketRss.GetAsync(http, ct).ConfigureAwait(false);
        return CricketFeed.Items(CricketFeed.Parse(xml), team());
    }
}

/// <summary>
/// ESPNcricinfo's RSS, shared by the live score and the ticker (both read it every 30 s during a match): a fetch
/// under way, or one finished less than <see cref="Fresh"/> ago, is reused instead of asking again.
/// </summary>
internal static class CricketRss
{
    static readonly TimeSpan Fresh = TimeSpan.FromSeconds(25);
    static readonly object Gate = new();
    static Task<string>? _last;
    static DateTime _lastUtc;

    public static Task<string> GetAsync(HttpClient http, CancellationToken ct)
    {
        lock (Gate)
        {
            if (_last is not { } last || last.IsFaulted || last.IsCanceled || (last.IsCompleted && DateTime.UtcNow - _lastUtc >= Fresh))
            {
                _lastUtc = DateTime.UtcNow;
                _last = last = FetchAsync(http);
            }
            // One caller giving up doesn't cancel the fetch the other is waiting on.
            return last.WaitAsync(ct);
        }
    }

    static async Task<string> FetchAsync(HttpClient http)
    {
        var xml = await AmbientNet.GetAsync(http, CricketFeed.Url).ConfigureAwait(false);
        if (!xml.Contains("<rss", StringComparison.OrdinalIgnoreCase)) throw new HttpRequestException("the cricket feed returned something other than RSS");
        return xml;
    }
}

/// <summary>Quotes from Yahoo Finance's chart endpoint, one request per symbol.</summary>
internal sealed class StocksProvider(Func<IReadOnlyList<string>> symbols) : ITickerProvider
{
    public static readonly string[] DefaultSymbols = { "RELIANCE.NS", "TCS.NS", "^NSEI" };

    public TickerKind Kind => TickerKind.Stocks;
    public string Name => "stocks";

    public async Task<IReadOnlyList<TickerItem>> RefreshAsync(HttpClient http, CancellationToken ct)
    {
        var list = symbols();
        if (list.Count == 0) return Array.Empty<TickerItem>();
        var quotes = await Task.WhenAll(list.Select(s => QuoteAsync(http, s, ct))).ConfigureAwait(false);
        var items = quotes.Where(q => q is not null).Select(q => new TickerItem(TickerKind.Stocks, TickerFormat.Quote(q!))).ToList();
        if (items.Count == 0) throw new HttpRequestException("no quotes (Yahoo unreachable or symbols unknown)");
        return items;
    }

    static async Task<StockQuote?> QuoteAsync(HttpClient http, string symbol, CancellationToken ct)
    {
        // query2 is the same service on another host; try it when query1 is throttled.
        foreach (var host in new[] { "query1", "query2" })
        {
            try
            {
                var url = YahooChart.Url(symbol).Replace("://query1.", $"://{host}.");
                var json = await AmbientNet.GetAsync(http, url, browserAgent: true, ct).ConfigureAwait(false);
                return YahooChart.Parse(json);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Next host, then give up on this symbol.
            }
        }
        return null;
    }
}

/// <summary>Bitcoin and Ether in rupees from CoinGecko's free simple-price endpoint.</summary>
internal sealed class CryptoProvider : ITickerProvider
{
    public TickerKind Kind => TickerKind.Crypto;
    public string Name => "crypto";

    public async Task<IReadOnlyList<TickerItem>> RefreshAsync(HttpClient http, CancellationToken ct)
    {
        var items = CoinGecko.Parse(await AmbientNet.GetAsync(http, CoinGecko.Url, ct: ct).ConfigureAwait(false));
        if (items.Count == 0) throw new HttpRequestException("CoinGecko returned no prices (rate limited?)");
        return items;
    }
}
