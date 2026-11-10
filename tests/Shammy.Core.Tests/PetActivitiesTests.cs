using Shammy.Core.Activities;
using Shammy.Core.Notch;

namespace Shammy.Core.Tests;

public class PetActivitiesTests
{
    static PetActivityState S(string id, PetCostume c) => new() { Id = id, Costume = c };

    [Theory]
    [InlineData(PetCostume.Call, false, false, false, PetFace.Call)]
    [InlineData(PetCostume.Call, true, false, false, PetFace.CallMuted)]
    [InlineData(PetCostume.Music, false, true, false, PetFace.Disc)]
    [InlineData(PetCostume.Music, false, false, false, PetFace.DiscPaused)]
    [InlineData(PetCostume.Timer, false, false, false, PetFace.TimerWatch)]
    [InlineData(PetCostume.Timer, false, false, true, PetFace.TimerDone)]
    [InlineData(PetCostume.Recording, false, false, false, PetFace.Ninja)]
    [InlineData(PetCostume.Download, false, false, false, PetFace.DownloadHold)]
    [InlineData(PetCostume.Download, false, false, true, PetFace.DownloadNom)]
    [InlineData(PetCostume.Charging, false, false, false, PetFace.ChargeSip)]
    [InlineData(PetCostume.Earbuds, false, false, false, PetFace.Earbuds)]
    [InlineData(PetCostume.Notify, false, false, false, PetFace.Notify)]
    public void Each_costume_has_its_face(PetCostume costume, bool muted, bool playing, bool done, PetFace face) =>
        Assert.Equal(face, PetActivities.Face(new PetActivityState { Costume = costume, Muted = muted, Playing = playing, Done = done }));

    [Fact]
    public void Call_circle_mutes_and_turns_red_when_muted()
    {
        var on = PetActivities.Circle(S("c", PetCostume.Call))!;
        Assert.Equal(CircleAction.Mute, on.Action);
        Assert.Equal(CircleTone.Neutral, on.Tone);
        var muted = PetActivities.Circle(S("c", PetCostume.Call) with { Muted = true })!;
        Assert.Equal(CircleAction.Unmute, muted.Action);
        Assert.Equal(CircleTone.Red, muted.Tone);
    }

    [Fact]
    public void Toggles_stay_neutral_and_only_open_or_dismiss_get_the_accent()
    {
        Assert.Equal((CircleAction.Pause, CircleTone.Neutral), Pair(S("m", PetCostume.Music) with { Playing = true }));
        Assert.Equal((CircleAction.Play, CircleTone.Neutral), Pair(S("m", PetCostume.Music)));
        Assert.Equal((CircleAction.Pause, CircleTone.Neutral), Pair(S("t", PetCostume.Timer)));
        Assert.Equal((CircleAction.Resume, CircleTone.Neutral), Pair(S("t", PetCostume.Timer) with { Paused = true }));
        Assert.Equal((CircleAction.Dismiss, CircleTone.Accent), Pair(S("t", PetCostume.Timer) with { Done = true }));
        Assert.Equal((CircleAction.Stop, CircleTone.Neutral), Pair(S("r", PetCostume.Recording) with { CanStop = true, CanOpen = true }));
        Assert.Equal((CircleAction.Open, CircleTone.Accent), Pair(S("r", PetCostume.Recording) with { CanOpen = true }));
        Assert.Equal((CircleAction.Open, CircleTone.Accent), Pair(S("d", PetCostume.Download) with { Done = true, CanOpen = true }));
        Assert.Equal((CircleAction.Open, CircleTone.Accent), Pair(S("n", PetCostume.Notify) with { CanOpen = true }));

        static (CircleAction, CircleTone) Pair(PetActivityState s) => PetActivities.Circle(s) is { } c ? (c.Action, c.Tone) : default;
    }

    [Fact]
    public void A_running_download_shows_a_waiting_open_that_cannot_be_clicked()
    {
        var c = PetActivities.Circle(S("d", PetCostume.Download) with { Progress = 0.64 })!;
        Assert.Equal(CircleAction.OpenWhenDone, c.Action);
        Assert.False(c.Enabled);
    }

    [Fact]
    public void Nothing_to_do_means_no_circle()
    {
        Assert.Null(PetActivities.Circle(S("b", PetCostume.Charging)));
        Assert.Null(PetActivities.Circle(S("e", PetCostume.Earbuds)));
        Assert.Null(PetActivities.Circle(S("r", PetCostume.Recording)));
        Assert.Null(PetActivities.Circle(S("n", PetCostume.Notify)));
        Assert.Null(PetActivities.Circle(S("d", PetCostume.Download) with { Done = true }));
    }

    [Fact]
    public void Top_activity_owns_the_pet_and_the_next_ongoing_one_waits_as_the_dot()
    {
        var call = S("call", PetCostume.Call);
        var music = S("music", PetCostume.Music) with { Playing = true };
        var stage = PetActivities.Stage(new[] { call, music }, null);
        Assert.Equal("call", stage.Owner);
        Assert.Equal("music", stage.Dot);
    }

    [Fact]
    public void Clicking_the_dot_swaps_priority_while_it_lasts()
    {
        var call = S("call", PetCostume.Call);
        var music = S("music", PetCostume.Music) with { Playing = true };
        var stage = PetActivities.Stage(new[] { call, music }, "music");
        Assert.Equal(("music", "call"), (stage.Owner, stage.Dot));
        // The preferred one ended: back to the top.
        Assert.Equal(("call", null), (PetActivities.Stage(new[] { call }, "music").Owner, PetActivities.Stage(new[] { call }, "music").Dot));
    }

    [Fact]
    public void A_one_off_moment_takes_the_pet_over_a_swap_and_never_waits_as_a_dot()
    {
        var notify = S("n", PetCostume.Notify);
        var call = S("call", PetCostume.Call);
        var music = S("music", PetCostume.Music);
        var stage = PetActivities.Stage(new[] { notify, call, music }, "music");
        Assert.Equal("n", stage.Owner);
        Assert.Equal("call", stage.Dot);

        var charging = S("battery", PetCostume.Charging);
        var earbuds = S("bt", PetCostume.Earbuds);
        Assert.Null(PetActivities.Stage(new[] { call, charging, earbuds }, null).Dot);
        Assert.Null(PetActivities.Stage(new[] { call, S("t", PetCostume.Timer) with { Done = true } }, null).Dot);
    }

    [Fact]
    public void Empty_stage_and_a_stale_preference_are_harmless()
    {
        Assert.Equal(new PetStage(null, null), PetActivities.Stage(Array.Empty<PetActivityState>(), "x"));
        var timer = S("t", PetCostume.Timer);
        Assert.Equal(new PetStage("t", null), PetActivities.Stage(new[] { timer }, "gone"));
        // A one-off can't be preferred into the pet's seat from below.
        var dl = S("d", PetCostume.Download) with { Done = true };
        Assert.Equal("t", PetActivities.Stage(new[] { timer, dl }, "d").Owner);
    }

    [Fact]
    public void Body_click_acts_only_for_one_off_moments()
    {
        Assert.True(PetActivities.BodyClickRunsActivity(S("n", PetCostume.Notify)));
        Assert.True(PetActivities.BodyClickRunsActivity(S("t", PetCostume.Timer) with { Done = true }));
        Assert.True(PetActivities.BodyClickRunsActivity(S("d", PetCostume.Download) with { Done = true }));
        Assert.False(PetActivities.BodyClickRunsActivity(S("c", PetCostume.Call)));
        Assert.False(PetActivities.BodyClickRunsActivity(S("t", PetCostume.Timer)));
        Assert.False(PetActivities.BodyClickRunsActivity(S("m", PetCostume.Music)));
    }

    [Fact]
    public void Notification_value_hides_while_the_screen_may_be_seen()
    {
        Assert.Equal("Riya", PetActivities.NotifyValue("Riya", "WhatsApp", presenting: false));
        Assert.Equal("", PetActivities.NotifyValue("Riya", "WhatsApp", presenting: true));
        Assert.Equal("", PetActivities.NotifyValue("WhatsApp", "WhatsApp", presenting: false));
        Assert.Equal("", PetActivities.NotifyValue("  ", "WhatsApp", presenting: false));
    }

    [Fact]
    public void Clock_reads_like_a_call_timer()
    {
        Assert.Equal("12:41", PetActivities.Clock(TimeSpan.FromSeconds(761)));
        Assert.Equal("1:02:03", PetActivities.Clock(new TimeSpan(1, 2, 3)));
        Assert.Equal("0:00", PetActivities.Clock(TimeSpan.FromSeconds(-3)));
    }

    [Fact]
    public void Talk_meter_gates_noise_opens_fast_and_holds_speaking_briefly()
    {
        var m = new TalkMeter();
        var t = TimeSpan.Zero;
        Assert.Equal(0, m.Sample(0.02, t));
        Assert.False(m.Speaking);

        double first = m.Sample(0.35, t += TimeSpan.FromMilliseconds(50));
        Assert.True(first > 0.5);
        Assert.True(m.Speaking);

        // Quiet again: the mouth closes over a few samples, speaking holds for a moment.
        double next = m.Sample(0, t += TimeSpan.FromMilliseconds(50));
        Assert.True(next < first && next > 0);
        Assert.True(m.Speaking);
        for (int i = 0; i < 20; i++) m.Sample(0, t += TimeSpan.FromMilliseconds(50));
        Assert.Equal(0, m.Open);
        Assert.False(m.Speaking);
    }

    [Fact]
    public void Talk_target_is_bounded()
    {
        Assert.Equal(0, TalkMeter.Target(double.NaN));
        Assert.Equal(0, TalkMeter.Target(TalkMeter.Gate));
        Assert.Equal(1, TalkMeter.Target(1));
        Assert.InRange(TalkMeter.Target(0.1), 0.01, 0.99);
    }

    [Fact]
    public void Activity_manager_ranks_every_live_activity()
    {
        var m = new ActivityManager();
        m.Post(new Activity("media", ActivityKind.NowPlaying, 20));
        m.Post(new Activity("call", ActivityKind.Custom, 65));
        m.Post(new Activity("timer", ActivityKind.Custom, 45));
        Assert.Equal(new[] { "call", "timer", "media" }, m.Ranked.Select(a => a.Id));
        m.Remove("call");
        Assert.Equal(new[] { "timer", "media" }, m.Ranked.Select(a => a.Id));
    }
}
