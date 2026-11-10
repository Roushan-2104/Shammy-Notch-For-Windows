using System.Net.Http;
using System.Net.NetworkInformation;
using Shammy.Core.Ambient;

namespace Shammy.Modules.Ambient;

/// <summary>Everything the ambient features remember between runs (%LOCALAPPDATA%\Shammy\modules\ambient.json).</summary>
internal sealed class AmbientData
{
    public GeoLocation? Location { get; set; }
    public DateTime? LastRainAlertUtc { get; set; }

    /// <summary>Places asked about by name ("weather in mumbai"), keyed by <see cref="CityWeather.Key"/>: a name never moves.</summary>
    public Dictionary<string, Place> Places { get; set; } = new();

    public bool TickerEnabled { get; set; }
    public List<TickerKind> TickerKinds { get; set; } = new();
    public List<string> StockSymbols { get; set; } = new(StocksProvider.DefaultSymbols);
    public string? CricketTeam { get; set; }
}

/// <summary>
/// Weather and customizable tickers: quiet things for the resting notch's glance and the weather card, plus
/// "pet on/off" for the resting notch's pet (AppSettings.PetEnabled) and a live India cricket match as a live
/// activity. Each feature owns its timers and only runs while it has something to do.
/// </summary>
internal sealed class AmbientModule : IShammyModule
{
    ModuleContext _ctx = null!;
    AmbientData _data = new();
    WeatherFeature? _weather;
    TickerFeature? _ticker;
    LiveCricketFeature? _cricket;

    public string Name => "ambient";

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        _data = ctx.Load<AmbientData>();
        _data.TickerKinds = _data.TickerKinds.Distinct().ToList();
        if (_data.StockSymbols.Count == 0) _data.StockSymbols = new(StocksProvider.DefaultSymbols);

        _weather = new WeatherFeature(ctx, _data, Save);
        _ticker = new TickerFeature(ctx, _data, Save);
        _cricket = new LiveCricketFeature(ctx);
        _weather.Start();
        _ticker.Start();
        _cricket.Start();

        ctx.AddCommand(new AmbientCommand(_weather, _ticker, _cricket, ctx));
        ctx.AddCommand(new LiveCricketTextCommand(_cricket));
        ctx.AddTrayToggle("Ticker", () => _data.TickerEnabled, on => _ticker.SetEnabled(on));
        // The same setting as "pet on/off" (AppSettings.PetEnabled).
        ctx.AddTrayToggle("Shammy the pet", () => ctx.Settings.PetEnabled, on => ctx.Notch.SetPetEnabled(on));

        ctx.AddPipeCommand("ambient-weather", _ => _weather.RefreshNowBlocking());
        ctx.AddPipeCommand("ambient-weather-city", arg => _weather.CityBlocking(arg));
        ctx.AddPipeCommand("ambient-weather-at", arg => _weather.AtForTest(arg));
        ctx.AddPipeCommand("ambient-ticker-preview", arg => _ticker.PreviewBlocking(arg));
        ctx.AddPipeCommand("ambient-state", _ =>
            $"online={NetworkInterface.GetIsNetworkAvailable()}; weather: {_weather.State}; ticker: {_ticker.State}; pet: {(ctx.Settings.PetEnabled ? "on" : "off")}");
    }

    void Save() => _ctx.Save(_data);

    public void Dispose()
    {
        _weather?.Dispose();
        _ticker?.Dispose();
        _cricket?.Dispose();
    }
}

/// <summary>"weather", "weather in mumbai", "ticker cricket", "pet on"... Parsing lives in Shammy.Core so it is unit tested.</summary>
internal sealed class AmbientCommand(WeatherFeature weather, TickerFeature ticker, LiveCricketFeature cricket, ModuleContext ctx) : ITextCommand, ICommandPreview
{
    public bool CanHandle(string text) =>
        AmbientCommands.ParseWeather(text) is not null || CityWeather.Parse(text) is not null
        || AmbientCommands.ParseTicker(text) is not null || AmbientCommands.ParsePet(text) is not null;

    public CommandPreview? Preview(string text)
    {
        if (AmbientCommands.ParseWeather(text) is not null) return null;
        return CityWeather.Parse(text) is { } city ? weather.PreviewCity(city) : null;
    }

    public async Task<CommandResult> RunAsync(string text)
    {
        if (AmbientCommands.ParseWeather(text) is { } query) return await weather.RunCommandAsync(query);
        if (CityWeather.Parse(text) is { } city) return await weather.RunCityAsync(city, text);
        if (AmbientCommands.ParseTicker(text) is { } cmd)
        {
            var result = ticker.Run(cmd);
            // Asking for the cricket ticker is asking to follow cricket: the live match comes with it.
            if (CricketLive.TurnedOnBy(cmd)) cricket.SetEnabled(true);
            return result;
        }
        if (AmbientCommands.ParsePet(text) is { } on)
        {
            // The pet is the resting notch's eyes; off leaves just the rotating glance there.
            ctx.Notch.SetPetEnabled(on);
            return new CommandResult(on ? "\uE76E" : "\uE711", on ? "Pet on" : "Pet off",
                on ? "Shammy's eyes are back in the notch" : "The resting notch shows just the glance");
        }
        return new CommandResult("\uE783", "Not an ambient command", IsError: true);
    }
}

/// <summary>Small helpers shared by the ambient features.</summary>
internal static class AmbientNet
{
    public const string BrowserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    public static bool IsOnline => NetworkInterface.GetIsNetworkAvailable();

    /// <summary>GET as text; throws on non-success so callers count it as a failure.</summary>
    public static async Task<string> GetAsync(HttpClient http, string url, bool browserAgent = false, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (browserAgent) request.Headers.TryAddWithoutValidation("User-Agent", BrowserAgent);
        using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && !body.TrimStart().StartsWith('{'))
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} from {new Uri(url).Host}");
        return body;
    }

    /// <summary>Run network work off the UI thread and wait for it; only for the synchronous test pipe.</summary>
    public static T Block<T>(Func<Task<T>> work, TimeSpan timeout, T onTimeout)
    {
        var task = Task.Run(work);
        return task.Wait(timeout) ? task.Result : onTimeout;
    }

    public static string Ago(DateTime? utc)
    {
        if (utc is not { } at) return "never";
        var span = DateTime.UtcNow - at;
        return span.TotalSeconds < 60 ? $"{(int)span.TotalSeconds}s ago" : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes}m ago" : $"{(int)span.TotalHours}h ago";
    }
}
