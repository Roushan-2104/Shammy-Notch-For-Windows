using Shammy.Core.Ambient;

namespace Shammy.Core.Tests;

public class WeatherCodeTests
{
    [Theory]
    [InlineData(0, WeatherKind.Clear, "Clear")]
    [InlineData(1, WeatherKind.Clear, "Mostly clear")]
    [InlineData(2, WeatherKind.PartlyCloudy, "Partly cloudy")]
    [InlineData(3, WeatherKind.Cloudy, "Overcast")]
    [InlineData(45, WeatherKind.Fog, "Fog")]
    [InlineData(53, WeatherKind.Drizzle, "Drizzle")]
    [InlineData(61, WeatherKind.Rain, "Light rain")]
    [InlineData(82, WeatherKind.Rain, "Violent showers")]
    [InlineData(75, WeatherKind.Snow, "Heavy snow")]
    [InlineData(95, WeatherKind.Thunderstorm, "Thunderstorm")]
    [InlineData(99, WeatherKind.Thunderstorm, "Thunderstorm with hail")]
    [InlineData(42, WeatherKind.Unknown, "Unknown")]
    public void Maps_wmo_codes(int code, WeatherKind kind, string text)
    {
        Assert.Equal(kind, WeatherCodes.Kind(code));
        Assert.Equal(text, WeatherCodes.Describe(code));
    }

    [Fact]
    public void Picks_day_and_night_glyphs()
    {
        Assert.Equal(WeatherCodes.SunGlyph, WeatherCodes.Glyph(0, isDay: true));
        Assert.Equal(WeatherCodes.MoonGlyph, WeatherCodes.Glyph(0, isDay: false));
        Assert.Equal(WeatherCodes.CloudGlyph, WeatherCodes.Glyph(63));
        Assert.Equal(WeatherCodes.BoltGlyph, WeatherCodes.Glyph(95));
        Assert.Equal(WeatherCodes.FogGlyph, WeatherCodes.Glyph(48));
    }

    [Fact]
    public void Wetness_and_intensity()
    {
        Assert.Equal(1, WeatherCodes.Intensity(61));
        Assert.Equal(3, WeatherCodes.Intensity(65));
        Assert.Equal(0, WeatherCodes.Intensity(2));
    }
}

public class WeatherParsingTests
{
    const string Forecast = """
        {"latitude":51.51,"longitude":-0.13,"utc_offset_seconds":19800,"timezone":"Asia/Kolkata",
         "current":{"time":"2026-11-02T22:00","interval":900,"temperature_2m":24.4,"weather_code":3,"is_day":0,"apparent_temperature":30.3},
         "hourly":{"time":["2026-11-02T21:00","2026-11-02T22:00","2026-11-02T23:00","2026-11-03T00:00","2026-11-03T01:00","2026-11-03T02:00","2026-11-03T03:00","2026-11-03T04:00"],
                   "temperature_2m":[25.0,24.4,24.1,23.9,23.8,23.6,23.5,null],
                   "precipitation_probability":[10,20,65,40,null,5,0,0],
                   "weather_code":[3,3,61,3,3,2,1,0]},
         "daily":{"time":["2026-11-02","2026-11-03"],"temperature_2m_max":[27.0,30.2],"temperature_2m_min":[24.1,23.7],
                  "weather_code":[95,95],"precipitation_probability_max":[98,51]}}
        """;

    static readonly DateTime NowUtc = new(2026, 11, 2, 16, 40, 0, DateTimeKind.Utc); // 22:10 in IST

    [Fact]
    public void Parses_open_meteo_forecast()
    {
        var r = OpenMeteo.Parse(Forecast)!;
        Assert.NotNull(r);
        Assert.Equal(24.4, r.Temperature);
        Assert.Equal(30.3, r.FeelsLike);
        Assert.Equal(3, r.Code);
        Assert.False(r.IsDay);
        Assert.Equal(27.0, r.TodayHigh);
        Assert.Equal(24.1, r.TodayLow);
        Assert.Equal(30.2, r.TomorrowHigh);
        Assert.Equal(95, r.TomorrowCode);
        Assert.Equal(51, r.TomorrowRainChance);
        Assert.Equal(7, r.Hours.Count); // the hour with a null temperature is dropped
        Assert.Null(r.Hours[4].RainChance);
    }

    [Fact]
    public void Next_hours_start_after_now()
    {
        var r = OpenMeteo.Parse(Forecast)!;
        var local = r.LocalNow(NowUtc);
        Assert.Equal(new DateTime(2026, 11, 2, 22, 10, 0), local);
        var next = r.NextHours(local, 6);
        Assert.Equal(5, next.Count);
        Assert.Equal(23, next[0].Time.Hour);
        Assert.Equal("11 PM", WeatherText.HourLabel(next[0].Time));
        Assert.Equal("12 AM", WeatherText.HourLabel(next[1].Time));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"error":true,"reason":"Latitude must be in range of -90 to 90\u00B0. Given: 999.0."}""")]
    [InlineData("""{"current":{"temperature_2m":1},"daily":{}}""")]
    public void Rejects_bad_forecasts(string json) => Assert.Null(OpenMeteo.Parse(json));

    [Fact]
    public void Forecast_url_uses_invariant_numbers()
    {
        var url = OpenMeteo.ForecastUrl(51.50741, -0.1278);
        Assert.Contains("latitude=51.5074&longitude=-0.1278", url);
        Assert.Contains("precipitation_probability", url);
        Assert.Contains("timezone=auto", url);
    }

    [Fact]
    public void Summaries()
    {
        var r = OpenMeteo.Parse(Forecast)!;
        Assert.Equal("24\u00B0 Overcast in London \u00B7 feels 30\u00B0 \u00B7 H 27\u00B0 L 24\u00B0", WeatherText.Now(r, "London"));
        Assert.Equal("Thunderstorm, 30\u00B0/24\u00B0, 51% chance of rain", WeatherText.Tomorrow(r));
        Assert.Equal("-3\u00B0", WeatherText.Temp(-2.5));
    }

    [Fact]
    public void Parses_ip_locations()
    {
        var now = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
        var a = IpLocation.ParseIpApiCo("""{"ip":"x","city":"Paris","latitude":48.85661,"longitude":2.35222}""", now)!;
        Assert.Equal("Paris", a.City);
        Assert.Equal(2.35222, a.Longitude);
        var b = IpLocation.ParseIpWhoIs("""{"success":true,"city":"London","latitude":51.5074,"longitude":-0.1278}""", now)!;
        Assert.Equal("London", b.City);
        Assert.Null(IpLocation.ParseIpApiCo("""{"error":true,"reason":"RateLimited"}""", now));
        Assert.Null(IpLocation.ParseIpWhoIs("""{"success":false,"message":"Reserved range"}""", now));
        Assert.Null(IpLocation.ParseIpWhoIs("<html>", now));
        Assert.StartsWith("https://", IpLocation.FallbackUrl);
        Assert.Equal("Your area", IpLocation.ParseIpApiCo("""{"latitude":1,"longitude":2}""", now)!.City);
    }

    [Fact]
    public void Location_cache_lasts_a_day()
    {
        var at = new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);
        var loc = new GeoLocation("X", 1, 2, at);
        Assert.True(loc.IsFresh(at.AddHours(23)));
        Assert.False(loc.IsFresh(at.AddHours(25)));
        Assert.False(loc.IsFresh(at.AddHours(-1))); // clock went backwards: refetch
    }
}

public class RainAlertTests
{
    static WeatherReport Report(int nextHourChance, int hour = 23) => new(
        new DateTime(2026, 11, 2, 22, 0, 0), 0, 24, 25, 3, true, 27, 22, null, null, null, null,
        new List<HourForecast>
        {
            new(new DateTime(2026, 11, 2, 22, 0, 0), 24, 10, 3),
            new(new DateTime(2026, 11, 2, hour, 0, 0), 24, nextHourChance, 61),
        });

    static readonly DateTime Now = new(2026, 11, 2, 22, 13, 0, DateTimeKind.Utc);

    [Fact]
    public void Alerts_when_next_hour_turns_wet()
    {
        Assert.Equal(50, RainAlert.Check(Report(30), Report(70), Now, null));
        Assert.Equal("Rain likely in ~50 min", RainAlert.Message(50));
    }

    [Fact]
    public void No_alert_when_it_was_already_expected_or_stays_dry()
    {
        Assert.Null(RainAlert.Check(Report(65), Report(80), Now, null));
        Assert.Null(RainAlert.Check(Report(30), Report(55), Now, null));
        Assert.Null(RainAlert.Check(null, Report(90), Now, null));
    }

    [Fact]
    public void At_most_once_per_three_hours()
    {
        Assert.Null(RainAlert.Check(Report(10), Report(90), Now, Now.AddHours(-2)));
        Assert.NotNull(RainAlert.Check(Report(10), Report(90), Now, Now.AddHours(-3.1)));
    }

    [Fact]
    public void Ignores_slots_beyond_the_next_hour()
    {
        // Only the upcoming hour counts; an earlier slot with rain in it is ignored.
        Assert.Null(RainAlert.Check(Report(10, hour: 1), Report(90, hour: 1), Now, null));
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(47, 50)]
    [InlineData(50, 50)]
    [InlineData(90, 60)]
    public void Rounds_minutes(int minutes, int expected) => Assert.Equal(expected, RainAlert.RoundMinutes(TimeSpan.FromMinutes(minutes)));
}

public class AmbientCommandTests
{
    [Theory]
    [InlineData("weather", WeatherQuery.Now)]
    [InlineData("Weather", WeatherQuery.Now)]
    [InlineData("what's the weather", WeatherQuery.Now)]
    [InlineData("What\u2019s the weather?", WeatherQuery.Now)]
    [InlineData("whats the weather like", WeatherQuery.Now)]
    [InlineData("what is the weather today", WeatherQuery.Now)]
    [InlineData("how's the weather outside", WeatherQuery.Now)]
    [InlineData("weather forecast", WeatherQuery.Now)]
    [InlineData("show me the weather", WeatherQuery.Now)]
    [InlineData("today's weather", WeatherQuery.Now)]
    [InlineData("will it rain", WeatherQuery.Now)]
    [InlineData("weather tomorrow", WeatherQuery.Tomorrow)]
    [InlineData("what's the weather tomorrow", WeatherQuery.Tomorrow)]
    [InlineData("weather for tomorrow", WeatherQuery.Tomorrow)]
    [InlineData("tomorrow's weather", WeatherQuery.Tomorrow)]
    [InlineData("will it rain tomorrow", WeatherQuery.Tomorrow)]
    public void Recognises_weather_commands(string text, WeatherQuery query) => Assert.Equal(query, AmbientCommands.ParseWeather(text));

    [Theory]
    [InlineData("weather forecast in paris history")]
    [InlineData("weather in london")]
    [InlineData("what's the weather in tokyo")]
    [InlineData("weather channel")]
    [InlineData("weathering with you")]
    [InlineData("the weather girls it's raining men")]
    [InlineData("best stocks to buy")]
    [InlineData("how does weather forecasting work")]
    [InlineData("will it rain in seattle tomorrow")]
    [InlineData("")]
    public void Leaves_searches_alone(string text)
    {
        Assert.Null(AmbientCommands.ParseWeather(text));
        Assert.Null(AmbientCommands.ParseTicker(text));
        Assert.Null(AmbientCommands.ParsePet(text));
    }

    [Theory]
    [InlineData("ticker", TickerAction.Status, null)]
    [InlineData("ticker on", TickerAction.On, null)]
    [InlineData("ticker off", TickerAction.Off, null)]
    [InlineData("Ticker cricket", TickerAction.Enable, TickerKind.Cricket)]
    [InlineData("ticker crypto", TickerAction.Enable, TickerKind.Crypto)]
    [InlineData("ticker stocks", TickerAction.Enable, TickerKind.Stocks)]
    [InlineData("ticker cricket off", TickerAction.Disable, TickerKind.Cricket)]
    [InlineData("ticker off crypto", TickerAction.Disable, TickerKind.Crypto)]
    public void Parses_ticker_commands(string text, TickerAction action, TickerKind? kind)
    {
        var cmd = AmbientCommands.ParseTicker(text)!;
        Assert.Equal(action, cmd.Action);
        Assert.Equal(kind, cmd.Kind);
    }

    [Fact]
    public void Parses_ticker_symbols()
    {
        var set = AmbientCommands.ParseTicker("ticker stocks RELIANCE.NS tcs.ns ^NSEI")!;
        Assert.Equal(TickerAction.SetSymbols, set.Action);
        Assert.Equal(new[] { "RELIANCE.NS", "TCS.NS", "^NSEI" }, set.Symbols);

        var add = AmbientCommands.ParseTicker("ticker add M&M.NS")!;
        Assert.Equal(TickerAction.AddSymbols, add.Action);
        Assert.Equal("M&M.NS", Assert.Single(add.Symbols!));

        var remove = AmbientCommands.ParseTicker("ticker remove tcs.ns")!;
        Assert.Equal(TickerAction.RemoveSymbols, remove.Action);
        Assert.Equal("TCS.NS", Assert.Single(remove.Symbols!));

        var team = AmbientCommands.ParseTicker("ticker cricket india")!;
        Assert.Equal(TickerAction.Enable, team.Action);
        Assert.Equal("india", Assert.Single(team.Symbols!));
    }

    [Theory]
    [InlineData("ticker tape parade history")]
    [InlineData("ticker add")]
    [InlineData("ticker add <script>")]
    [InlineData("ticker symbol for apple")]
    [InlineData("stock ticker for apple")]
    [InlineData("tickers")]
    public void Rejects_other_ticker_text(string text) => Assert.Null(AmbientCommands.ParseTicker(text));

    [Theory]
    [InlineData("pet on", true)]
    [InlineData("Pet off", false)]
    [InlineData("pet mode on", true)]
    [InlineData("hide pet", false)]
    [InlineData("show the pet", true)]
    public void Parses_pet_commands(string text, bool on) => Assert.Equal(on, AmbientCommands.ParsePet(text));

    [Theory]
    [InlineData("pet food delivery")]
    [InlineData("pet shop near me")]
    [InlineData("pet")]
    public void Leaves_pet_searches_alone(string text) => Assert.Null(AmbientCommands.ParsePet(text));
}

public class TickerTests
{
    [Theory]
    [InlineData("RELIANCE.NS", "RELIANCE")]
    [InlineData("tcs.bo", "TCS")]
    [InlineData("^NSEI", "NIFTY")]
    [InlineData("^XYZ", "XYZ")]
    [InlineData("AAPL", "AAPL")]
    public void Labels_symbols(string symbol, string label) => Assert.Equal(label, TickerFormat.SymbolLabel(symbol));

    [Fact]
    public void Formats_quotes()
    {
        Assert.Equal("RELIANCE \u20B92,941 \u25B21.2%", TickerFormat.Quote(new StockQuote("RELIANCE.NS", 2941.2, 1.21, "INR", false)));
        Assert.Equal("NIFTY 23,441 \u25BC0.4%", TickerFormat.Quote(new StockQuote("^NSEI", 23440.8, -0.36, "INR", true)));
        Assert.Equal("AAPL $340.36 0.0%", TickerFormat.Quote(new StockQuote("AAPL", 340.356, 0.01, "USD", false)));
        Assert.Equal("X SGD 12.30", TickerFormat.Quote(new StockQuote("X", 12.3, null, "SGD", false)));
    }

    [Fact]
    public void Parses_yahoo_chart()
    {
        const string json = """
            {"chart":{"result":[{"meta":{"currency":"INR","symbol":"RELIANCE.NS","instrumentType":"EQUITY","regularMarketPrice":1300.0,
            "regularMarketChangePercent":11.598,"chartPreviousClose":1196.2},"timestamp":[1,2],"indicators":{"quote":[{"close":[1164.9,1300.0]}]}}],"error":null}}
            """;
        var q = YahooChart.Parse(json)!;
        Assert.Equal("RELIANCE.NS", q.Symbol);
        Assert.Equal(1300.0, q.Price);
        Assert.Equal(11.598, q.ChangePercent);
        Assert.False(q.IsIndex);
    }

    [Fact]
    public void Derives_change_from_closes_when_missing()
    {
        const string json = """
            {"chart":{"result":[{"meta":{"currency":"INR","symbol":"^NSEI","instrumentType":"INDEX","regularMarketPrice":110},
            "indicators":{"quote":[{"close":[90,null,100,110]}]}}]}}
            """;
        var q = YahooChart.Parse(json)!;
        Assert.True(q.IsIndex);
        Assert.Equal(10, q.ChangePercent!.Value, 3);
    }

    [Theory]
    [InlineData("""{"chart":{"result":null,"error":{"code":"Not Found","description":"No data found, symbol may be delisted"}}}""")]
    [InlineData("""{"finance":{"error":{"code":"Unauthorized"}}}""")]
    [InlineData("Too Many Requests")]
    public void Rejects_bad_charts(string json) => Assert.Null(YahooChart.Parse(json));

    [Fact]
    public void Validates_symbols()
    {
        Assert.True(YahooChart.IsValidSymbol("RELIANCE.NS"));
        Assert.True(YahooChart.IsValidSymbol("^NSEI"));
        Assert.True(YahooChart.IsValidSymbol("BTC-USD"));
        Assert.False(YahooChart.IsValidSymbol("../x"));
        Assert.False(YahooChart.IsValidSymbol(""));
        Assert.EndsWith("/chart/%5ENSEI?interval=1d&range=5d", YahooChart.Url("^NSEI"));
    }

    [Fact]
    public void Parses_coingecko()
    {
        var items = CoinGecko.Parse("""{"bitcoin":{"inr":7328023,"inr_24h_change":-1.0003},"ethereum":{"inr":214821.7}}""");
        Assert.Equal(2, items.Count);
        Assert.Equal("BTC \u20B97,328,023 \u25BC1.0%", items[0].Text);
        Assert.Equal("ETH \u20B9214,822", items[1].Text);
        Assert.Empty(CoinGecko.Parse("""{"status":{"error_code":429}}"""));
        Assert.Empty(CoinGecko.Parse("<html>"));
    }

    const string Feed = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <rss version="2.0"><channel><title>Cricinfo Live Scores</title>
        <item><title>New Zealand A 424/10  v India A 32 &amp;  346/10 *</title></item>
        <item><title>Tripura 219/7 * v Saurashtra</title></item>
        <item><title>Toronto Kings v Montreal Warriors</title></item>
        <item><title>India 287/4 * v Australia</title></item>
        <item><title>Netherlands 183/3  v Oman 182/10 </title></item>
        <item><title>Chhattisgarh 279/7 * v Hyderabad (India)</title></item>
        <item><title>no versus here</title></item>
        </channel></rss>
        """;

    [Fact]
    public void Parses_cricinfo_feed()
    {
        var matches = CricketFeed.Parse(Feed);
        Assert.Equal(6, matches.Count);
        var first = matches[0];
        Assert.Equal("New Zealand A", first.Home.Team);
        Assert.Equal("424/10", first.Home.Score);
        Assert.Equal("India A", first.Away.Team);
        Assert.Equal("32 & 346/10", first.Away.Score);
        Assert.True(first.Away.Batting);
        Assert.True(first.Live);
        Assert.Null(matches[1].Away.Score);
        Assert.False(matches[4].Live);
    }

    [Fact]
    public void Formats_matches_batting_side_first()
    {
        var m = CricketFeed.ParseTitle("New Zealand A 424/10  v India A 32 &amp;  346/10 *")!;
        Assert.Equal("IND A 32 v NZ A 424/10", CricketFeed.Format(m));
        Assert.Equal("Chhattisgarh 279/7 v Hyderabad", CricketFeed.Format(CricketFeed.ParseTitle("Chhattisgarh 279/7 * v Hyderabad (India)")!));
        Assert.Equal("IND W", CricketFeed.ShortTeam("India Women"));
    }

    [Theory]
    [InlineData("India A 346/10 &amp;  32 *", "32")]
    [InlineData("India A 32 &amp;  346/10 *", "32")]
    [InlineData("Victoria 136/6 &amp; 307/10 *", "136/6")]
    [InlineData("India 250/7d &amp; 12/1 *", "12/1")]
    [InlineData("Rajasthan 78/10 &amp; 300/10", "300/10")]
    public void Picks_the_innings_in_progress(string side, string expected)
    {
        var m = CricketFeed.ParseTitle("Somebody 100/10 v " + side)!;
        Assert.Equal(expected, CricketFeed.CurrentInnings(m.Away));
    }

    [Fact]
    public void Team_names_may_contain_ampersands()
    {
        var m = CricketFeed.ParseTitle("Jammu &amp; Kashmir v Puducherry 261/6 *")!;
        Assert.Equal("Jammu & Kashmir", m.Home.Team);
        Assert.Null(m.Home.Score);
        Assert.Equal("Puducherry 261/6 v Jammu & Kashmir", CricketFeed.Format(m));
    }

    [Fact]
    public void Orders_internationals_first_and_skips_unstarted()
    {
        var items = CricketFeed.Items(CricketFeed.Parse(Feed));
        Assert.Equal("IND 287/4 v AUS", items[0].Text);
        Assert.True(items[0].Live);
        Assert.Equal("NED 183/3 v OMA 182/10", items[1].Text);
        Assert.Equal("IND A 32 v NZ A 424/10", items[2].Text);
        Assert.DoesNotContain(items, i => i.Text.Contains("Toronto"));
        Assert.All(items, i => Assert.Equal(TickerKind.Cricket, i.Kind));
    }

    [Fact]
    public void Filters_by_team()
    {
        var items = CricketFeed.Items(CricketFeed.Parse(Feed), "india");
        Assert.Equal(3, items.Count); // India, India A, and Hyderabad (India)
        Assert.Empty(CricketFeed.Items(CricketFeed.Parse(Feed), "pakistan"));
        Assert.Empty(CricketFeed.Parse("<html><body>blocked</body>"));
    }
}

public class RefreshScheduleTests
{
    [Fact]
    public void Ticker_intervals()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), RefreshSchedule.Ticker(TickerKind.Cricket, anyLive: true));
        Assert.Equal(TimeSpan.FromMinutes(5), RefreshSchedule.Ticker(TickerKind.Cricket, anyLive: false));
        Assert.Equal(TimeSpan.FromSeconds(60), RefreshSchedule.Ticker(TickerKind.Stocks, false));
    }

    [Fact]
    public void Backs_off_but_never_below_normal()
    {
        var normal = TimeSpan.FromSeconds(60);
        Assert.Equal(normal, RefreshSchedule.AfterFailure(0, normal));
        Assert.Equal(normal, RefreshSchedule.AfterFailure(1, normal));
        Assert.Equal(TimeSpan.FromMinutes(2), RefreshSchedule.AfterFailure(3, normal));
        Assert.Equal(TimeSpan.FromMinutes(20), RefreshSchedule.AfterFailure(50, normal));
    }

    [Fact]
    public void Weather_retries_sooner_after_failure()
    {
        Assert.Equal(TimeSpan.FromMinutes(20), RefreshSchedule.WeatherAfterFailure(0));
        Assert.Equal(TimeSpan.FromSeconds(30), RefreshSchedule.WeatherAfterFailure(1));
        Assert.Equal(TimeSpan.FromMinutes(20), RefreshSchedule.WeatherAfterFailure(9));
    }
}
