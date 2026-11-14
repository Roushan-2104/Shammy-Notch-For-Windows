using Shammy.Core.Conversions;

namespace Shammy.Core.Tests;

public class NumberTextTests
{
    [Theory]
    [InlineData("1,299", 1299)]
    [InlineData("1,00,000", 100000)]
    [InlineData("1,234,567.5", 1234567.5)]
    [InlineData("2.5", 2.5)]
    [InlineData(".5", 0.5)]
    [InlineData("15,50", 15.5)]
    [InlineData("1.299,99", 1299.99)]
    [InlineData("1.000.000", 1000000)]
    [InlineData("1/2", 0.5)]
    [InlineData("1 1/2", 1.5)]
    [InlineData("-40", -40)]
    [InlineData("five", 5)]
    [InlineData("an", 1)]
    [InlineData("twenty five", 25)]
    [InlineData("two hundred", 200)]
    public void Parses(string text, double expected)
    {
        Assert.True(NumberText.TryParse(text, out var v));
        Assert.Equal(expected, v, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1,2,3")]
    [InlineData("1/0")]
    [InlineData("12ab")]
    [InlineData("five six")]
    public void Rejects(string text) => Assert.False(NumberText.TryParse(text, out _));

    [Theory]
    [InlineData(152.4, "152.4")]
    [InlineData(1000, "1,000")]
    [InlineData(6.2137119, "6.214")]
    [InlineData(37.77777, "37.78")]
    [InlineData(0.0352739, "0.03527")]
    [InlineData(-40, "-40")]
    [InlineData(0, "0")]
    public void Formats(double value, string expected) => Assert.Equal(expected, NumberText.Format(value));
}

public class UnitTests
{
    [Theory]
    [InlineData("5 ft in cm", 152.4, "cm")]
    [InlineData("convert 70 kg to pounds", 154.32358, "lb")]
    [InlineData("100 f to c", 37.77778, "°C")]
    [InlineData("100°F in °C", 37.77778, "°C")]
    [InlineData("100 degrees fahrenheit to celsius", 37.77778, "°C")]
    [InlineData("-40 c to f", -40, "°F")]
    [InlineData("300 k to c", 26.85, "°C")]
    [InlineData("3 cups in ml", 709.76471, "mL")]
    [InlineData("10 km to miles", 6.2137119, "mi")]
    [InlineData("1 gb in mb", 1000, "MB")]
    [InlineData("1 gib in mib", 1024, "MiB")]
    [InlineData("60 mph to km/h", 96.56064, "km/h")]
    [InlineData("1 acre in sq ft", 43560, "ft²")]
    [InlineData("2 hours in minutes", 120, "min")]
    [InlineData("1 week in days", 7, "day")]
    [InlineData("5 in in cm", 12.7, "cm")]
    [InlineData("5 ft 10 in in cm", 177.8, "cm")]
    [InlineData("5'10\" in cm", 177.8, "cm")]
    [InlineData("1 lb 4 oz in grams", 566.99, "g")]
    [InlineData("12 oz in ml", 354.88, "mL")]
    [InlineData("1 1/2 cups in ml", 354.88, "mL")]
    [InlineData("five feet in centimeters", 152.4, "cm")]
    [InlineData("What is 2.5 kg in lbs?", 5.51156, "lb")]
    [InlineData("how many cm in an inch", 2.54, "cm")]
    [InlineData("how many cups in a gallon", 16, "cup")]
    [InlineData("how many ounces in a cup", 8, "fl oz")]
    [InlineData("feet to meters", 0.3048, "m")]
    [InlineData("kg to lbs", 2.20462, "lb")]
    [InlineData("1 pound in kg", 0.45359, "kg")]
    [InlineData("100 sq m to sq ft", 1076.39, "ft²")]
    [InlineData("1 nautical mile in km", 1.852, "km")]
    public void Converts_phrases(string phrase, double expected, string unit)
    {
        Assert.True(Units.TryParseQuery(phrase, out var c), phrase);
        Assert.Equal(expected, c.Value, 2);
        Assert.Equal(unit, c.To.Symbol);
    }

    [Theory]
    [InlineData("5 ft in cm", "152.4 cm", "5 ft")]
    [InlineData("100 f to c", "37.78°C", "100°F")]
    [InlineData("1 gb in mb", "1,000 MB", "1 GB")]
    [InlineData("3 cups in ml", "709.76 mL", "3 cups")]
    [InlineData("178 cm in feet and inches", "5 ft 10.1 in", "178 cm")]
    [InlineData("180 cm in ft", "5.906 ft", "180 cm · 5 ft 10.9 in")]
    public void Titles_show_value_with_unit(string phrase, string title, string detail)
    {
        Assert.True(Units.TryParseQuery(phrase, out var c));
        Assert.Equal(title, c.Title);
        Assert.Equal(detail, c.Detail);
    }

    [Theory]
    [InlineData("how many cm in an inch history")]
    [InlineData("time in a bottle song")]
    [InlineData("best usd stocks")]
    [InlineData("1 in 3 people")]
    [InlineData("top 10 in india")]
    [InlineData("30 seconds to mars")]
    [InlineData("7 days to die")]
    [InlineData("100 days in paris")]
    [InlineData("2 states in india")]
    [InlineData("5 ways to save money")]
    [InlineData("convert pdf to word")]
    [InlineData("month to month")]
    [InlineData("day to day")]
    [InlineData("c to f")]
    [InlineData("1/2 cup in grams")]
    [InlineData("5 kg")]
    [InlineData("5 kg in")]
    [InlineData("5 kg to celsius")]
    [InlineData("remind me in 5 min to call mom")]
    [InlineData("timer 10 minutes")]
    [InlineData("")]
    public void Ignores_ordinary_searches(string phrase) => Assert.False(Units.TryParseQuery(phrase, out _), phrase);

    [Theory]
    [InlineData("5 ft 10 in", "177.8 cm", "5 ft 10 in")]
    [InlineData("5′10″", "177.8 cm", "5 ft 10 in")]
    [InlineData("350°F", "176.67°C", "350°F")]
    [InlineData("350 ºF", "176.67°C", "350°F")]
    [InlineData("2.5 kg", "5.512 lb", "2.5 kg")]
    [InlineData("12 oz", "340.19 g", "12 oz")]
    [InlineData("60 mph", "96.56 km/h", "60 mph")]
    [InlineData("100 km/h", "62.14 mph", "100 km/h")]
    [InlineData("500 ml", "16.91 fl oz", "500 mL")]
    [InlineData("2 m", "6.562 ft", "2 m · 6 ft 6.7 in")]
    [InlineData("1,200 sq ft", "111.48 m²", "1,200 ft²")]
    [InlineData("Weight: 2.5 kg", "5.512 lb", "2.5 kg")]
    public void Reads_circled_measurements(string text, string title, string detail)
    {
        Assert.True(Units.TryReadMeasurement(text, out var c), text);
        Assert.Equal(title, c.Title);
        Assert.Equal(detail, c.Detail);
    }

    [Theory]
    [InlineData("100K")]
    [InlineData("1M views")]
    [InlineData("1M")]
    [InlineData("2T")]
    [InlineData("5 min")]
    [InlineData("12 GB")]
    [InlineData("2023")]
    [InlineData("Buy 2 get 1 free")]
    [InlineData("the box weighs 2 kg and is red")]
    [InlineData("$49.99")]
    [InlineData("")]
    [InlineData("10 in")]
    [InlineData("1 in 3 people")]
    [InlineData("3 in 1 printer")]
    public void Ignores_circles_that_arent_measurements(string text) => Assert.False(Units.TryReadMeasurement(text, out _), text);

    [Fact]
    public void Imperial_and_metric_swap_sides()
    {
        Assert.Equal(Units.Centimetre, Units.Counterpart(Units.Inch, 3));
        Assert.Equal(Units.Inch, Units.Counterpart(Units.Centimetre, 30));
        Assert.Equal(Units.Fahrenheit, Units.Counterpart(Units.Celsius, 20));
        Assert.Equal(Units.Kilogram, Units.Counterpart(Units.Pound, 1));
        Assert.Null(Units.Counterpart(Units.Gigabyte, 1));
    }

    [Fact]
    public void Feet_and_inches_round_cleanly()
    {
        Assert.Equal("6 ft 0 in", Units.FeetAndInches(1.8288));
        Assert.Equal("11.8 in", Units.FeetAndInches(0.3));
    }
}

public class CurrencyTests
{
    static readonly Dictionary<string, double> Rates = new()
    {
        ["USD"] = 1, ["INR"] = 94.91, ["EUR"] = 0.89158, ["GBP"] = 0.74519, ["JPY"] = 157.9,
    };

    [Theory]
    [InlineData("100 usd to inr", 100, "USD", "INR")]
    [InlineData("50 euro in rupees", 50, "EUR", "INR")]
    [InlineData("₹500 in dollars", 500, "INR", "USD")]
    [InlineData("convert 20 gbp to inr", 20, "GBP", "INR")]
    [InlineData("$49.99 to inr", 49.99, "USD", "INR")]
    [InlineData("Rs. 1,299 in usd", 1299, "INR", "USD")]
    [InlineData("1.5 lakh rupees to usd", 150000, "INR", "USD")]
    [InlineData("2 crore inr to usd", 20000000, "INR", "USD")]
    [InlineData("10k yen to inr", 10000, "JPY", "INR")]
    [InlineData("100 US dollars to Indian rupees", 100, "USD", "INR")]
    [InlineData("50 pounds in rupees", 50, "GBP", "INR")]
    [InlineData("100 krw to usd", 100, "KRW", "USD")]
    [InlineData("dollar to rupee", 1, "USD", "INR")]
    [InlineData("usd to inr", 1, "USD", "INR")]
    [InlineData("how much is 15€ in inr", 15, "EUR", "INR")]
    public void Parses_queries(string phrase, double amount, string from, string to)
    {
        Assert.True(Currency.TryParseQuery(phrase, out var q), phrase);
        Assert.Equal(amount, q.From.Amount, 6);
        Assert.Equal(from, q.From.Code);
        Assert.Equal(to, q.To);
    }

    [Theory]
    [InlineData("best usd stocks")]
    [InlineData("usd to inr rate")]
    [InlineData("usd")]
    [InlineData("100 usd")]
    [InlineData("dollars to donuts")]
    [InlineData("50 pounds in kg")]
    [InlineData("usd to usd")]
    [InlineData("how many cm in an inch history")]
    [InlineData("time in a bottle song")]
    [InlineData("100 aed to inr")]
    [InlineData("")]
    public void Ignores_ordinary_searches(string phrase) => Assert.False(Currency.TryParseQuery(phrase, out _), phrase);

    [Theory]
    [InlineData("$49.99", 49.99, "USD")]
    [InlineData("€15", 15, "EUR")]
    [InlineData("₹1,299", 1299, "INR")]
    [InlineData("₹ 1,00,000", 100000, "INR")]
    [InlineData("Rs. 1,299/-", 1299, "INR")]
    [InlineData("MRP ₹999 only", 999, "INR")]
    [InlineData("Price: $49.99", 49.99, "USD")]
    [InlineData("USD 20", 20, "USD")]
    [InlineData("15,50 €", 15.5, "EUR")]
    [InlineData("£ 7.50", 7.5, "GBP")]
    [InlineData("(¥10,000)", 10000, "JPY")]
    public void Reads_circled_prices(string text, double amount, string code)
    {
        Assert.True(Currency.TryReadPrice(text, out var m), text);
        Assert.Equal(amount, m.Amount, 6);
        Assert.Equal(code, m.Code);
    }

    [Theory]
    [InlineData("49.99")]
    [InlineData("USD")]
    [InlineData("$49.99 $59.99")]
    [InlineData("Save $10 on your first order today")]
    [InlineData("5 ft 10 in")]
    [InlineData("$0")]
    [InlineData("")]
    public void Ignores_circles_that_arent_prices(string text) => Assert.False(Currency.TryReadPrice(text, out _), text);

    [Fact]
    public void Converts_through_the_base_currency()
    {
        Assert.True(Currency.TryConvert(100, "USD", "INR", Rates, out var inr));
        Assert.Equal(9491, inr, 6);
        Assert.True(Currency.TryConvert(50, "EUR", "INR", Rates, out var fromEur));
        Assert.Equal(50 / 0.89158 * 94.91, fromEur, 6);
        Assert.True(Currency.TryConvert(500, "INR", "USD", Rates, out var usd));
        Assert.Equal(500 / 94.91, usd, 9);
        Assert.False(Currency.TryConvert(1, "USD", "AED", Rates, out _));
    }

    [Fact]
    public void Default_target_is_rupees_unless_already_rupees()
    {
        Assert.Equal("INR", Currency.DefaultTarget("USD"));
        Assert.Equal("INR", Currency.DefaultTarget("EUR"));
        Assert.Equal("USD", Currency.DefaultTarget("INR"));
    }

    [Theory]
    [InlineData(474550.25, "INR", "₹4,74,550.25")]
    [InlineData(1000000, "INR", "₹10,00,000")]
    [InlineData(9491, "INR", "₹9,491")]
    [InlineData(999, "INR", "₹999")]
    [InlineData(49.99, "USD", "$49.99")]
    [InlineData(50, "USD", "$50")]
    [InlineData(1234.5, "EUR", "€1,234.50")]
    [InlineData(7895.4, "JPY", "¥7,895")]
    [InlineData(12.5, "AUD", "12.50 AUD")]
    [InlineData(0.010537, "USD", "$0.0105")]
    public void Formats(double amount, string code, string expected) => Assert.Equal(expected, Currency.Format(amount, code));

    [Fact]
    public void Reads_frankfurter_rates()
    {
        const string json = """{"amount":1.0,"base":"USD","date":"2026-11-02","rates":{"EUR":0.89158,"INR":94.91}}""";
        Assert.True(Currency.TryParseRates(json, out var table));
        Assert.Equal("USD", table.Base);
        Assert.Equal("2026-11-02", table.Date);
        Assert.Equal(1, table.Rates["USD"]);
        Assert.Equal(94.91, table.Rates["INR"]);
        Assert.False(Currency.TryParseRates("<html>offline</html>", out _));
        Assert.False(Currency.TryParseRates("""{"base":"USD","rates":{}}""", out _));
    }

    [Fact]
    public void Every_supported_code_has_an_alias() =>
        Assert.All(Currency.Supported, code => Assert.True(Currency.TryFindCode(code.ToLowerInvariant(), out var c) && c == code));
}

public class WorldClockTests
{
    static readonly TimeZoneInfo India = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
    static readonly DateTime November = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc); // US is back on standard time
    static readonly DateTime July = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Every_zone_exists_on_windows() =>
        Assert.All(WorldClock.KnownZones.Values, z => Assert.NotNull(z.Info));

    [Theory]
    [InlineData("time in tokyo", "Tokyo")]
    [InlineData("what time is it in london", "London")]
    [InlineData("What's the time in New York?", "New York")]
    [InlineData("current time in dubai", "Dubai")]
    [InlineData("tokyo time", "Tokyo")]
    [InlineData("time in IST", "IST")]
    [InlineData("time in nyc", "NYC")]
    public void Parses_clock_questions(string phrase, string label)
    {
        Assert.True(WorldClock.TryParse(phrase, out var q), phrase);
        Assert.Equal(label, Assert.IsType<ClockQuery>(q).Zone.Label);
    }

    [Fact]
    public void Clock_shows_local_time_there()
    {
        Assert.True(WorldClock.TryParse("time in tokyo", out var q));
        var a = WorldClock.Answer(q, November, India);
        Assert.Equal("9:00 PM", a.Title);
        Assert.Equal("Tokyo · Mon 2 Nov · UTC+9 · 3h 30m ahead", a.Detail);
    }

    [Fact]
    public void Clock_follows_daylight_saving()
    {
        Assert.True(WorldClock.TryParse("time in london", out var q));
        Assert.Equal("1:00 PM", WorldClock.Answer(q, July, India).Title);
        Assert.Equal("12:00 PM", WorldClock.Answer(q, November, India).Title);
    }

    [Theory]
    [InlineData("5pm ist in pst", "3:30 AM PST", "5:00 PM IST")]
    [InlineData("3:30 pm est to ist", "2:00 AM IST", "3:30 PM EST · next day in IST")]
    [InlineData("17:00 utc to ist", "10:30 PM IST", "5:00 PM UTC")]
    [InlineData("noon pst to ist", "1:30 AM IST", "12:00 PM PST · next day in IST")]
    [InlineData("9 am london time in tokyo", "6:00 PM Tokyo", "9:00 AM London")]
    [InlineData("10:00 p.m. ist to est", "11:30 AM EST", "10:00 PM IST")]
    [InlineData("5pm in london", "11:30 AM London", "5:00 PM your time")]
    public void Converts_times(string phrase, string title, string detail)
    {
        Assert.True(WorldClock.TryParse(phrase, out var q), phrase);
        var a = WorldClock.Answer(q, November, India);
        Assert.Equal(title, a.Title);
        Assert.Equal(detail, a.Detail);
    }

    [Fact]
    public void Abbreviations_follow_daylight_saving_too()
    {
        Assert.True(WorldClock.TryParse("5pm ist in pst", out var q));
        Assert.Equal("4:30 AM PST", WorldClock.Answer(q, July, India).Title);
    }

    [Theory]
    [InlineData("time in a bottle song")]
    [InlineData("time management")]
    [InlineData("best time to visit tokyo")]
    [InlineData("5 in tokyo")]
    [InlineData("top 10 in india")]
    [InlineData("5 ft in cm")]
    [InlineData("tokyo")]
    [InlineData("screen time")]
    [InlineData("25:00 ist to pst")]
    [InlineData("13pm ist to pst")]
    [InlineData("how many cm in an inch history")]
    [InlineData("best usd stocks")]
    [InlineData("")]
    public void Ignores_ordinary_searches(string phrase) => Assert.False(WorldClock.TryParse(phrase, out _), phrase);
}

public class TranslationTests
{
    [Theory]
    [InlineData("translate good morning to hindi", "good morning", "hi")]
    [InlineData("translate where is the station into Spanish", "where is the station", "es")]
    [InlineData("Translate how to cook rice to Hindi", "how to cook rice", "hi")]
    [InlineData("translate to french: thank you", "thank you", "fr")]
    [InlineData("translate \"I love you\" in japanese", "I love you", "ja")]
    [InlineData("How do you say thank you in Japanese?", "thank you", "ja")]
    [InlineData("translate नमस्ते to english", "नमस्ते", "en")]
    public void Parses_requests(string phrase, string text, string code)
    {
        Assert.True(Translation.TryParse(phrase, out var r), phrase);
        Assert.Equal(text, r.Text);
        Assert.Equal(code, r.LanguageCode);
    }

    [Theory]
    [InlineData("translate")]
    [InlineData("translate to english")]
    [InlineData("translate app")]
    [InlineData("google translate to english")]
    [InlineData("translator jobs in delhi")]
    [InlineData("translate this to klingon")]
    [InlineData("time in a bottle song")]
    public void Ignores_other_text(string phrase) => Assert.False(Translation.TryParse(phrase, out _), phrase);

    [Theory]
    [InlineData("good morning", true)]
    [InlineData("Where is the railway station?", true)]
    [InlineData("नमस्ते दुनिया", false)]
    [InlineData("hola amigo como estas", false)]
    [InlineData("bonjour", false)]
    [InlineData("", false)]
    public void Guesses_english(string text, bool english) => Assert.Equal(english, Translation.LooksEnglish(text));

    [Fact]
    public void Default_target_flips_between_english_and_hindi()
    {
        Assert.Equal("hi", Translation.DefaultTarget("good morning"));
        Assert.Equal("en", Translation.DefaultTarget("नमस्ते"));
    }

    [Fact]
    public void Builds_google_translate_link()
    {
        var url = Translation.BuildUrl("good morning & more", "hi").AbsoluteUri;
        Assert.StartsWith("https://translate.google.com/?sl=auto&tl=hi&text=", url);
        Assert.Contains("good%20morning%20%26%20more", url);
        Assert.EndsWith("&op=translate", url);
        Assert.Equal("Hindi", Translation.LanguageName("hi"));
    }
}

public class QrPayloadTests
{
    [Theory]
    [InlineData("https://example.com/a?b=1", "example.com")]
    [InlineData("http://www.shammy.test/path", "shammy.test")]
    [InlineData("www.example.com", "example.com")]
    public void Links_are_urls(string raw, string host)
    {
        var p = QrPayload.Parse(raw);
        Assert.Equal(QrKind.Url, p.Kind);
        Assert.Equal(host, p.Host);
    }

    [Fact]
    public void Wifi_codes_give_network_and_password()
    {
        var p = QrPayload.Parse(@"WIFI:T:WPA;S:Home Net;P:pa\;ss\\word;H:false;;");
        Assert.Equal(QrKind.Wifi, p.Kind);
        Assert.Equal("Home Net", p.Ssid);
        Assert.Equal(@"pa;ss\word", p.Password);
    }

    [Fact]
    public void Open_wifi_has_no_password()
    {
        var p = QrPayload.Parse("WIFI:S:Cafe;T:nopass;P:;;");
        Assert.Equal(QrKind.Wifi, p.Kind);
        Assert.Equal("Cafe", p.Ssid);
        Assert.Null(p.Password);
    }

    [Theory]
    [InlineData("upi://pay?pa=shop@okbank&pn=Shop")]
    [InlineData("hello world")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://exa mple.com")]
    [InlineData("8901234567890")]
    [InlineData("WIFI:garbage")]
    public void Everything_else_is_text(string raw) => Assert.Equal(QrKind.Text, QrPayload.Parse(raw).Kind);
}
