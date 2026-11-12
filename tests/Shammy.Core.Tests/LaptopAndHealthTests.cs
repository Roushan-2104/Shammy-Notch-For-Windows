using Shammy.Core.Quick;

namespace Shammy.Core.Tests;

public class HealthPhraseTests
{
    [Theory]
    [InlineData("health")]
    [InlineData("Open health")]
    [InlineData("cleanup")]
    [InlineData("clean up my pc")]
    [InlineData("clean-up my pc")]
    [InlineData("storage")]
    [InlineData("disk space")]
    public void Opens_the_health_card(string text) => Assert.True(HealthPhrases.IsOpen(text));

    [Theory]
    [InlineData("health insurance")]
    [InlineData("storage box")]
    [InlineData("how to clean up my pc")]
    public void Leaves_searches_alone(string text) => Assert.False(HealthPhrases.IsOpen(text));
}

public class PaletteCardRowTests
{
    [Theory]
    [InlineData("health", "health")]
    [InlineData("disk space", "health")]
    [InlineData("laptop", "quick")]
    [InlineData("guide", "guide")]
    [InlineData("volume mixer", "mixer")]
    [InlineData("claude code", "agents")]
    public void Each_card_has_a_row(string typed, string id) =>
        Assert.Equal(id, Shammy.Core.Palette.PaletteSources.FindViews(typed)[0].View.Id);
}

public class HealthAttentionTests
{
    static readonly DriveSpace Roomy = new("C:", 120L << 30, 476L << 30);

    [Fact]
    public void A_long_uptime_alone_never_nags() =>
        Assert.False(HealthText.NeedsAttention(new HealthReport(Roomy, TimeSpan.FromDays(30), 5, null, false)));

    [Fact]
    public void Worn_battery_counts_only_while_it_is_news()
    {
        var r = new HealthReport(Roomy, TimeSpan.FromDays(1), 5, new BatteryWear(50000, 30000), false);
        Assert.True(HealthText.NeedsAttention(r, batteryNews: true));
        Assert.False(HealthText.NeedsAttention(r, batteryNews: false));
    }

    [Theory]
    [InlineData(80, null, false)]  // healthy
    [InlineData(65, null, true)]   // first time worn
    [InlineData(64, 65, false)]    // already told
    [InlineData(55, 65, true)]     // 10 points worse
    public void Battery_news(int percent, int? toldAt, bool news) => Assert.Equal(news, HealthText.BatteryNews(percent, toldAt));

    [Fact]
    public void Hidden_for_a_week()
    {
        var now = new DateTime(2026, 11, 12, 9, 0, 0);
        Assert.True(HealthText.Hidden(now, now + HealthText.HideFor));
        Assert.False(HealthText.Hidden(now.AddDays(8), now + HealthText.HideFor));
        Assert.False(HealthText.Hidden(now, null));
    }

    [Fact]
    public void Restart_offered_after_a_week() =>
        Assert.True(HealthText.LongUptime(TimeSpan.FromDays(7.5)) && !HealthText.LongUptime(TimeSpan.FromDays(3)));
}

public class CleanupTextTests
{
    [Theory]
    [InlineData(CleanupKind.OldDownloads, 2L << 30, 3, 0, "Moved 2.0 GB to the Recycle Bin")]
    [InlineData(CleanupKind.OldDownloads, 1L << 30, 3, 2, "Moved 1.0 GB to the Recycle Bin · 2 couldn’t move")]
    [InlineData(CleanupKind.OldDownloads, 0L, 0, 4, "Couldn’t move them")]
    [InlineData(CleanupKind.OldDownloads, 0L, 0, 1, "Couldn’t move it")]
    [InlineData(CleanupKind.OldDownloads, 0L, 0, 0, "Nothing to move")]
    [InlineData(CleanupKind.TempFiles, 5L << 20, 10, 3, "Freed 5 MB · 3 in use")]
    [InlineData(CleanupKind.TempFiles, 0L, 0, 3, "All in use; nothing removed")]
    [InlineData(CleanupKind.RecycleBin, 3L << 30, 40, 0, "Freed 3.0 GB")]
    [InlineData(CleanupKind.RecycleBin, 0L, 0, 1, "Couldn’t empty the Recycle Bin")]
    [InlineData(CleanupKind.RecycleBin, 0L, 0, 0, "Already empty")]
    public void Says_what_happened_honestly(CleanupKind kind, long bytes, int done, int failed, string said)
    {
        Assert.Equal(said, Cleanup.Said(kind, new CleanupOutcome(bytes, done, failed)));
        Assert.DoesNotContain("0 B", Cleanup.Said(kind, new CleanupOutcome(bytes, done, failed)));
    }

    [Fact]
    public void Downloads_count_items_and_say_when_they_arrived()
    {
        Assert.Equal("1 item", Cleanup.Count(CleanupKind.OldDownloads, 1));
        Assert.Equal("4 files", Cleanup.Count(CleanupKind.TempFiles, 4));
        Assert.StartsWith("Downloaded over 3 months ago", Cleanup.Explain(CleanupKind.OldDownloads));
    }

    [Fact]
    public void An_item_is_old_only_when_everything_in_it_is()
    {
        var cutoff = new DateTime(2026, 8, 1);
        var old = (new DateTime(2026, 1, 1), new DateTime(2026, 2, 1));
        // Copied in last week, written long ago: it arrived last week.
        var copiedIn = (new DateTime(2026, 11, 5), new DateTime(2025, 1, 1));
        Assert.True(Cleanup.IsOld(new[] { old }, cutoff));
        Assert.False(Cleanup.IsOld(new[] { copiedIn }, cutoff));
        Assert.False(Cleanup.IsOld(new[] { old, copiedIn }, cutoff));
        Assert.False(Cleanup.IsOld(Array.Empty<(DateTime, DateTime)>(), cutoff));
    }
}
