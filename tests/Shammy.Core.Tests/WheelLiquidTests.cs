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

    [Fact]
    public void At_rest_only_the_chosen_droplet_stays_joined_to_the_hub()
    {
        // The dial's sizes: a 32 px hub, droplets of 21 px on a 90 px orbit, the chosen one swollen 1.25×.
        Assert.Null(Goo.Bridge(0, 0, 32, 0, -90, 21));
        Assert.NotNull(Goo.Bridge(0, 0, 32, 0, -90, 21 * 1.25));
        // Beading off (half way out) every droplet is still joined.
        Assert.NotNull(Goo.Bridge(0, 0, 32, 45, 0, 21));
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
