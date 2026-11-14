using System.Globalization;
using System.Net.Http;
using Shammy.Core.Conversions;

namespace Shammy.Modules.SelectionTools;

/// <summary>
/// USD-based rates from Frankfurter (ECB data, free, no key), cached for an hour in memory and on disk.
/// When offline, rates up to two days old are still used: close enough for "what's this in rupees".
/// </summary>
internal sealed class ExchangeRates
{
    // api.frankfurter.app now redirects here; calling it directly saves a round trip.
    const string Endpoint = "https://api.frankfurter.dev/v1/latest?from=USD";
    static readonly TimeSpan FreshFor = TimeSpan.FromHours(1);
    static readonly TimeSpan UsableFor = TimeSpan.FromHours(48);
    static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

    readonly ModuleContext _ctx;
    readonly object _gate = new();
    Saved? _saved;
    bool _loaded;
    DateTime _failedAt;
    string _lastError = "";
    Task<RateTable?>? _fetch;

    /// <summary>
    /// The one set of rates, made by Selection tools (its store keeps the saved copy). Hover's prices use it too,
    /// rather than fetching and saving a second copy.
    /// </summary>
    public static ExchangeRates? Shared { get; private set; }

    public ExchangeRates(ModuleContext ctx)
    {
        _ctx = ctx;
        Shared ??= this;
    }

    /// <summary>What's stored in %LOCALAPPDATA%\Shammy\modules\selection.json.</summary>
    public sealed class Saved
    {
        public DateTime FetchedUtc { get; set; }
        public string Base { get; set; } = "USD";
        public string Date { get; set; } = "";
        public Dictionary<string, double> Rates { get; set; } = new();
    }

    /// <summary>Current rates, or null when there are none fresh enough. Never throws; safe from any thread.</summary>
    public async Task<RateTable?> GetAsync()
    {
        Task<RateTable?> fetch;
        lock (_gate)
        {
            LoadOnce();
            var age = _saved is null ? TimeSpan.MaxValue : DateTime.UtcNow - _saved.FetchedUtc;
            if (_saved is not null && age < FreshFor) return ToTable(_saved);
            if (_fetch is null && DateTime.UtcNow - _failedAt < RetryAfterFailure) return Usable();
            fetch = _fetch ??= Task.Run(FetchAsync);
        }
        RateTable? table = null;
        try
        {
            table = await fetch.ConfigureAwait(false);
        }
        finally
        {
            // Always clear it: a faulted fetch left in place would make every later conversion fail too.
            lock (_gate)
                if (_fetch == fetch) _fetch = null;
        }
        lock (_gate) return table ?? Usable();
    }

    /// <summary>The rates already on hand (memory or disk), without fetching: for previews on a keystroke.</summary>
    public RateTable? Cached()
    {
        lock (_gate)
        {
            LoadOnce();
            return Usable();
        }
    }

    public string Describe()
    {
        lock (_gate)
        {
            if (_saved is null) return _lastError.Length > 0 ? $"none ({_lastError})" : "none";
            var age = DateTime.UtcNow - _saved.FetchedUtc;
            var inr = _saved.Rates.TryGetValue("INR", out var r) ? r.ToString("0.####", CultureInfo.InvariantCulture) : "?";
            return $"{_saved.Rates.Count} codes, {_saved.Base} base, date {_saved.Date}, fetched {(int)age.TotalMinutes} min ago, USD-INR {inr}";
        }
    }

    void LoadOnce()
    {
        if (_loaded) return;
        _loaded = true;
        var saved = _ctx.Load<Saved>();
        if (saved.Rates.Count > 1) _saved = saved;
    }

    RateTable? Usable() =>
        _saved is not null && DateTime.UtcNow - _saved.FetchedUtc < UsableFor ? ToTable(_saved) : null;

    static RateTable ToTable(Saved s) => new(s.Base, s.Date, s.Rates);

    async Task<RateTable?> FetchAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            var json = await _ctx.Http.GetStringAsync(Endpoint, cts.Token).ConfigureAwait(false);
            if (!Currency.TryParseRates(json, out var table) || table.Base != "USD")
            {
                Fail("unexpected response");
                return null;
            }
            var saved = new Saved { FetchedUtc = DateTime.UtcNow, Base = table.Base, Date = table.Date, Rates = new(table.Rates) };
            lock (_gate)
            {
                _saved = saved;
                _lastError = "";
            }
            _ctx.Save(saved);
            _ctx.Info($"Exchange rates updated ({table.Rates.Count} currencies, {table.Date})");
            return table;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            Fail(ex is OperationCanceledException ? "timed out" : ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _ctx.Warn("Exchange rate update failed", ex);
            Fail(ex.GetType().Name);
            return null;
        }
    }

    void Fail(string why)
    {
        lock (_gate)
        {
            _failedAt = DateTime.UtcNow;
            _lastError = why;
        }
        _ctx.Warn($"Exchange rates unavailable: {why}");
    }
}
