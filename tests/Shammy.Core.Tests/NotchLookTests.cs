using Shammy.Core.Settings;

namespace Shammy.Core.Tests;

public class NotchLookTests
{
    static readonly string[] Two = { @"\\.\DISPLAY1", @"\\.\DISPLAY2" };

    [Fact]
    public void Normal_size_changes_nothing_and_the_others_stay_modest()
    {
        Assert.Equal(1, NotchLook.Scale(NotchSize.Normal));
        Assert.InRange(NotchLook.Scale(NotchSize.Small), 0.8, 0.95);
        Assert.InRange(NotchLook.Scale(NotchSize.Large), 1.05, 1.2);
    }

    [Fact]
    public void Every_accent_has_its_own_colour_and_orange_is_the_designs_own()
    {
        Assert.Equal("#FF8A3D", NotchLook.Colours(AccentColor.Orange).Accent);
        var all = Enum.GetValues<AccentColor>().Select(c => NotchLook.Colours(c).Accent).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("MAIN")]
    public void Nothing_chosen_means_the_main_screen(string? setting) =>
        Assert.Equal(Two[0], NotchDisplay.Pick(setting, Two, Two[0], Two[1]));

    [Fact]
    public void Follow_pointer_goes_where_the_pointer_is() =>
        Assert.Equal(Two[1], NotchDisplay.Pick("pointer", Two, Two[0], Two[1]));

    [Fact]
    public void Follow_pointer_with_no_known_pointer_screen_stays_on_main() =>
        Assert.Equal(Two[0], NotchDisplay.Pick("pointer", Two, Two[0], null));

    [Fact]
    public void A_named_screen_is_used_while_connected_and_main_when_unplugged()
    {
        Assert.Equal(Two[1], NotchDisplay.Pick(Two[1].ToLowerInvariant(), Two, Two[0], Two[0]));
        Assert.Equal(Two[0], NotchDisplay.Pick(Two[1], new[] { Two[0] }, Two[0], Two[0]));
    }

    [Fact]
    public void One_screen_offers_only_the_main_screen() =>
        Assert.Equal(new[] { ("main", "Main screen") }, NotchDisplay.Options(new[] { Two[0] }, Two[0]));

    [Fact]
    public void Two_screens_offer_main_pointer_and_the_other_by_its_windows_number() =>
        Assert.Equal(new[] { ("main", "Main screen"), ("pointer", "Follow pointer"), (Two[1], "Screen 2") }, NotchDisplay.Options(Two, Two[0]));

    [Theory]
    [InlineData(@"C:\Program Files\Adobe\Photoshop.EXE", "photoshop")]
    [InlineData("  VALORANT.exe ", "valorant")]
    [InlineData("code", "code")]
    [InlineData("", "")]
    public void Apps_are_remembered_by_their_plain_name(string app, string key) =>
        Assert.Equal(key, HideRules.Key(app));

    [Fact]
    public void The_notch_hides_only_for_listed_apps()
    {
        var apps = new[] { "photoshop", "valorant" };
        Assert.True(HideRules.Hides("Photoshop.exe", apps));
        Assert.False(HideRules.Hides("chrome.exe", apps));
        Assert.False(HideRules.Hides("", apps));
        Assert.False(HideRules.Hides("photoshop", null));
    }

    [Fact]
    public void The_list_is_kept_tidy() =>
        Assert.Equal(new[] { "photoshop", "valorant" }, HideRules.Clean(new[] { "Photoshop.exe", " photoshop ", "", "VALORANT" }));
}
