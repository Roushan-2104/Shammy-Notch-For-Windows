using Shammy.Core.Notch;

namespace Shammy.Core.Tests;

public class NotchLayoutTests
{
    [Fact]
    public void Design_row_fits_exactly()
    {
        // The mockup's row: music 340, calendar 260, timer 176, weather 176 with 8 px gaps = 976.
        var row = HomeRowLayout.Fit(new[]
        {
            new RowCard("weather", 176, 40, true),
            new RowCard("music", 340, 10, true),
            new RowCard("timer", 176, 30, true),
            new RowCard("calendar", 260, 20, true),
        });
        Assert.Equal(new[] { "music", "calendar", "timer", "weather" }, row);
    }

    [Fact]
    public void Irrelevant_cards_are_left_out()
    {
        var row = HomeRowLayout.Fit(new[]
        {
            new RowCard("music", 340, 10, false),
            new RowCard("calendar", 260, 20, true),
        });
        Assert.Equal(new[] { "calendar" }, row);
    }

    [Fact]
    public void Lowest_priority_card_that_does_not_fit_is_dropped()
    {
        var row = HomeRowLayout.Fit(new[]
        {
            new RowCard("music", 340, 10, true),
            new RowCard("calendar", 260, 20, true),
            new RowCard("timer", 176, 30, true),
            new RowCard("weather", 176, 40, true),
            new RowCard("battery", 176, 50, true),
        });
        Assert.Equal(new[] { "music", "calendar", "timer", "weather" }, row);
    }

    [Fact]
    public void A_smaller_later_card_fills_the_gap_a_bigger_one_could_not()
    {
        // 340 + 340 = 688 (+8); a third 340 needs 1036, but a 176 still fits (872).
        var row = HomeRowLayout.Fit(new[]
        {
            new RowCard("a", 340, 1, true),
            new RowCard("b", 340, 2, true),
            new RowCard("c", 340, 3, true),
            new RowCard("d", 176, 4, true),
        });
        Assert.Equal(new[] { "a", "b", "d" }, row);
    }

    [Fact]
    public void Equal_orders_keep_registration_order()
    {
        var row = HomeRowLayout.Fit(new[]
        {
            new RowCard("first", 176, 100, true),
            new RowCard("second", 176, 100, true),
        });
        Assert.Equal(new[] { "first", "second" }, row);
    }

    [Fact]
    public void Nothing_relevant_gives_an_empty_row()
    {
        Assert.Empty(HomeRowLayout.Fit(new[] { new RowCard("music", 340, 10, false) }));
        Assert.Empty(HomeRowLayout.Fit(Array.Empty<RowCard>()));
    }

    [Fact]
    public void Chips_follow_the_design_order_then_registration()
    {
        var chips = NotchChips.Build(
            new[]
            {
                new LegacyView("Shelf", "shelf"), new LegacyView("Alerts", "alerts"),
                new LegacyView("Weather", "tab:weather"), new LegacyView("Clipboard", "tab:clipboard"),
                new LegacyView("Snippets", "tab:snippets"), new LegacyView("Notes", "tab:notes"),
            },
            new[] { new ChipCard("mirror", "Mirror", true), new ChipCard("music", "Music", false) });
        Assert.Equal(new[] { "Clipboard", "Shelf", "Notes", "Alerts", "Mirror", "Snippets", "Weather" }, chips.Select(c => c.Label));
    }

    [Fact]
    public void Guide_Laptop_Health_and_Claude_Code_come_before_Shelf_Mirror_and_Snippets()
    {
        var chips = NotchChips.Build(
            new[] { new LegacyView("Shelf", "shelf"), new LegacyView("Snippets", "tab:snippets") },
            new[]
            {
                new ChipCard("mirror", "Mirror", true), new ChipCard("agents", "Claude Code", true), new ChipCard("health", "Health", true),
                new ChipCard("quick", "Laptop", true), new ChipCard("guide", "Guide", true),
            });
        Assert.Equal(new[] { "Guide", "Laptop", "Health", "Claude Code", "Shelf", "Mirror", "Snippets" }, chips.Select(c => c.Label));
    }

    [Fact]
    public void A_card_with_a_tabs_title_replaces_its_chip()
    {
        var chips = NotchChips.Build(
            new[] { new LegacyView("Clipboard", "tab:clipboard"), new LegacyView("Weather", "tab:weather") },
            new[] { new ChipCard("clipboard", "Clipboard", true), new ChipCard("weather", "weather", false) });
        Assert.Equal(new[] { new ChipSpec("Clipboard", "clipboard"), new ChipSpec("weather", "weather") }, chips);
    }

    [Fact]
    public void Cards_without_a_chip_flag_get_no_chip()
    {
        var chips = NotchChips.Build(Array.Empty<LegacyView>(), new[] { new ChipCard("timer", "Timer", false) });
        Assert.Empty(chips);
    }

    [Theory]
    [InlineData(@"C:\a\Report_final.docx", false, "Word")]
    [InlineData(@"C:\a\Screenshot 10.21.PNG", false, "PNG image")]
    [InlineData(@"C:\a\Invoice.pdf", false, "PDF")]
    [InlineData(@"C:\a\thing.xyz", false, "XYZ file")]
    [InlineData(@"C:\a\Makefile", false, "File")]
    [InlineData(@"C:\a\Photos", true, "Folder")]
    public void File_kinds_read_naturally(string path, bool folder, string expected) =>
        Assert.Equal(expected, FileKinds.Describe(path, folder));
}
