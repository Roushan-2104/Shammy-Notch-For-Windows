using System.Net.Http;
using System.Net.NetworkInformation;
using System.Windows.Threading;
using Shammy.Core.Ambient;

namespace Shammy.Modules.Ambient;

/// <summary>
/// Approximate location from the IP (cached a day), an Open-Meteo forecast every 20 minutes, the
/// weather card (v2 notch), and a one-off "rain likely" nudge. Pauses while offline and resumes when the network
/// comes back.
/// </summary>
internal sealed class WeatherFeature : IDisposable
{
    readonly ModuleContext _ctx;
    readonly AmbientData _data;
    readonly Action _save;
    readonly WeatherCard _card = new();
    readonly DispatcherTimer _timer;
    WeatherReport? _report;
    DateTime? _updatedUtc;
    int _failures;
    string? _lastError;
    Task? _inFlight;

    public WeatherFeature(ModuleContext ctx, AmbientData data, Action save)
    {
        _ctx = ctx;
        _data = data;
        _save = save;
        _timer = new DispatcherTimer(DispatcherPriority.Background, ctx.Ui);
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _ = RefreshAsync();
        };
    }

    public string State => _report is null
        ? $"no data (failures={_failures}{(_lastError is null ? "" : ", last error: " + _lastError)})"
        : $"{WeatherText.Now(_report, City)} (updated {AmbientNet.Ago(_updatedUtc)}, next in {(_timer.IsEnabled ? $"{_timer.Interval.TotalMinutes:0.#}m" : "-")})";

    string City => _data.Location?.City ?? "your area";

    public void Start()
    {
        // v2: the weather card replaces the Weather tab.
        _ctx.AddCard(new NotchCard
        {
            Id = "weather",
            Title = "Weather",
            Width = 176,
            Compact = _card.Compact,
            Detail = _card.BuildDetail,
            Order = 8,
            Chip = true,
            IsRelevant = () => _card.IsRelevant,
            Peek = _card.Peek,
        });
        _card.Open = () => _ctx.OpenCard("weather");
        _ctx.AddGlance("weather", _card.Glance);
        Views.Cards.CardPipes.Register(_ctx, "weather", _card.DescribeCompact, _card.DescribeDetail);
        _card.ShowStatus("Finding the weather\u2026");
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        // Let startup settle before the first request.
        Schedule(TimeSpan.FromSeconds(4));
    }

    void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable) return;
        _ctx.Ui.BeginInvoke(() =>
        {
            // Back online: refresh soon if the forecast is stale or never arrived.
            if (_report is null || _updatedUtc is null || DateTime.UtcNow - _updatedUtc > RefreshSchedule.Weather) Schedule(TimeSpan.FromSeconds(3));
        });
    }

    void Schedule(TimeSpan delay)
    {
        _timer.Stop();
        _timer.Interval = delay;
        _timer.Start();
    }

    public Task RefreshAsync() => _inFlight is { IsCompleted: false } ? _inFlight : _inFlight = RunRefreshAsync();

    async Task RunRefreshAsync()
    {
        if (!AmbientNet.IsOnline)
        {
            // Nothing to do until the network returns (OnNetworkChanged); check again later anyway.
            _lastError = "offline";
            if (_report is null) _card.ShowStatus("Weather will appear when you\u2019re back online");
            Schedule(TimeSpan.FromMinutes(10));
            return;
        }
        var location = _data.Location;
        var result = await Task.Run(() => FetchAsync(_ctx.Http, location));
        Apply(result);
    }

    sealed record FetchResult(GeoLocation? Location, WeatherReport? Report, string? Error);

    static async Task<FetchResult> FetchAsync(HttpClient http, GeoLocation? cached)
    {
        var location = cached is not null && cached.IsFresh(DateTime.UtcNow) ? cached : await LocateAsync(http).ConfigureAwait(false) ?? cached;
        if (location is null) return new FetchResult(null, null, "location unavailable");
        try
        {
            var json = await AmbientNet.GetAsync(http, OpenMeteo.ForecastUrl(location.Latitude, location.Longitude)).ConfigureAwait(false);
            var report = OpenMeteo.Parse(json);
            return new FetchResult(location, report, report is null ? "unexpected forecast response" : null);
        }
        catch (Exception ex)
        {
            return new FetchResult(location, null, ex.Message);
        }
    }

    static async Task<GeoLocation?> LocateAsync(HttpClient http)
    {
        foreach (var (url, parse) in new (string, Func<string, DateTime, GeoLocation?>)[]
                 {
                     (IpLocation.PrimaryUrl, IpLocation.ParseIpApiCo),
                     (IpLocation.FallbackUrl, IpLocation.ParseIpWhoIs),
                 })
        {
            try
            {
                if (parse(await AmbientNet.GetAsync(http, url).ConfigureAwait(false), DateTime.UtcNow) is { } loc) return loc;
            }
            catch (Exception)
            {
                // Rate limited or down; try the next one.
            }
        }
        return null;
    }

    void Apply(FetchResult result)
    {
        if (result.Location is { } loc && !Equals(loc, _data.Location))
        {
            _data.Location = loc;
            _save();
        }
        if (result.Report is not { } report)
        {
            _failures++;
            _lastError = result.Error;
            _ctx.Warn($"weather refresh failed ({_failures}): {result.Error}");
            if (_report is null) _card.ShowStatus("Weather unavailable right now \u00B7 retrying");
            Schedule(RefreshSchedule.WeatherAfterFailure(_failures));
            return;
        }

        var previous = _report;
        _report = report;
        _updatedUtc = DateTime.UtcNow;
        _failures = 0;
        _lastError = null;
        bool wasRelevant = _card.IsRelevant;
        _card.Show(report, City);
        if (!wasRelevant) _ctx.CardsChanged();
        Schedule(RefreshSchedule.Weather);

        if (RainAlert.Check(previous, report, DateTime.UtcNow, _data.LastRainAlertUtc) is { } minutes)
        {
            _data.LastRainAlertUtc = DateTime.UtcNow;
            _save();
            _ctx.ShowMessage(WeatherCodes.CloudGlyph, RainAlert.Message(minutes), seconds: 5);
        }
    }

    /// <summary>For the test pipe: refresh now and return a one-line summary.</summary>
    /// <summary>Test pipe only: "Mumbai|19.07|72.87" sets the place, so screenshots don't show where this PC really is.</summary>
    public string AtForTest(string arg)
    {
        var p = arg.Split('|');
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        _data.Location = new GeoLocation(p[0], double.Parse(p[1], inv), double.Parse(p[2], inv), DateTime.UtcNow);
        return RefreshNowBlocking();
    }

    public string RefreshNowBlocking()
    {
        if (!AmbientNet.IsOnline) return "error: offline";
        var location = _data.Location;
        var result = AmbientNet.Block(() => FetchAsync(_ctx.Http, location), TimeSpan.FromSeconds(40), new FetchResult(null, null, "timed out"));
        Apply(result);
        if (result.Report is not { } r) return "error: " + result.Error;
        var hours = r.NextHours(r.LocalNow(DateTime.UtcNow), 6)
            .Select(h => $"{WeatherText.HourLabel(h.Time)} {WeatherText.Temp(h.Temperature)} {h.RainChance?.ToString() ?? "-"}%");
        return $"{WeatherText.Now(r, City)} | next: {string.Join(", ", hours)}";
    }

    public async Task<CommandResult> RunCommandAsync(WeatherQuery query)
    {
        if (_report is null || _updatedUtc is null || DateTime.UtcNow - _updatedUtc > TimeSpan.FromMinutes(30)) await RefreshAsync();
        if (_report is not { } r)
            return new CommandResult(WeatherCodes.CloudGlyph, "Weather unavailable",
                AmbientNet.IsOnline ? "Couldn't reach the forecast; trying again soon" : "You're offline", IsError: true);

        // Opens the weather card's detail once the command's confirmation card has closed.
        _ctx.OpenCard("weather");
        if (query == WeatherQuery.Tomorrow && WeatherText.Tomorrow(r) is { } tomorrow)
            return new CommandResult(WeatherCodes.Glyph(r.TomorrowCode ?? r.Code), "Tomorrow in " + City, tomorrow);

        // A refresh that failed (offline) leaves an old report; say how old instead of passing it off as now.
        var stale = _updatedUtc is { } at && DateTime.UtcNow - at > TimeSpan.FromHours(2)
            ? $" \u00B7 as of {Shammy.Core.Cards.ClockFormat.Time(at.ToLocalTime(), Views.Cards.CardKit.H24)}" : "";
        return new CommandResult(WeatherCodes.Glyph(r.Code, r.IsDay), $"{WeatherText.Temp(r.Temperature)} {WeatherCodes.Describe(r.Code)}",
            $"{City} \u00B7 feels {WeatherText.Temp(r.FeelsLike)} \u00B7 H {WeatherText.Temp(r.TodayHigh)} L {WeatherText.Temp(r.TodayLow)}{stale}");
    }

    // ---- Weather for a named place ("weather in mumbai") ------------------------------------------

    static readonly TimeSpan CityReportFresh = TimeSpan.FromMinutes(30);
    const int MaxPlaces = 60;

    /// <summary>Forecasts for named places this session (by place key); the user's own city stays in <see cref="_report"/>.</summary>
    readonly Dictionary<string, (WeatherReport Report, DateTime FetchedUtc)> _cityReports = new();

    /// <summary>Names the geocoder didn't know this session, so a retry goes straight to the web.</summary>
    readonly HashSet<string> _unknownPlaces = new();

    /// <summary>Cheap: the answer itself when a fresh forecast for that place is cached, otherwise what Enter will do.</summary>
    public CommandPreview? PreviewCity(CityWeatherQuery q)
    {
        var key = CityWeather.Key(q.City);
        if (_unknownPlaces.Contains(key)) return null;
        var place = _data.Places.GetValueOrDefault(key);
        var label = place?.Label ?? q.City;
        if (place is not null && _cityReports.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.FetchedUtc < CityReportFresh)
        {
            var answer = CityAnswer(cached.Report, place, q.When, DateTime.MinValue);
            return new CommandPreview(answer.Glyph, $"{answer.Title} · {(q.When == WeatherQuery.Tomorrow ? "tomorrow" : place.Name)}", answer.Detail);
        }
        return new CommandPreview(WeatherCodes.CloudGlyph, q.When == WeatherQuery.Tomorrow ? $"Tomorrow’s weather in {label}" : $"Weather in {label}", "Forecast from Open-Meteo");
    }

    public async Task<CommandResult> RunCityAsync(CityWeatherQuery q, string original)
    {
        var key = CityWeather.Key(q.City);
        var search = original.Trim();
        if (_unknownPlaces.Contains(key)) return new CommandResult("", "", SearchInstead: search);
        if (!AmbientNet.IsOnline) return new CommandResult(WeatherCodes.CloudGlyph, "Weather unavailable", "You're offline", IsError: true);

        var place = _data.Places.GetValueOrDefault(key);
        if (place is null)
        {
            var name = CityWeather.LookupName(q.City);
            var lookup = await Task.Run(() => GeocodeAsync(_ctx.Http, name));
            if (lookup.Error is not null)
            {
                _ctx.Warn($"geocoding failed: {lookup.Error}");
                return new CommandResult(WeatherCodes.CloudGlyph, "Couldn't reach the weather service", "Check your connection and try again", IsError: true);
            }
            if (lookup.Place is null)
            {
                // Not a place after all ("weather in my dreams"): the web, exactly as before this command existed.
                _unknownPlaces.Add(key);
                return new CommandResult("", "", SearchInstead: search);
            }
            place = lookup.Place;
            if (_data.Places.Count >= MaxPlaces) _data.Places.Remove(_data.Places.Keys.First());
            _data.Places[key] = place;
            _save();
        }

        if (!_cityReports.TryGetValue(key, out var cached) || DateTime.UtcNow - cached.FetchedUtc >= CityReportFresh)
        {
            var at = place;
            var fetched = await Task.Run(() => ForecastAsync(_ctx.Http, at.Latitude, at.Longitude));
            if (fetched is null)
                return new CommandResult(WeatherCodes.CloudGlyph, $"Couldn't get the weather for {place.Name}", "The forecast service didn't answer; try again soon", IsError: true);
            cached = (fetched, DateTime.UtcNow);
            _cityReports[key] = cached;
        }
        return CityAnswer(cached.Report, place, q.When, cached.FetchedUtc);
    }

    static CommandResult CityAnswer(WeatherReport r, Place place, WeatherQuery when, DateTime fetchedUtc)
    {
        if (when == WeatherQuery.Tomorrow && WeatherText.Tomorrow(r) is { } tomorrow)
            return new CommandResult(WeatherCodes.Glyph(r.TomorrowCode ?? r.Code), "Tomorrow in " + place.Label, tomorrow);
        return new CommandResult(WeatherCodes.Glyph(r.Code, r.IsDay), $"{WeatherText.Temp(r.Temperature)} {WeatherCodes.Describe(r.Code)}",
            $"{place.Label} · feels {WeatherText.Temp(r.FeelsLike)} · H {WeatherText.Temp(r.TodayHigh)} L {WeatherText.Temp(r.TodayLow)}");
    }

    sealed record GeocodeResult(Place? Place, string? Error);

    static async Task<GeocodeResult> GeocodeAsync(HttpClient http, string name)
    {
        try
        {
            var json = await AmbientNet.GetAsync(http, OpenMeteoGeocoding.SearchUrl(name)).ConfigureAwait(false);
            // An answer without "results" means the name isn't a place; anything unparsable is a failure.
            if (!json.TrimStart().StartsWith('{')) return new GeocodeResult(null, "unexpected response");
            if (json.Contains("\"error\"", StringComparison.Ordinal) && !json.Contains("\"results\"", StringComparison.Ordinal))
                return new GeocodeResult(null, "service error");
            return new GeocodeResult(OpenMeteoGeocoding.Parse(json), null);
        }
        catch (Exception ex)
        {
            return new GeocodeResult(null, ex.Message);
        }
    }

    static async Task<WeatherReport?> ForecastAsync(HttpClient http, double lat, double lon)
    {
        try
        {
            return OpenMeteo.Parse(await AmbientNet.GetAsync(http, OpenMeteo.ForecastUrl(lat, lon)).ConfigureAwait(false));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>For the test pipe: "ambient-weather-city weather in mumbai tomorrow" → the answer, or what would happen.</summary>
    public string CityBlocking(string text)
    {
        if (CityWeather.Parse(text) is not { } q) return "not a city weather phrase";
        // The pipe runs on the UI thread, so the lookup runs on a worker (it touches no UI).
        var result = AmbientNet.Block(() => RunCityAsync(q, text), TimeSpan.FromSeconds(30),
            new CommandResult("", "timed out", IsError: true));
        return result.SearchInstead is { } s
            ? $"city={q.City} when={q.When} -> web search \"{s}\""
            : $"city={q.City} when={q.When} -> {result.Title} | {result.Detail}{(result.IsError ? " (error)" : "")}";
    }

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _timer.Stop();
    }
}
