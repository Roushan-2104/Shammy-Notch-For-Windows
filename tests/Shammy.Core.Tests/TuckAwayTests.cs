using Shammy.Core.Notch;

namespace Shammy.Core.Tests;

public class PushDetectorTests
{
    static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>Upward reports every 8 ms (a 125 Hz mouse) for <paramref name="ms"/>, <paramref name="step"/> counts each.</summary>
    static bool Push(PushDetector d, double fromMs, double ms, int step = -3, bool atEdge = true)
    {
        bool fired = false;
        for (double t = fromMs; t <= fromMs + ms; t += 8) fired |= d.Add(step, atEdge, Ms(t));
        return fired;
    }

    [Fact]
    public void A_steady_push_of_a_third_of_a_second_tucks_it_away() => Assert.True(Push(new PushDetector(), 0, 320));

    [Fact]
    public void Reaching_the_edge_on_the_way_to_a_tab_does_not() => Assert.False(Push(new PushDetector(), 0, 120, -20));

    [Fact]
    public void Pushing_below_the_top_edge_does_not() => Assert.False(Push(new PushDetector(), 0, 600, atEdge: false));

    [Fact]
    public void A_resting_hands_jitter_does_not()
    {
        var d = new PushDetector();
        bool fired = false;
        // One count up now and then, with pauses: never a push.
        for (int t = 0; t < 3000; t += 150) fired |= d.Add(-1, true, Ms(t));
        Assert.False(fired);
    }

    [Fact]
    public void A_slow_tiny_drift_is_not_enough_travel()
    {
        var d = new PushDetector();
        bool fired = false;
        for (int t = 0; t <= 400; t += 40) fired |= d.Add(-1, true, Ms(t));
        Assert.False(fired);
    }

    [Fact]
    public void A_pause_starts_the_push_over()
    {
        var d = new PushDetector();
        Assert.False(Push(d, 0, 200));
        Assert.False(Push(d, 400, 200));
        Assert.True(Push(d, 700, 320));
    }

    [Fact]
    public void Moving_down_starts_it_over()
    {
        var d = new PushDetector();
        Assert.False(Push(d, 0, 250));
        Assert.False(d.Add(5, true, Ms(258)));
        Assert.False(Push(d, 266, 200));
    }

    [Fact]
    public void Sideways_movement_neither_counts_nor_stops_it()
    {
        var d = new PushDetector();
        bool fired = false;
        for (double t = 0; t <= 330; t += 8) fired |= d.Add(t % 16 == 0 ? 0 : -6, true, Ms(t));
        Assert.True(fired);
    }

    [Fact]
    public void One_push_per_second()
    {
        var d = new PushDetector();
        Assert.True(Push(d, 0, 320));
        Assert.False(Push(d, 330, 500));
        Assert.True(Push(d, 1400, 320));
    }

    [Fact]
    public void While_pushing_it_says_so_and_stops_saying_so_after_a_pause()
    {
        var d = new PushDetector();
        Push(d, 0, 100);
        Assert.True(d.InProgress(Ms(110)));
        Assert.False(d.InProgress(Ms(300)));
    }

    [Fact]
    public void A_clock_going_backwards_does_not_fire() => Assert.False(new PushDetector().Add(-50, true, Ms(-10)));

    [Fact]
    public void It_comes_back_after_a_few_seconds() => Assert.InRange(TuckAway.Away.TotalSeconds, 3, 8);
}
