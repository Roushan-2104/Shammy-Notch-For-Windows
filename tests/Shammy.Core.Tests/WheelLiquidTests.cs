using Shammy.Core.Wheel;

namespace Shammy.Core.Tests;

public class WheelMomentTests
{
    static WheelMoment Decide(bool call = false, string? process = null, string? title = null, bool music = false) =>
        WheelMoments.Decide(new MomentSignals(call, process, title, music));

    [Fact]
    public void Nothing_special_is_the_usual_wheel() => Assert.Equal(WheelMoment.Usual, Decide(process: "explorer", title: "Downloads"));

    [Fact]
    public void A_call_app_on_the_mic_is_a_call_whatever_is_in_front() =>
        Assert.Equal(WheelMoment.Call, Decide(call: true, process: "Code", title: "Program.cs - Visual Studio Code", music: true));

    [Theory]
    [InlineData("Zoom", "Zoom Meeting")]
    [InlineData("Zoom.exe", "Zoom Webinar")]
    [InlineData("ms-teams", "Meeting with Priya | Microsoft Teams")]
    [InlineData("chrome", "Meet - abc-defg-hij - Google Chrome")]
    [InlineData("msedge", "Google Meet - Work - Microsoft Edge")]
    [InlineData("firefox", "Jitsi Meet — Mozilla Firefox")]
    [InlineData("CiscoCollabHost", "")]
    public void A_meeting_in_front_is_a_call(string process, string title) => Assert.Equal(WheelMoment.Call, Decide(process: process, title: title));

    [Theory]
    [InlineData("Zoom", "Zoom Workplace")] // the home screen, not a meeting
    [InlineData("ms-teams", "Chat | Priya | Microsoft Teams")]
    [InlineData("winword", "Meeting notes.docx - Word")] // a document called "Meeting", not a call
    [InlineData("notepad", "Google Meet links.txt - Notepad")]
    public void Not_every_meeting_word_is_a_call(string process, string title) => Assert.NotEqual(WheelMoment.Call, Decide(process: process, title: title));

    [Theory]
    [InlineData("Spotify", "Spotify Premium", false)]
    [InlineData("chrome", "Kesariya - YouTube Music - Google Chrome", false)]
    [InlineData("explorer", "Desktop", true)] // music playing behind an ordinary window
    public void Music_in_front_or_playing_is_music(string process, string title, bool playing) =>
        Assert.Equal(WheelMoment.Music, Decide(process: process, title: title, music: playing));

    [Theory]
    [InlineData("Code", "WheelModule.cs - Shammy - Visual Studio Code")]
    [InlineData("devenv", "Shammy - Microsoft Visual Studio")]
    [InlineData("WINWORD", "Report.docx - Word")]
    [InlineData("Notion", "Plans")]
    [InlineData("chrome", "Essay - Google Docs - Google Chrome")]
    [InlineData("Obsidian.exe", "Daily note - Obsidian")]
    public void An_editor_in_front_is_writing(string process, string title) => Assert.Equal(WheelMoment.Writing, Decide(process: process, title: title));

    [Fact]
    public void The_app_in_front_beats_music_in_the_background()
    {
        Assert.Equal(WheelMoment.Writing, Decide(process: "Code", title: "x - Visual Studio Code", music: true));
        Assert.Equal(WheelMoment.Music, Decide(process: "Spotify", title: "Spotify", music: false));
    }

    [Fact]
    public void A_title_only_counts_in_a_browser() => Assert.Equal(WheelMoment.Usual, Decide(process: "explorer", title: "Spotify downloads"));

    [Fact]
    public void Each_moment_has_six_droplets_the_wheel_can_run()
    {
        foreach (var m in new[] { WheelMoment.Call, WheelMoment.Music, WheelMoment.Writing })
        {
            var ids = WheelMoments.Droplets(m);
            Assert.Equal(WheelGeometry.Beads, ids.Count);
            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.All(ids, id => Assert.NotNull(WheelSlots.Find(id)));
        }
        Assert.Empty(WheelMoments.Droplets(WheelMoment.Usual));
        Assert.Equal(new[] { "stage", "captions", "mute", "record", "notes", "share" }, WheelMoments.Droplets(WheelMoment.Call));
    }

    [Theory]
    [InlineData("call", WheelMoment.Call)]
    [InlineData(" Music ", WheelMoment.Music)]
    [InlineData("writing", WheelMoment.Writing)]
    [InlineData("usual", WheelMoment.Usual)]
    [InlineData("auto", null)]
    [InlineData("2", null)]
    public void Moments_parse_by_name(string text, WheelMoment? moment) => Assert.Equal(moment, WheelMoments.Parse(text));
}

public class WheelDialTests
{
    [Fact]
    public void One_notch_turns_one_droplet_rolling_toward_you_is_clockwise()
    {
        var dial = new WheelDial(6);
        Assert.Equal(1, dial.Scroll(-120));
        Assert.Equal(0, dial.Selected); // from nothing chosen, the first turn lands on the top droplet
        dial.Scroll(-120);
        Assert.Equal(1, dial.Selected);
        Assert.Equal(-1, dial.Scroll(120));
        Assert.Equal(0, dial.Selected);
    }

    [Fact]
    public void Turning_back_from_nothing_lands_on_the_last_droplet()
    {
        var dial = new WheelDial(6);
        dial.Scroll(120);
        Assert.Equal(5, dial.Selected);
    }

    [Fact]
    public void Turning_wraps_round()
    {
        Assert.Equal(0, WheelDial.Step(5, 1, 6));
        Assert.Equal(5, WheelDial.Step(0, -1, 6));
        Assert.Equal(2, WheelDial.Step(4, 4, 6));
        Assert.Equal(-1, WheelDial.Step(3, 1, 0));
    }

    [Fact]
    public void A_fine_wheel_needs_a_whole_notch()
    {
        var dial = new WheelDial(6);
        Assert.Equal(0, dial.Scroll(-40));
        Assert.Equal(0, dial.Scroll(-40));
        Assert.Equal(-1, dial.Selected);
        Assert.Equal(1, dial.Scroll(-40));
        Assert.Equal(0, dial.Selected);
    }

    [Fact]
    public void Reversing_starts_a_fresh_notch()
    {
        var dial = new WheelDial(6);
        dial.Scroll(-240); // two clockwise: droplet 1
        dial.Scroll(-100); // almost a third
        Assert.Equal(-1, dial.Scroll(120)); // one back, not swallowed by the leftover
        Assert.Equal(0, dial.Selected);
    }

    [Fact]
    public void A_fast_spin_turns_several_droplets_at_once()
    {
        var dial = new WheelDial(6);
        Assert.Equal(3, dial.Scroll(-360));
        Assert.Equal(2, dial.Selected);
    }

    [Fact]
    public void Pointing_picks_and_the_hub_clears()
    {
        var dial = new WheelDial(6);
        Assert.False(dial.Point(0, 0)); // opened under the pointer: nothing yet
        Assert.True(dial.Point(60, 35)); // lower right
        Assert.Equal(2, dial.Selected);
        Assert.True(dial.Point(2, 3));
        Assert.Equal(-1, dial.Selected); // back over the hub: letting go cancels
    }

    [Fact]
    public void A_turned_choice_survives_a_resting_pointer()
    {
        var dial = new WheelDial(6);
        dial.Point(0, 0);
        dial.Scroll(-360); // turned to droplet 2 without moving
        Assert.False(dial.Point(1, -2)); // the pointer jitters inside the hub
        Assert.Equal(2, dial.Selected);
        Assert.True(dial.Point(0, -60)); // a flick to the top takes over
        Assert.Equal(0, dial.Selected);
    }

    [Fact]
    public void Choosing_out_of_range_clears()
    {
        var dial = new WheelDial(6);
        dial.Choose(4);
        Assert.Equal(4, dial.Selected);
        dial.Choose(9);
        Assert.Equal(-1, dial.Selected);
        dial.Reset(3);
        Assert.Equal(3, dial.Count);
        Assert.Equal(-1, dial.Selected);
    }
}

public class GooTests
{
    static double Dist(GooPoint p, double x, double y) => Math.Sqrt((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y));

    [Fact]
    public void Far_apart_blobs_have_no_neck() => Assert.Null(Goo.Bridge(0, 0, 30, 200, 0, 20));

    [Fact]
    public void A_blob_inside_another_has_no_neck() => Assert.Null(Goo.Bridge(0, 0, 30, 5, 0, 10));

    [Fact]
    public void A_zero_size_blob_has_no_neck() => Assert.Null(Goo.Bridge(0, 0, 30, 40, 0, 0));

    [Theory]
    [InlineData(60)] // apart, within reach
    [InlineData(40)] // overlapping
    public void The_neck_starts_and_ends_on_the_circles(double d)
    {
        var b = Goo.Bridge(0, 0, 30, d, 0, 20);
        Assert.NotNull(b);
        Assert.Equal(30, Dist(b!.P1, 0, 0), 6);
        Assert.Equal(30, Dist(b.P2, 0, 0), 6);
        Assert.Equal(20, Dist(b.P3, d, 0), 6);
        Assert.Equal(20, Dist(b.P4, d, 0), 6);
    }

    [Fact]
    public void Equal_blobs_give_a_mirror_image_neck()
    {
        var b = Goo.Bridge(0, 0, 20, 50, 0, 20)!;
        Assert.Equal(b.P1.X, b.P2.X, 6);
        Assert.Equal(b.P1.Y, -b.P2.Y, 6);
        Assert.Equal(b.H1.Y, -b.H2.Y, 6);
        Assert.Equal(50 - b.P1.X, b.P3.X, 6); // the two ends mirror each other too
    }

    [Fact]
    public void The_neck_reaches_toward_the_other_blob()
    {
        var b = Goo.Bridge(0, 0, 30, 0, 70, 20)!; // the second blob below
        Assert.True(b.P1.Y > 0 && b.P2.Y > 0);
        Assert.True(b.P3.Y < 70 && b.P4.Y < 70);
    }

    static readonly PourShape Pour = new(26, 13, 58);

    static double WidthAt(IReadOnlyList<GooPoint> points, double y)
    {
        var row = points.Where(p => Math.Abs(p.Y - y) < 0.01).ToList();
        return row.Max(p => p.X) - row.Min(p => p.X);
    }

    [Fact]
    public void The_stream_runs_from_the_island_to_where_it_has_reached()
    {
        var points = Goo.Stream(100, 20, 100, 420, 0, 0.5, Pour, 0, wobble: 0);
        Assert.NotEmpty(points);
        Assert.All(points, p => Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y)));
        Assert.Equal(20, points.Min(p => p.Y), 1);
        Assert.Equal(220, points.Max(p => p.Y), 1); // half way down, where the pour has reached
        Assert.Equal(26, WidthAt(points, 20), 2); // a drip forming under the pill
        Assert.Equal(58, WidthAt(points, 220), 6); // pooling into the blob at its end
    }

    [Fact]
    public void A_real_pour_is_wide_at_the_island_narrower_mid_way_and_flares_into_the_hub()
    {
        double length = 500;
        double Width(double s) => 2 * Goo.HalfWidth(s, length - s, s / length, Pour, 0, wobble: 0);
        Assert.Equal(26, Width(0), 2);
        Assert.InRange(Width(length / 2), 12, 14);
        Assert.Equal(58, Width(length), 6);
        // Smooth: it narrows steadily out of the island and widens steadily into the hub, no kinks or bumps.
        for (double s = 0; s < 120; s += 5) Assert.True(Width(s + 5) <= Width(s) + 1e-9);
        for (double s = length - 120; s < length; s += 5) Assert.True(Width(s + 5) >= Width(s) - 1e-9);
        // Where it meets a 32 px hub's edge it is still well over the middle's width: a neck, not a wire.
        Assert.True(Width(length - 32) > 18);
    }

    [Fact]
    public void Draining_thins_the_stream()
    {
        var thin = new PourShape(26 * 0.2, 2.5, 10);
        Assert.True(2 * Goo.HalfWidth(250, 250, 0.5, thin, 0, wobble: 0) < 3);
    }

    [Fact]
    public void A_drained_stream_draws_nothing()
    {
        Assert.Empty(Goo.Stream(0, 0, 0, 300, 0.4, 0.4, Pour, 0));
        Assert.Empty(Goo.Stream(0, 0, 0, 0, 0, 1, Pour, 0));
    }

    [Fact]
    public void The_stream_stays_attached_while_it_wobbles()
    {
        for (double t = 0; t < 2; t += 0.13)
        {
            var points = Goo.Stream(0, 0, 0, 300, 0, 1, new PourShape(26, 13, 50), t, wobble: 1);
            int half = points.Count / 2;
            // The ends sit on the line between the island and the hub, whatever the sway.
            Assert.Equal(0, (points[0].X + points[^1].X) / 2, 6);
            Assert.Equal(0, (points[half - 1].X + points[half].X) / 2, 6);
        }
    }

    [Fact]
    public void Gravity_eases_in_and_lands_exactly()
    {
        Assert.Equal(0, Goo.EaseIn(0));
        Assert.Equal(1, Goo.EaseIn(1), 9);
        Assert.True(Goo.EaseIn(0.5) < 0.5);
        for (double t = 0; t < 1; t += 0.05) Assert.True(Goo.EaseIn(t + 0.05) >= Goo.EaseIn(t));
    }

    [Fact]
    public void The_tick_is_short_quiet_and_dies_away()
    {
        var s = DialTick.Render();
        Assert.InRange(s.Length, DialTick.SampleRate / 100, DialTick.SampleRate / 20); // 10–50 ms
        Assert.True(s.Max(v => Math.Abs((int)v)) < short.MaxValue / 3);
        Assert.True(Math.Abs((int)s[0]) < 50); // no pop at the start
        Assert.True(s.Skip(s.Length * 4 / 5).Max(v => Math.Abs((int)v)) < 200);
    }
}
