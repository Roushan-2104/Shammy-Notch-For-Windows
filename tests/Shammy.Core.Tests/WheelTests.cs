using Shammy.Core.Wheel;

namespace Shammy.Core.Tests;

public class WheelGestureTests
{
    [Fact]
    public void A_quick_click_goes_back_to_the_app()
    {
        var g = new WheelGesture();
        Assert.Equal(WheelStep.Swallow, g.Down(100, 100, 0, enabled: true));
        Assert.Equal(WheelStep.Pass, g.Move(102, 99));
        Assert.Equal(WheelStep.SwallowAndReplayClick, g.Up(120));
        Assert.Equal(WheelGesture.Phase.Idle, g.Current);
    }

    [Fact]
    public void Moving_first_is_a_drag_the_app_gets()
    {
        var g = new WheelGesture();
        g.Down(100, 100, 0, true);
        Assert.Equal(WheelStep.ReplayDownThenPass, g.Move(120, 100));
        Assert.Equal(WheelStep.Pass, g.Move(140, 100));
        Assert.False(g.HoldElapsed(300));
        Assert.Equal(WheelStep.Pass, g.Up(400));
    }

    [Fact]
    public void Holding_still_opens_the_wheel_and_the_release_is_ours()
    {
        var g = new WheelGesture();
        g.Down(100, 100, 0, true);
        Assert.False(g.HoldElapsed(100));
        Assert.True(g.HoldElapsed(250));
        Assert.Equal(WheelGesture.Phase.Open, g.Current);
        Assert.Equal(WheelStep.Pass, g.Move(180, 40)); // moves always reach the screen
        Assert.Equal(WheelStep.SwallowAndRelease, g.Up(900));
    }

    [Fact]
    public void A_late_timer_still_gives_the_app_its_click() // the wheel never showed
    {
        var g = new WheelGesture();
        g.Down(5, 5, 0, true);
        Assert.Equal(WheelStep.SwallowAndReplayClick, g.Up(600));
    }

    [Fact]
    public void Switched_off_or_in_a_game_nothing_is_held_back()
    {
        var g = new WheelGesture();
        Assert.Equal(WheelStep.Pass, g.Down(1, 1, 0, enabled: false));
        Assert.False(g.HoldElapsed(1000));
        Assert.Equal(WheelStep.Pass, g.Up(1000));
    }

    [Fact]
    public void Reset_drops_an_open_wheel()
    {
        var g = new WheelGesture();
        g.Down(1, 1, 0, true);
        g.HoldElapsed(300);
        g.Reset();
        Assert.Equal(WheelStep.Pass, g.Up(400));
    }

    [Theory]
    [InlineData(0, -50, 0)]
    [InlineData(50, -50, 1)]
    [InlineData(50, 0, 2)]
    [InlineData(40, 40, 3)]
    [InlineData(0, 60, 4)]
    [InlineData(-40, 40, 5)]
    [InlineData(-60, 0, 6)]
    [InlineData(-40, -40, 7)]
    [InlineData(5, -10, -1)]
    [InlineData(-8, -60, 0)]
    public void Points_pick_beads_clockwise_from_the_top(double dx, double dy, int bead) => Assert.Equal(bead, WheelGeometry.Pick(dx, dy));
}

public class WheelSlotsTests
{
    static readonly DateTime Now = new(2026, 11, 11, 12, 0, 0);
    static IReadOnlyDictionary<string, IReadOnlyList<DateTime>> Uses(params (string Id, int Count, int DaysAgo)[] uses) =>
        uses.GroupBy(u => u.Id).ToDictionary(g => g.Key, g => (IReadOnlyList<DateTime>)g.SelectMany(u => Enumerable.Repeat(Now.AddDays(-u.DaysAgo), u.Count)).ToList());

    [Fact]
    public void Starts_with_the_eight_drawn() =>
        Assert.Equal(WheelSlots.Defaults, WheelSlots.Arrange(null, Uses(), Now).Select(s => s.ActionId));

    [Fact]
    public void Something_used_a_lot_takes_the_weakest_unpinned_bead()
    {
        var slots = WheelSlots.Arrange(null, Uses(("captions", 6, 0), ("screenshot", 3, 0), ("record", 2, 0), ("timer", 2, 0), ("music", 4, 0), ("notes", 1, 0), ("ask", 2, 0), ("phone", 1, 0)), Now);
        Assert.Contains(slots, s => s.ActionId == "captions");
        Assert.DoesNotContain(slots, s => s.ActionId == "awake"); // never used: it made room
        Assert.Equal("screenshot", slots[0].ActionId); // the rest stay where they were
    }

    [Fact]
    public void A_little_use_doesnt_shuffle_the_wheel()
    {
        var slots = WheelSlots.Arrange(null, Uses(("captions", 1, 0), ("awake", 1, 0)), Now);
        Assert.Equal(WheelSlots.Defaults, slots.Select(s => s.ActionId));
    }

    [Fact]
    public void Pinned_beads_never_move()
    {
        var saved = WheelSlots.Defaults.Select((id, i) => new WheelSlot(id, Pinned: true)).ToList();
        var slots = WheelSlots.Arrange(saved, Uses(("captions", 50, 0)), Now);
        Assert.Equal(WheelSlots.Defaults, slots.Select(s => s.ActionId));
        Assert.All(slots, s => Assert.True(s.Pinned));
    }

    [Fact]
    public void Old_use_fades()
    {
        Assert.Equal(0.5, WheelSlots.Weight(Now.AddDays(-14), Now), 3);
        var slots = WheelSlots.Arrange(null, Uses(("captions", 6, 60)), Now); // two months ago: worth ~0.3
        Assert.DoesNotContain(slots, s => s.ActionId == "captions");
    }

    [Fact]
    public void A_damaged_save_is_repaired()
    {
        var saved = new[] { new WheelSlot("screenshot", true), new WheelSlot("screenshot", false), new WheelSlot("nonsense", false) };
        var slots = WheelSlots.Arrange(saved, Uses(), Now);
        Assert.Equal(8, slots.Count);
        Assert.Equal(8, slots.Select(s => s.ActionId).Distinct().Count());
        Assert.True(slots[0].Pinned);
    }

    [Theory]
    [InlineData("timer 10 minutes", "timer")]
    [InlineData("screenshot", "screenshot")]
    [InlineData("send this to my phone", "phone")]
    [InlineData("turn on captions", "captions")]
    [InlineData("play kesariya", "music")]
    [InlineData("keep my pc awake", "awake")]
    [InlineData("how tall is the eiffel tower", null)]
    public void Commands_count_for_their_action(string text, string? id) => Assert.Equal(id, WheelSlots.ActionFor(text));
}

public class ActivityExpiryTests
{
    [Fact]
    public void The_next_expiry_is_the_soonest_one_in_utc()
    {
        var m = new Shammy.Core.Activities.ActivityManager();
        Assert.Null(m.NextExpiryUtc);
        var soon = DateTime.UtcNow.AddSeconds(3);
        m.Post(new Shammy.Core.Activities.Activity("a", Shammy.Core.Activities.ActivityKind.Custom, 1, ExpiresAt: DateTime.UtcNow.AddSeconds(10)));
        m.Post(new Shammy.Core.Activities.Activity("b", Shammy.Core.Activities.ActivityKind.Custom, 1, ExpiresAt: soon.ToLocalTime()));
        m.Post(new Shammy.Core.Activities.Activity("c", Shammy.Core.Activities.ActivityKind.Custom, 1));
        Assert.Equal(soon, m.NextExpiryUtc!.Value, TimeSpan.FromMilliseconds(1));
        m.Remove("b");
        Assert.True(m.NextExpiryUtc > soon);
    }
}
