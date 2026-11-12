using Shammy.Core.Notch;
using Shammy.Core.Productivity;

namespace Shammy.Core.Tests;

/// <summary>The words and rules behind the designed alarm, settings and tour screens.</summary>
public class ScreensTests
{
    static readonly DateTime Wed2248 = new(2026, 11, 4, 22, 48, 0);

    [Fact]
    public void NextLineTomorrowMorning() =>
        Assert.Equal("Next · tomorrow 7:00 AM · in 8 h 12 m", AlarmText.NextLine(Wed2248, new DateTime(2026, 11, 5, 7, 0, 0), use24Hour: false));

    [Fact]
    public void NextLineToday() =>
        Assert.Equal("Next · today 23:30 · in 42 m", AlarmText.NextLine(Wed2248, new DateTime(2026, 11, 4, 23, 30, 0), use24Hour: true));

    [Fact]
    public void NextLineLaterThisWeekAndFurther()
    {
        Assert.Equal("Next · Saturday 9:30 AM · in 2 days", AlarmText.NextLine(Wed2248, new DateTime(2026, 11, 7, 9, 30, 0), use24Hour: false));
        Assert.StartsWith("Next · Thu 12 Nov 7:00 AM", AlarmText.NextLine(Wed2248, new DateTime(2026, 11, 12, 7, 0, 0), use24Hour: false));
    }

    [Theory]
    [InlineData(0, "now")]
    [InlineData(0.2, "in 1 m")]
    [InlineData(45, "in 45 m")]
    [InlineData(120, "in 2 h")]
    [InlineData(492, "in 8 h 12 m")]
    [InlineData(4 * 24 * 60, "in 4 days")]
    public void ShortUntil(double minutes, string expected) => Assert.Equal(expected, AlarmText.Short(TimeSpan.FromMinutes(minutes)));

    [Theory]
    [InlineData(6, "Morning")]
    [InlineData(13, "Afternoon")]
    [InlineData(19, "Evening")]
    [InlineData(23, "Night")]
    [InlineData(2, "Night")]
    public void Greeting(int hour, string expected) => Assert.Equal(expected, AlarmText.Greeting(new DateTime(2026, 11, 5, hour, 0, 0)));

    [Theory]
    [InlineData("24° Bhubaneswar", "24°")]
    [InlineData("-3° Leh", "-3°")]
    [InlineData("31°", "31°")]
    [InlineData("Rain later", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("Pune 31°", null)]
    public void TemperatureFromGlance(string? glance, string? expected) => Assert.Equal(expected, AlarmText.Temperature(glance));

    [Fact]
    public void RingDate() => Assert.Equal("Alarm · Thu 5 Nov", AlarmText.RingDate(new DateTime(2026, 11, 5, 7, 0, 0)));

    [Fact]
    public void WeekIsMondayFirstAndCoversEveryDay()
    {
        Assert.Equal(7, AlarmText.Week.Count);
        Assert.Equal(AlarmDays.Monday, AlarmText.Week[0].Day);
        Assert.Equal(AlarmDays.EveryDay, AlarmText.Week.Aggregate(AlarmDays.None, (all, d) => all | d.Day));
    }

    [Theory]
    [InlineData(null, "Shammy")]
    [InlineData("   ", "Shammy")]
    [InlineData("  Golu  ", "Golu")]
    [InlineData("Mr   Pixel", "Mr Pixel")]
    [InlineData("Shammyshammyshammy", "Shammyshammy")]
    public void PetNames(string? input, string expected) => Assert.Equal(expected, PetLooks.Name(input));

    [Fact]
    public void EyeColoursHaveTwoTones()
    {
        foreach (var c in Enum.GetValues<PetEyeColor>())
        {
            var (hex, shade) = PetLooks.Colors(c);
            Assert.Matches("^#[0-9A-F]{6}$", hex);
            Assert.Matches("^#[0-9A-F]{6}$", shade);
            Assert.NotEqual(hex, shade);
        }
        Assert.Equal(("#F5F5F7", "#A3A6B2"), PetLooks.Colors(PetEyeColor.White));
    }

    [Fact]
    public void RestyleKeepsRoundAndShapesOthers()
    {
        Assert.Equal((7.0, 11.0, 3.5), PetLooks.Restyle(PetEyeStyle.Round, 7, 11));
        var oval = PetLooks.Restyle(PetEyeStyle.Oval, 7, 11);
        Assert.True(oval.Width < 7 && oval.Height > 11);
        var pixel = PetLooks.Restyle(PetEyeStyle.Pixel, 7, 11);
        Assert.True(pixel.Height <= 7.7 && pixel.Radius < 2);
    }

    [Fact]
    public void TourHasFiveStepsWithTheName()
    {
        Assert.Equal(5, TourScript.Steps);
        Assert.Equal("Hi, I’m Golu!", TourScript.Step(1, "Golu").Title);
        Assert.Equal("Welcome · 1 of 5", TourScript.Step(1, "Golu").Kicker);
        Assert.Equal("Hover me for the rest", TourScript.Step(5, "Golu").Title);
        Assert.Equal(TourScript.Step(5, "x"), TourScript.Step(9, "x"));
        Assert.Contains("Golu", TourScript.Finished("Golu").Body);
        Assert.Contains("Golu", TourScript.Skipped("Golu").Body);
        for (int i = 1; i <= 5; i++) Assert.EndsWith($"{i} of 5", TourScript.Step(i, "Momo").Kicker);
    }

    [Theory]
    [InlineData("tour", true)]
    [InlineData("Help", false)] // the guide answers "help"
    [InlineData("  show the tour ", true)]
    [InlineData("help?", false)]
    [InlineData("help me write an email", false)]
    [InlineData("tourism in goa", false)]
    [InlineData("", false)]
    public void TourCommand(string text, bool expected) => Assert.Equal(expected, TourScript.IsTourCommand(text));
}
