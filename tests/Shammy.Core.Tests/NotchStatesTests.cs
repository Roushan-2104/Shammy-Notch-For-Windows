using System.Globalization;
using Shammy.Core.Music;
using Shammy.Core.Notch;

namespace Shammy.Core.Tests;

public class HoverIntentTests
{
    static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>Feeds samples every 16 ms along a straight line at the given speed (DIPs/ms); returns every decision with its time.</summary>
    static List<(HoverDecision D, double T)> Run(HoverIntent h, double fromMs, double toMs, double x0, double speed, double y = 10)
    {
        var decisions = new List<(HoverDecision, double)>();
        for (double t = fromMs; t <= toMs; t += 16)
        {
            var d = h.Sample(x0 + (t - fromMs) * speed, y, Ms(t));
            if (d != HoverDecision.None) decisions.Add((d, t));
        }
        return decisions;
    }

    [Fact]
    public void A_fast_pass_through_never_opens_anything()
    {
        var h = new HoverIntent();
        // 1 DIP/ms (a quick move to a browser tab) for a full second across the notch.
        Assert.Empty(Run(h, 0, 1000, 0, 1.0));
        Assert.Equal(HoverStage.Resting, h.Stage);
    }

    [Fact]
    public void Resting_shows_the_peek_then_opens()
    {
        var h = new HoverIntent();
        var d = Run(h, 0, 1200, 100, 0);
        Assert.Equal(HoverDecision.Peek, d[0].D);
        Assert.InRange(d[0].T, 250, 290);
        Assert.Equal(HoverDecision.Open, d[1].D);
        Assert.InRange(d[1].T, d[0].T + 700, d[0].T + 740);
        Assert.Equal(2, d.Count);
    }

    [Fact]
    public void Slowing_down_then_stopping_counts_from_the_stop()
    {
        var h = new HoverIntent();
        Assert.Empty(Run(h, 0, 200, 0, 1.2));
        var d = Run(h, 216, 700, 240, 0);
        Assert.Equal(HoverDecision.Peek, d[0].D);
        // The speed window has to forget the fast samples first, then 250 ms of rest.
        Assert.InRange(d[0].T, 216 + 250, 216 + 250 + HoverIntent.SpeedWindow.TotalMilliseconds + 20);
    }

    [Fact]
    public void Slow_drift_still_counts_as_resting()
    {
        var h = new HoverIntent();
        var d = Run(h, 0, 400, 100, 0.1);
        Assert.Equal(HoverDecision.Peek, Assert.Single(d).D);
    }

    [Fact]
    public void Moving_on_the_peek_restarts_the_wait_to_open()
    {
        var h = new HoverIntent();
        Assert.Equal(HoverDecision.Peek, Run(h, 0, 300, 100, 0).Single().D);
        Assert.Empty(Run(h, 316, 800, 100, 0)); // 484 ms of rest
        Assert.Empty(Run(h, 816, 900, 100, 1.0)); // a quick move towards the peek's button
        var d = Run(h, 916, 1800, 184, 0);
        Assert.Equal(HoverDecision.Open, d.Single().D);
        Assert.True(d[0].T >= 916 + 700);
    }

    [Fact]
    public void Without_a_peek_it_opens_after_the_whole_wait()
    {
        var h = new HoverIntent { AllowPeek = false };
        var d = Run(h, 0, 1200, 50, 0);
        var only = Assert.Single(d);
        Assert.Equal(HoverDecision.Open, only.D);
        Assert.InRange(only.T, 950, 990);
    }

    [Fact]
    public void Reset_starts_over()
    {
        var h = new HoverIntent();
        Run(h, 0, 300, 0, 0);
        Assert.Equal(HoverStage.Peek, h.Stage);
        h.Reset();
        Assert.Equal(HoverStage.Resting, h.Stage);
        Assert.Equal(HoverDecision.Peek, Run(h, 1000, 1300, 0, 0).Single().D);
    }
}

public class GlanceTests
{
    sealed record Item(string Id, int Priority);

    [Fact]
    public void Orders_by_priority_keeping_ties_in_order_and_drops_nulls()
    {
        var items = new Item?[] { new("time", 0), null, new("weather", 10), new("calendar", 60), new("notes", 10) };
        var ordered = GlanceOrder.ByPriority(items, i => i.Priority).Select(i => i.Id);
        Assert.Equal(new[] { "calendar", "weather", "notes", "time" }, ordered);
    }

    [Fact]
    public void Rotation_cycles_and_follows_items_that_come_and_go()
    {
        var r = new GlanceRotator();
        var ids = new[] { "calendar", "weather", "time" };
        Assert.Equal(0, r.Advance(ids));
        Assert.Equal(1, r.Advance(ids));
        Assert.Equal(2, r.Advance(ids));
        Assert.Equal(0, r.Advance(ids));
        Assert.Equal("calendar", r.CurrentId);

        // Battery went low between rotations: it slots in by priority, and the rotation carries on after calendar.
        Assert.Equal(2, r.Advance(new[] { "battery", "calendar", "weather", "time" }));
        Assert.Equal("weather", r.CurrentId);

        // Weather vanished: start again from the top.
        Assert.Equal(0, r.Advance(new[] { "battery", "time" }));
        Assert.Equal(-1, r.Advance(Array.Empty<string>()));
        Assert.Null(r.CurrentId);
    }

    [Fact]
    public void Locate_finds_the_current_item_without_moving_on()
    {
        var r = new GlanceRotator();
        r.Advance(new[] { "a", "b" });
        r.Advance(new[] { "a", "b" });
        Assert.Equal(0, r.Locate(new[] { "b", "a" }));
        Assert.Equal("b", r.CurrentId);
    }

    [Fact]
    public void Time_glance_reads_like_the_mockup()
    {
        var t = new DateTime(2026, 11, 3, 10, 20, 0);
        Assert.Equal(("10:20", "Tue 3"), BuiltInGlances.Time(t, twentyFourHour: false));
        Assert.Equal(("22:05", "Tue 3"), BuiltInGlances.Time(t.AddHours(11).AddMinutes(45), twentyFourHour: true));
        Assert.Equal(("10:05", "Tue 3"), BuiltInGlances.Time(t.AddHours(11).AddMinutes(45), twentyFourHour: false));
        Assert.True(BuiltInGlances.Uses24Hour(CultureInfo.GetCultureInfo("de-DE")));
        Assert.False(BuiltInGlances.Uses24Hour(CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void Battery_shows_only_when_low_or_charging()
    {
        Assert.Null(BuiltInGlances.Battery(true, 81, false));
        Assert.Equal("18% battery", BuiltInGlances.Battery(true, 18, false));
        Assert.Equal("20% battery", BuiltInGlances.Battery(true, 20, false));
        Assert.Null(BuiltInGlances.Battery(true, 28, false));
        Assert.Equal("Charging 64%", BuiltInGlances.Battery(true, 64, true));
        Assert.Null(BuiltInGlances.Battery(false, 10, false));
    }
}

public class EyeTests
{
    [Theory]
    [InlineData(22, 59, false)]
    [InlineData(23, 0, true)]
    [InlineData(2, 30, true)]
    [InlineData(5, 59, true)]
    [InlineData(6, 0, false)]
    [InlineData(14, 0, false)]
    public void Sleepy_from_eleven_at_night_to_six_in_the_morning(int hour, int minute, bool sleepy)
    {
        var t = new DateTime(2026, 11, 3, hour, minute, 0);
        Assert.Equal(sleepy, EyeLogic.IsSleepy(t));
    }

    [Fact]
    public void Follows_a_nearby_pointer_only()
    {
        Assert.Null(EyeLogic.Follow(400, 0));
        Assert.Null(EyeLogic.Follow(250, 250));
        Assert.Equal(0, EyeLogic.Follow(0, 50));
        Assert.Equal(EyeLogic.MaxLook, EyeLogic.Follow(290, 10));
        Assert.Equal(-EyeLogic.MaxLook, EyeLogic.Follow(-200, 0));
        var small = EyeLogic.Follow(28, 0)!.Value;
        Assert.InRange(small, 0.1, 1);
    }

    [Fact]
    public void Blinks_about_every_five_seconds_but_never_on_a_beat()
    {
        var r = new Random(7);
        var gaps = Enumerable.Range(0, 200).Select(_ => EyeLogic.NextBlink(r, sleepy: false).TotalSeconds).ToList();
        Assert.All(gaps, g => Assert.InRange(g, 3.5, 6.5));
        Assert.InRange(gaps.Average(), 4.5, 5.5);
        Assert.True(gaps.Distinct().Count() > 150);
        Assert.All(Enumerable.Range(0, 50), _ => Assert.InRange(EyeLogic.NextBlink(r, sleepy: true).TotalSeconds, 7, 11));
    }
}

public class GestureDebounceTests
{
    [Fact]
    public void A_quick_sideways_flick_is_a_swipe()
    {
        Assert.Equal(1, DragSwipe.Classify(80, 6, TimeSpan.FromMilliseconds(180)));
        Assert.Equal(-1, DragSwipe.Classify(-60, -10, TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public void Clicks_slow_drags_and_vertical_drags_are_not_swipes()
    {
        Assert.Equal(0, DragSwipe.Classify(4, 2, TimeSpan.FromMilliseconds(90)));
        Assert.Equal(0, DragSwipe.Classify(120, 0, TimeSpan.FromMilliseconds(900)));
        Assert.Equal(0, DragSwipe.Classify(50, 40, TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void One_long_two_finger_swipe_skips_once()
    {
        var s = new SwipeDetector();
        var t0 = new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);
        int fired = 0;
        // 40 small deltas, 10 ms apart: one continuous gesture with momentum.
        for (int i = 0; i < 40; i++) fired += Math.Abs(s.Add(30, t0.AddMilliseconds(i * 10)));
        Assert.Equal(1, fired);
        // A new gesture after a pause and past the cooldown skips again.
        Assert.Equal(1, s.Add(150, t0.AddMilliseconds(1200)));
    }
}

public class KeyboardPillTests
{
    [Fact]
    public void Caps_state_trusts_the_flip_when_the_read_lags()
    {
        Assert.True(KeyboardPills.ResolveCaps(read: true, previous: false));
        Assert.True(KeyboardPills.ResolveCaps(read: false, previous: false));
        Assert.False(KeyboardPills.ResolveCaps(read: true, previous: true));
        Assert.True(KeyboardPills.ResolveCaps(read: true, previous: null));
        Assert.Equal("Caps Lock on", KeyboardPills.CapsText(true));
    }

    [Fact]
    public void Language_labels_match_the_taskbar_style()
    {
        Assert.Equal("ENG", KeyboardPills.Label(CultureInfo.GetCultureInfo("en-IN")));
        Assert.Equal("ENG", KeyboardPills.Label(0x0409));
        Assert.Equal("FRA", KeyboardPills.Label(CultureInfo.GetCultureInfo("fr-FR")));
        var hindi = KeyboardPills.Label(0x0439);
        Assert.StartsWith("हि", hindi); // हि…
        Assert.DoesNotContain("(", hindi);
        Assert.Equal("ENG → " + hindi, KeyboardPills.SwitchText("ENG", hindi));
    }
}
