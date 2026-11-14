using Shammy.Core.Notch;
using Shammy.Core.Wheel;

namespace Shammy.Core.Tests;

public class EssentialsTests
{
    static readonly DateTime Now = new(2026, 11, 14, 20, 0, 0);

    static IReadOnlyList<Essential> Catalog() => Essentials.Catalog(new[]
    {
        ("timer", "Timer"), ("notes", "Notes"), ("music", "Music"), ("circles", "Circles"), ("alarms", "Alarms"), ("weather", "Weather"),
    });

    static Dictionary<string, IReadOnlyList<DateTime>> Uses(params (string Id, int Count, int DaysAgo)[] uses) =>
        uses.ToDictionary(u => u.Id, u => (IReadOnlyList<DateTime>)Enumerable.Repeat(Now.AddDays(-u.DaysAgo), u.Count).ToList());

    [Fact]
    public void Every_feature_appears_once()
    {
        var all = Catalog();
        Assert.Equal(all.Count, all.Select(e => e.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(all, e => e.Id == "circles" && e.IsCard);
        Assert.Contains(all, e => e.Id == "screenshot" && !e.IsCard);
    }

    [Fact]
    public void A_card_wins_over_the_action_with_the_same_id()
    {
        var timer = Assert.Single(Catalog(), e => e.Id == "timer");
        Assert.True(timer.IsCard);
        var music = Assert.Single(Catalog(), e => e.Id == "music");
        Assert.True(music.IsCard);
    }

    [Fact]
    public void Media_keys_and_repeats_are_left_out()
    {
        var ids = Catalog().Select(e => e.Id).ToList();
        foreach (var skipped in new[] { "playpause", "next", "previous", "stage", "musiccard", "share" }) Assert.DoesNotContain(skipped, ids);
        Assert.Contains("prompter", ids);
    }

    [Fact]
    public void Most_used_come_first_and_the_rest_is_a_to_z()
    {
        var (most, rest) = Essentials.Arrange(Catalog(), Uses(("circles", 9, 0), ("weather", 4, 1), ("mirror", 2, 0)), Now);
        Assert.Equal(new[] { "circles", "weather", "mirror" }, most.Take(3).Select(e => e.Id));
        Assert.Equal(Essentials.TopCount, most.Count);
        Assert.Equal(rest.Select(e => e.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase), rest.Select(e => e.Name));
        Assert.Empty(most.Intersect(rest));
        Assert.Equal(Catalog().Count, most.Count + rest.Count);
    }

    [Fact]
    public void Old_uses_fade()
    {
        // Many uses two months ago lose to a few this week (the wheel's two-week half-life).
        var (most, _) = Essentials.Arrange(Catalog(), Uses(("alarms", 10, 60), ("notes", 3, 0)), Now);
        Assert.True(most.ToList().FindIndex(e => e.Id == "notes") < most.ToList().FindIndex(e => e.Id == "alarms"));
    }

    [Fact]
    public void With_no_history_the_wheel_defaults_fill_the_top()
    {
        var (most, _) = Essentials.Arrange(Catalog(), new Dictionary<string, IReadOnlyList<DateTime>>(), Now);
        Assert.Equal(WheelSlots.Defaults.Take(most.Count), most.Select(e => e.Id));
    }

    [Fact]
    public void A_swipe_moves_one_page_however_many_deltas_it_sends()
    {
        var swipe = new PageSwipe();
        int moves = 0;
        for (int i = 0; i < 20; i++) moves += Math.Abs(swipe.Add(40, Now.AddMilliseconds(i * 16)));
        Assert.Equal(1, moves);
    }

    [Fact]
    public void After_a_rest_the_next_swipe_moves_again_and_direction_counts()
    {
        var swipe = new PageSwipe();
        Assert.Equal(1, swipe.Add(130, Now));
        Assert.Equal(0, swipe.Add(-200, Now.AddMilliseconds(100)));
        Assert.Equal(-1, swipe.Add(-130, Now.AddMilliseconds(600)));
    }

    [Fact]
    public void A_small_nudge_does_nothing() => Assert.Equal(0, new PageSwipe().Add(60, Now));
}
