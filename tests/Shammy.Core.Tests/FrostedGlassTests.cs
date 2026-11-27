using Shammy.Core.Music;
using Shammy.Core.Notch;
using Xunit;

namespace Shammy.Core.Tests;

public class FrostedGlassTests
{
    static GlassConditions Fine() => new(SwitchOn: true, Available: true, OnAc: true, BatteryPercent: 80, EnergySaver: false, Transparency: true, Fullscreen: false);

    [Fact]
    public void Shows_when_everything_is_fine()
    {
        var d = FrostedGlassRules.Decide(Fine());
        Assert.True(d.Runs);
        Assert.Equal("On", d.Status);
    }

    [Fact]
    public void Off_switch_says_nothing()
    {
        var d = FrostedGlassRules.Decide(Fine() with { SwitchOn = false, EnergySaver = true });
        Assert.Equal(GlassState.Off, d.State);
        Assert.Equal("", d.Status);
    }

    [Theory]
    [InlineData(true, 10, GlassState.On)]
    [InlineData(false, 80, GlassState.On)]
    [InlineData(false, 50, GlassState.On)]
    [InlineData(false, 49, GlassState.Paused)]
    [InlineData(false, -1, GlassState.On)]
    public void Battery_above_half_or_plugged_in(bool onAc, int battery, GlassState expected) =>
        Assert.Equal(expected, FrostedGlassRules.Decide(Fine() with { OnAc = onAc, BatteryPercent = battery }).State);

    [Fact]
    public void Pauses_for_energy_saver_transparency_and_fullscreen()
    {
        Assert.Equal("Paused: Energy saver is on", FrostedGlassRules.Decide(Fine() with { EnergySaver = true }).Status);
        Assert.Equal("Paused: Transparency effects are off in Windows", FrostedGlassRules.Decide(Fine() with { Transparency = false }).Status);
        Assert.Equal("Paused: a full-screen app is in front", FrostedGlassRules.Decide(Fine() with { Fullscreen = true }).Status);
        Assert.Equal("Paused: high contrast is on", FrostedGlassRules.Decide(Fine() with { HighContrast = true }).Status);
    }

    [Fact]
    public void Unavailable_wins_over_pauses()
    {
        var d = FrostedGlassRules.Decide(Fine() with { Available = false, EnergySaver = true });
        Assert.Equal(GlassState.Unavailable, d.State);
        Assert.False(d.Runs);
    }

    [Fact]
    public void Moment_colour_most_urgent_wins()
    {
        var album = new Rgb(1, 2, 3);
        Assert.Null(FrostedGlassRules.Moment(false, false, RimState.None, album));
        Assert.Equal(album, FrostedGlassRules.Moment(false, false, RimState.Music, album));
        Assert.Equal(RimLight.Focus, FrostedGlassRules.Moment(false, false, RimState.Focus, album));
        Assert.Equal(FrostedGlassRules.Notify, FrostedGlassRules.Moment(false, true, RimState.Music, album));
        Assert.Equal(RimLight.MicLive, FrostedGlassRules.Moment(false, true, RimState.MicLive, album));
        Assert.Equal(FrostedGlassRules.Alert, FrostedGlassRules.Moment(true, true, RimState.MicLive, album));
    }

    [Fact]
    public void Tint_is_light_resting_and_dark_open()
    {
        Assert.Equal(FrostedGlassRules.LightTint, FrostedGlassRules.TintAlpha(FrostedGlassRules.Openness(41)));
        Assert.Equal(FrostedGlassRules.DarkTint, FrostedGlassRules.TintAlpha(FrostedGlassRules.Openness(297)));
        Assert.Equal(170, FrostedGlassRules.TintAlpha(FrostedGlassRules.Openness(90)));
    }

    [Fact]
    public void Wave_colours_are_the_moment_with_a_lighter_middle()
    {
        var c = FrostedGlassRules.WaveColours(new Rgb(0, 100, 255));
        Assert.Equal(new Rgb(0, 100, 255), c[0]);
        Assert.Equal(new Rgb(76, 146, 255), c[1]);
        Assert.Equal(c[0], c[2]);
    }

    [Theory]
    [InlineData(18363, false)]
    [InlineData(19041, true)]
    [InlineData(26300, true)]
    public void Needs_windows_2004_or_newer(int build, bool ok) => Assert.Equal(ok, FrostedGlassRules.WindowsBuildOk(build));
}

public class FrostWindowFitTests
{
    static readonly ScreenBox Island = new(800, 6, 1130, 47);

    [Fact]
    public void Starts_at_the_pieces() => Assert.Equal((Island, false), FrostWindowFit.Next(default, Island));

    [Fact]
    public void Grows_to_take_a_wing_and_holds_while_it_shrinks()
    {
        var withWing = new ScreenBox(800, 6, 1410, 114);
        var grown = FrostWindowFit.Next(Island, withWing);
        Assert.Equal(withWing, grown.Window);
        Assert.False(grown.ShrinkSoon);
        var folded = FrostWindowFit.Next(grown.Window, Island);
        Assert.Equal(withWing, folded.Window);
        Assert.True(folded.ShrinkSoon);
    }

    [Fact]
    public void A_move_to_another_screen_spans_both_then_shrinks_soon()
    {
        var elsewhere = new ScreenBox(2720, 6, 3050, 47);
        var next = FrostWindowFit.Next(Island, elsewhere);
        Assert.Equal(new ScreenBox(800, 6, 3050, 47), next.Window);
        Assert.True(next.ShrinkSoon);
    }
}
