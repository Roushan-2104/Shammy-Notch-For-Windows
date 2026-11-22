using Shammy.Core.Music;
using Shammy.Core.Notch;

namespace Shammy.Core.Tests;

public class ScreenTintTests
{
    [Fact]
    public void A_white_page_adds_no_colour_only_a_faint_dark_edge()
    {
        var t = ScreenTint.From(new Rgb(245, 245, 242));
        Assert.Equal(0, t.GlassAlpha);
        Assert.Equal(new Rgb(0, 0, 0), t.Rim);
        Assert.True(t.RimAlpha is > 0 and < 0.2);
    }

    [Theory]
    [InlineData(30, 30, 34)]     // dark grey
    [InlineData(30, 18, 14)]     // almost black, a little warm
    [InlineData(12, 12, 12)]
    public void A_dark_screen_with_no_real_colour_keeps_the_plain_glass(byte r, byte g, byte b) =>
        Assert.Equal(ScreenTint.None, ScreenTint.From(new Rgb(r, g, b)));

    [Fact]
    public void A_cream_page_counts_as_light_not_as_colour()
    {
        var t = ScreenTint.From(new Rgb(245, 235, 225));
        Assert.Equal(0, t.GlassAlpha);
        Assert.Equal(new Rgb(0, 0, 0), t.Rim);
    }

    [Fact]
    public void Just_enough_colour_starts_a_faint_tint()
    {
        // 30 of 255 apart (just under MinChroma) is grey; 40 apart is a faint tint.
        Assert.Equal(0, ScreenTint.From(new Rgb(100, 110, 130)).GlassAlpha);
        Assert.InRange(ScreenTint.From(new Rgb(90, 110, 130)).GlassAlpha, 0.1, 0.15);
    }

    [Fact]
    public void A_coloured_screen_tints_the_glass_dark_and_the_rim_light_in_its_hue()
    {
        var t = ScreenTint.From(new Rgb(184, 85, 31)); // an orange video
        Assert.True(t.GlassAlpha > 0.1);
        Assert.True(t.Glass.R > t.Glass.B);
        // The glass stays dark, the rim stays light: the island never turns into a light pill.
        Assert.True(t.Glass.R + t.Glass.G + t.Glass.B < 3 * 140);
        Assert.True(t.Rim.R + t.Rim.G + t.Rim.B > 3 * 150);
        Assert.True(t.HaloAlpha > 0);
    }

    [Fact]
    public void The_average_of_a_strip_is_its_colour()
    {
        byte[] bgra = { 10, 20, 30, 255, 30, 40, 50, 255 };
        Assert.Equal(new Rgb(40, 30, 20), ScreenTint.Average(bgra));
        Assert.Equal(default, ScreenTint.Average(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Fading_from_no_tint_keeps_the_new_hue_and_only_grows_its_strength()
    {
        var to = ScreenTint.From(new Rgb(63, 143, 95));
        var half = ScreenTint.None.Toward(to, 0.5);
        Assert.Equal(to.Glass, half.Glass);
        Assert.Equal(to.GlassAlpha / 2, half.GlassAlpha, 3);
        Assert.Equal(to, ScreenTint.None.Toward(to, 1));
        Assert.Equal(ScreenTint.None, ScreenTint.None.Toward(to, 0) with { Glass = default, Rim = default, Halo = default });
    }

    [Fact]
    public void Over_lays_the_tint_on_the_glass_colour()
    {
        var t = ScreenTint.From(new Rgb(107, 63, 160));
        var under = new Rgb(8, 8, 10);
        var over = t.Over(under);
        Assert.NotEqual(under, over);
        Assert.Equal(under, ScreenTint.None.Over(under));
    }

    [Theory]
    [InlineData(4, 3)]
    [InlineData(5, 3)]
    [InlineData(6, 2)]
    public void The_calendar_shows_as_many_plans_as_fit_under_its_weeks(int weeks, int plans)
    {
        Assert.Equal(plans, Shammy.Core.Calendar.CalendarWingFit.PlansThatFit(weeks));
        // 250 inside the slab: 39 + 21 a week for the month, 26 for the heading, 24 a plan.
        Assert.True(39 + 21 * weeks + 26 + 24 * plans <= 250);
    }
}