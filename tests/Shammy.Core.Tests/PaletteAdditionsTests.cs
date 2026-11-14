using Shammy.Core.Ambient;
using Shammy.Core.Conversions;
using Shammy.Core.Levels;
using Shammy.Core.Productivity;
using Shammy.Core.Search;

namespace Shammy.Core.Tests;

public class LevelPhraseTests
{
    [Theory]
    [InlineData("volume 50", LevelTarget.Volume, 50)]
    [InlineData("Volume 50%.", LevelTarget.Volume, 50)]
    [InlineData("set volume to 30", LevelTarget.Volume, 30)]
    [InlineData("volume 30 percent", LevelTarget.Volume, 30)]
    [InlineData("set the volume at 0", LevelTarget.Volume, 0)]
    [InlineData("volume fifty", LevelTarget.Volume, 50)]
    [InlineData("brightness 70", LevelTarget.Brightness, 70)]
    [InlineData("brightness to 60%", LevelTarget.Brightness, 60)]
    [InlineData("set screen brightness to 45 per cent", LevelTarget.Brightness, 45)]
    [InlineData("volume max", LevelTarget.Volume, 100)]
    [InlineData("brightness minimum", LevelTarget.Brightness, 0)]
    [InlineData("volume 40 kar do", LevelTarget.Volume, 40)]
    [InlineData("brightness 80 pe kar do", LevelTarget.Brightness, 80)]
    public void Sets_a_level(string text, LevelTarget target, int percent)
    {
        Assert.Equal(new LevelCommand(target, LevelAction.Set, percent), LevelPhrase.Parse(text));
    }

    [Fact]
    public void Clamps_and_says_so()
    {
        Assert.Equal(new LevelCommand(LevelTarget.Volume, LevelAction.Set, 100, Clamped: true), LevelPhrase.Parse("volume 500"));
        Assert.Equal(new LevelCommand(LevelTarget.Brightness, LevelAction.Set, 100, Clamped: true), LevelPhrase.Parse("brightness 150%"));
        Assert.Equal(51, LevelPhrase.Parse("volume 50.5")!.Percent);
    }

    [Theory]
    [InlineData("volume up", LevelTarget.Volume, LevelAction.Up)]
    [InlineData("turn the volume down", LevelTarget.Volume, LevelAction.Down)]
    [InlineData("increase brightness", LevelTarget.Brightness, LevelAction.Up)]
    [InlineData("lower the volume", LevelTarget.Volume, LevelAction.Down)]
    [InlineData("turn up the volume", LevelTarget.Volume, LevelAction.Up)]
    [InlineData("volume badha do", LevelTarget.Volume, LevelAction.Up)]
    [InlineData("awaaz kam karo", LevelTarget.Volume, LevelAction.Down)]
    [InlineData("mute", LevelTarget.Volume, LevelAction.Mute)]
    [InlineData("Mute the sound", LevelTarget.Volume, LevelAction.Mute)]
    [InlineData("unmute", LevelTarget.Volume, LevelAction.Unmute)]
    [InlineData("volume off", LevelTarget.Volume, LevelAction.Mute)]
    public void Steps_and_mutes(string text, LevelTarget target, LevelAction action)
    {
        var c = LevelPhrase.Parse(text);
        Assert.Equal(target, c?.Target);
        Assert.Equal(action, c?.Action);
    }

    [Theory]
    [InlineData("volume of a sphere")]
    [InlineData("volume of cylinder formula")]
    [InlineData("mute mic")]
    [InlineData("unmute my microphone")]
    [InlineData("brightness")]
    [InlineData("volume")]
    [InlineData("volume control app")]
    [InlineData("volume 50 ml to cups")]
    [InlineData("sound of music")]
    [InlineData("volume a")]
    [InlineData("how to increase brightness on laptop")]
    [InlineData("")]
    public void Leaves_other_text_alone(string text) => Assert.Null(LevelPhrase.Parse(text));

    [Fact]
    public void Applies_steps_within_bounds()
    {
        Assert.Equal(60, LevelPhrase.Apply(new LevelCommand(LevelTarget.Volume, LevelAction.Up), 50));
        Assert.Equal(100, LevelPhrase.Apply(new LevelCommand(LevelTarget.Volume, LevelAction.Up), 95));
        Assert.Equal(0, LevelPhrase.Apply(new LevelCommand(LevelTarget.Brightness, LevelAction.Down), 4));
        Assert.Equal("Set volume to 50%", LevelPhrase.Describe(LevelPhrase.Parse("volume 50")!));
    }
}

public class LakhCroreTests
{
    [Theory]
    [InlineData("5 lakh", 500000)]
    [InlineData("2.5 crore", 25000000)]
    [InlineData("10 lakh + 5 lakh", 1500000)]
    [InlineData("3 lacs", 300000)]
    [InlineData("1 cr - 50 lakhs", 5000000)]
    [InlineData("2 Crore * 2", 40000000)]
    [InlineData("what is 1.5 lakh / 12", 12500)]
    public void Calculator_reads_lakh_and_crore(string text, double expected)
    {
        Assert.True(Calculator.TryEvaluate(text, out var value, out _), text);
        Assert.Equal(expected, value, 6);
    }

    [Theory]
    [InlineData("5 lakh rupees")]
    [InlineData("lakh")]
    [InlineData("crore se zyada")]
    public void Calculator_leaves_phrases_alone(string text) => Assert.False(Calculator.TryEvaluate(text, out _, out _));

    [Theory]
    [InlineData("1.2 lakh in dollars", 120000, "INR", "USD")]
    [InlineData("₹3 crore to usd", 30000000, "INR", "USD")]
    [InlineData("5 lakh to euro", 500000, "INR", "EUR")]
    [InlineData("2 cr in usd", 20000000, "INR", "USD")]
    public void Currency_reads_lakh_and_crore_as_rupees(string text, double amount, string from, string to)
    {
        Assert.True(Currency.TryParseQuery(text, out var q), text);
        Assert.Equal(amount, q.From.Amount, 6);
        Assert.Equal(from, q.From.Code);
        Assert.Equal(to, q.To);
    }

    [Fact]
    public void Plain_numbers_still_need_a_currency() => Assert.False(Currency.TryParseQuery("5 thousand in dollars", out _));
}

public class DayAfterTomorrowTests
{
    // Monday 2 Nov 2026, 10:00.
    static readonly DateTime Now = new(2026, 11, 2, 10, 0, 0);

    [Theory]
    [InlineData("day after tomorrow", 4, 9)]
    [InlineData("the day after tomorrow at 5pm", 4, 17)]
    [InlineData("parso", 4, 9)]
    [InlineData("parso at 5pm", 4, 17)]
    [InlineData("parso morning", 4, 9)]
    public void Two_days_ahead(string text, int day, int hour) =>
        Assert.Equal(new DateTime(2026, 11, day, hour, 0, 0), WhenParser.Parse(text, Now));

    [Fact]
    public void Crosses_the_month_end()
    {
        var late = new DateTime(2026, 1, 30, 22, 0, 0);
        Assert.Equal(new DateTime(2026, 2, 1, 9, 0, 0), WhenParser.Parse("day after tomorrow at 9", late));
        Assert.Equal(new DateTime(2027, 1, 1, 9, 0, 0), WhenParser.Parse("parso", new DateTime(2026, 12, 30, 23, 0, 0)));
    }

    [Fact]
    public void Keeps_the_wall_clock_across_dst()
    {
        // Europe/US clocks change on these weekends; the reminder is still "9 AM on the day", not 8 or 10.
        Assert.Equal(new DateTime(2026, 3, 30, 9, 0, 0), WhenParser.Parse("day after tomorrow at 9am", new DateTime(2026, 3, 28, 12, 0, 0)));
        Assert.Equal(new DateTime(2026, 11, 2, 9, 0, 0), WhenParser.Parse("parso at 9am", new DateTime(2026, 10, 31, 12, 0, 0)));
    }

    [Theory]
    [InlineData("tomorrow day after tomorrow")]
    [InlineData("parso tomorrow")]
    [InlineData("day after")]
    public void Rejects_conflicts(string text) => Assert.Null(WhenParser.Parse(text, Now));

    [Fact]
    public void Reminders_in_english()
    {
        var c = ProductivityParser.Parse("remind me parso at 5pm to pay rent", Now);
        Assert.Equal(ProductivityAction.Remind, c!.Action);
        Assert.Equal(new DateTime(2026, 11, 4, 17, 0, 0), c.Due);
        Assert.Equal("Pay rent", c.Text);
        Assert.Equal(new DateTime(2026, 11, 4, 9, 0, 0), ProductivityParser.Parse("remind me day after tomorrow to call mom", Now)!.Due);
    }

    [Fact]
    public void Hinglish_reminder_lands_two_days_ahead()
    {
        var c = ProductivityParser.Parse("parso subah 9 baje yaad dilana", Now);
        Assert.Equal(new DateTime(2026, 11, 4, 9, 0, 0), c!.Due);
    }
}

public class CityWeatherTests
{
    [Theory]
    [InlineData("weather in mumbai", "Mumbai", WeatherQuery.Now)]
    [InlineData("Weather in Mumbai?", "Mumbai", WeatherQuery.Now)]
    [InlineData("mumbai weather", "Mumbai", WeatherQuery.Now)]
    [InlineData("weather in delhi tomorrow", "Delhi", WeatherQuery.Tomorrow)]
    [InlineData("what's the weather like in new york", "New York", WeatherQuery.Now)]
    [InlineData("what's the weather in tokyo", "Tokyo", WeatherQuery.Now)]
    [InlineData("weather in london", "London", WeatherQuery.Now)]
    [InlineData("how's the weather in san francisco today", "San Francisco", WeatherQuery.Now)]
    [InlineData("tomorrow's weather in pune", "Pune", WeatherQuery.Tomorrow)]
    [InlineData("weather tomorrow in goa", "Goa", WeatherQuery.Tomorrow)]
    [InlineData("bangalore weather tomorrow", "Bangalore", WeatherQuery.Tomorrow)]
    [InlineData("temperature in chennai", "Chennai", WeatherQuery.Now)]
    [InlineData("will it rain in seattle tomorrow", "Seattle", WeatherQuery.Tomorrow)]
    [InlineData("pune ka mausam", "Pune", WeatherQuery.Now)]
    [InlineData("delhi ka kal ka mausam", "Delhi", WeatherQuery.Tomorrow)]
    [InlineData("kal delhi ka mausam kaisa rahega", "Delhi", WeatherQuery.Tomorrow)]
    [InlineData("mumbai mein mausam kaisa hai", "Mumbai", WeatherQuery.Now)]
    public void Finds_the_place(string text, string city, WeatherQuery when)
    {
        Assert.Equal(new CityWeatherQuery(city, when), CityWeather.Parse(text));
    }

    [Fact]
    public void Reads_devanagari()
    {
        var q = CityWeather.Parse("पुणे का मौसम");
        Assert.NotNull(q);
        Assert.Equal(WeatherQuery.Now, q!.When);
    }

    [Theory]
    [InlineData("weather")]
    [InlineData("weather tomorrow")]
    [InlineData("what's the weather")]
    [InlineData("tomorrow's weather")]
    [InlineData("weather app download")]
    [InlineData("best weather app")]
    [InlineData("weather channel")]
    [InlineData("weathering with you")]
    [InlineData("the weather girls it's raining men")]
    [InlineData("how does weather forecasting work")]
    [InlineData("weather forecast in paris history")]
    [InlineData("whether in mumbai")]
    [InlineData("nice weather")]
    [InlineData("cold weather tips")]
    [InlineData("cold weather")]
    [InlineData("bad weather")]
    [InlineData("weather in 2024")]
    [InlineData("weather app for windows")]
    [InlineData("room temperature")]
    [InlineData("body temperature")]
    [InlineData("aaj ka mausam")]
    [InlineData("weather in my area")]
    [InlineData("weather near me")]
    [InlineData("weather in the united states of america and canada")]
    [InlineData("")]
    public void Leaves_searches_alone(string text) => Assert.Null(CityWeather.Parse(text));

    [Fact]
    public void Old_names_look_up_the_new_ones()
    {
        Assert.Equal("Bengaluru", CityWeather.LookupName("Bangalore"));
        Assert.Equal("Gurugram", CityWeather.LookupName("gurgaon"));
        Assert.Equal("Pune", CityWeather.LookupName("Pune"));
        Assert.Equal(CityWeather.Key("Bangalore"), CityWeather.Key("bengaluru"));
    }

    [Fact]
    public void Geocoding_prefers_the_biggest_town()
    {
        const string json = """
            {"results":[
              {"name":"Delhi","latitude":42.27,"longitude":-74.91,"feature_code":"PPL","population":3157,"country":"United States"},
              {"name":"Delhi","latitude":28.65,"longitude":77.23,"feature_code":"PPLA","population":11034555,"country":"India"},
              {"name":"Delhi Airport","latitude":28.55,"longitude":77.1,"feature_code":"AIRP","country":"India"}]}
            """;
        var place = OpenMeteoGeocoding.Parse(json);
        Assert.Equal(new Place("Delhi", "India", 28.65, 77.23), place);
        Assert.Equal("Delhi, India", place!.Label);
    }

    [Fact]
    public void Geocoding_without_results_is_unknown()
    {
        Assert.Null(OpenMeteoGeocoding.Parse("""{"generationtime_ms":0.13}"""));
        Assert.Null(OpenMeteoGeocoding.Parse("not json"));
        Assert.Equal("Kerala", OpenMeteoGeocoding.Parse("""{"results":[{"name":"Kerala","latitude":10,"longitude":76,"feature_code":"ADM1","country":"India"}]}""")!.Name);
        Assert.Contains("name=New%20York", OpenMeteoGeocoding.SearchUrl("New York"));
    }
}
