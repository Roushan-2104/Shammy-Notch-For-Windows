using Shammy.Core.Quick;

namespace Shammy.Core.Tests;

public class QuickPhraseTests
{
    [Theory]
    [InlineData("keep awake")]
    [InlineData("Keep awake.")]
    [InlineData("stay awake")]
    [InlineData("keep my pc awake")]
    [InlineData("keep the screen awake")]
    [InlineData("keep-awake")]
    [InlineData("please keep awake")]
    [InlineData("hey shammy, keep awake")]
    [InlineData("turn on keep awake")]
    [InlineData("keep awake on")]
    public void Keep_awake_with_no_length_means_the_usual_hour(string text) =>
        Assert.Equal(new QuickCommand(QuickKind.KeepAwake), QuickPhrases.Parse(text));

    [Theory]
    [InlineData("keep awake for 2 hours", 120)]
    [InlineData("stay awake 30 min", 30)]
    [InlineData("keep awake for an hour", 60)]
    [InlineData("Keep awake for two hours.", 120)]
    [InlineData("keep awake 45m", 45)]
    [InlineData("keep awake for half an hour", 30)]
    [InlineData("keep awake for an hour and a half", 90)]
    [InlineData("keep awake 1h 30m", 90)]
    [InlineData("stay awake for 90 minutes", 90)]
    [InlineData("keep the laptop awake for 4 hours", 240)]
    [InlineData("keep awake for 90 seconds", 2)]
    [InlineData("keep awake for 3 days", 4320)]
    public void Keep_awake_reads_how_long(string text, int minutes) =>
        Assert.Equal(new QuickCommand(QuickKind.KeepAwake, minutes), QuickPhrases.Parse(text));

    [Theory]
    [InlineData("stop keep awake")]
    [InlineData("stop keeping awake")]
    [InlineData("Stop keep awake!")]
    [InlineData("cancel keep awake")]
    [InlineData("turn off keep awake")]
    [InlineData("keep awake off")]
    [InlineData("turn keep awake off")]
    [InlineData("stop staying awake")]
    [InlineData("stop keeping my pc awake")]
    public void Stop_keep_awake_turns_it_off(string text) =>
        Assert.Equal(new QuickCommand(QuickKind.StopKeepAwake), QuickPhrases.Parse(text));

    [Theory]
    [InlineData("lock")]
    [InlineData("Lock.")]
    [InlineData("lock my pc")]
    [InlineData("lock the screen")]
    [InlineData("lock pc")]
    [InlineData("lock this computer")]
    [InlineData("lock my laptop now")]
    [InlineData("please lock my pc")]
    public void Lock_locks_the_pc(string text) => Assert.Equal(new QuickCommand(QuickKind.Lock), QuickPhrases.Parse(text));

    [Theory]
    [InlineData("sleep my pc")]
    [InlineData("put the pc to sleep")]
    [InlineData("go to sleep")]
    [InlineData("Go to sleep.")]
    [InlineData("sleep pc")]
    [InlineData("put my computer to sleep")]
    [InlineData("put the laptop to sleep now")]
    public void Sleep_needs_the_pc_named_or_go_to_sleep(string text) =>
        Assert.Equal(new QuickCommand(QuickKind.Sleep), QuickPhrases.Parse(text));

    [Theory]
    [InlineData("quick actions")]
    [InlineData("Quick actions")]
    [InlineData("open quick actions")]
    [InlineData("show quick actions")]
    public void Quick_actions_opens_the_card(string text) => Assert.Equal(new QuickCommand(QuickKind.Open), QuickPhrases.Parse(text));

    [Theory]
    // Bare or topical words are searches.
    [InlineData("sleep")]
    [InlineData("awake")]
    [InlineData("how to sleep better")]
    [InlineData("sleep my baby")]
    [InlineData("go to sleep lyrics")]
    [InlineData("put the baby to sleep")]
    [InlineData("why does my pc go to sleep")]
    [InlineData("sleep pc shortcut")]
    [InlineData("lock screen")]
    [InlineData("lock screen wallpaper ideas")]
    [InlineData("lock picking")]
    [InlineData("lock my pc with a password")]
    [InlineData("how to lock my pc")]
    [InlineData("locksmith near me")]
    [InlineData("keep awake pills")]
    [InlineData("how to keep awake")]
    [InlineData("keep awake for studying")]
    [InlineData("keep awake for 2 hours tonight")]
    [InlineData("keep awake 30")]
    [InlineData("keep awake for 0 minutes")]
    [InlineData("stay awake song")]
    [InlineData("stay awake at night tips")]
    [InlineData("stop keep awake app from running")]
    [InlineData("quick actions windows 11")]
    [InlineData("quick")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_question_or_an_extra_topic_is_left_for_the_web(string text) => Assert.Null(QuickPhrases.Parse(text));
}

public class KeepAwakeClockTests
{
    static readonly DateTime Noon = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void It_is_off_until_started()
    {
        var c = new KeepAwakeClock();
        Assert.False(c.IsOn);
        Assert.Equal(0, c.MinutesLeft(Noon));
        Assert.False(c.Expire(Noon));
    }

    [Fact]
    public void It_always_has_an_end_time()
    {
        var c = new KeepAwakeClock();
        Assert.Equal(60, c.Start(Noon, KeepAwakeClock.DefaultMinutes));
        Assert.True(c.IsOn);
        Assert.Equal(Noon.AddHours(1), c.EndsUtc);
        Assert.Equal(60, c.StartedMinutes);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(30, 30)]
    [InlineData(720, 720)]
    [InlineData(4320, 720)]
    public void However_long_was_asked_it_runs_between_a_minute_and_twelve_hours(int asked, int runs) =>
        Assert.Equal(runs, new KeepAwakeClock().Start(Noon, asked));

    [Theory]
    [InlineData(0, 60)]
    [InlineData(1, 60)]
    [InlineData(60, 59)]
    [InlineData(119, 59)]
    [InlineData(120, 58)]
    [InlineData(3541, 1)]
    [InlineData(3599, 1)]
    [InlineData(3600, 0)]
    [InlineData(5000, 0)]
    public void Minutes_left_round_up_so_it_reads_one_minute_until_the_end(int secondsIn, int left)
    {
        var c = new KeepAwakeClock();
        c.Start(Noon, 60);
        Assert.Equal(left, c.MinutesLeft(Noon.AddSeconds(secondsIn)));
    }

    [Fact]
    public void It_ends_by_itself_exactly_once()
    {
        var c = new KeepAwakeClock();
        c.Start(Noon, 30);
        Assert.False(c.Expire(Noon.AddMinutes(29.9)));
        Assert.True(c.IsOn);
        Assert.True(c.Expire(Noon.AddMinutes(30)));
        Assert.False(c.IsOn);
        Assert.Null(c.EndsUtc);
        Assert.False(c.Expire(Noon.AddMinutes(31)));
    }

    [Fact]
    public void Stopping_turns_it_off_at_once()
    {
        var c = new KeepAwakeClock();
        c.Start(Noon, 120);
        c.Stop();
        Assert.False(c.IsOn);
        Assert.Equal(0, c.MinutesLeft(Noon));
        Assert.Equal(0, c.StartedMinutes);
    }

    [Fact]
    public void Starting_again_replaces_the_end_time()
    {
        var c = new KeepAwakeClock();
        c.Start(Noon, 30);
        c.Start(Noon.AddMinutes(10), 120);
        Assert.Equal(Noon.AddMinutes(130), c.EndsUtc);
    }

    [Fact]
    public void Pretending_time_passed_brings_the_end_closer()
    {
        var c = new KeepAwakeClock();
        c.Start(Noon, 60);
        c.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(40, c.MinutesLeft(Noon));
        c.Advance(TimeSpan.FromMinutes(45));
        Assert.True(c.Expire(Noon));
    }

    [Theory]
    [InlineData(0, 60)]        // a whole number of minutes left: the label changes in a minute
    [InlineData(30, 30.05)]    // half a minute into one: when that minute is used up
    [InlineData(3580, 20.05)]  // the last twenty seconds: at the end
    [InlineData(3599.8, 1)]    // never sooner than a second
    [InlineData(4000, 1)]      // already over: straight away
    public void It_looks_again_when_the_minutes_change_or_it_ends(double secondsIn, double expected)
    {
        var c = new KeepAwakeClock();
        c.Start(Noon, 60);
        Assert.Equal(expected, c.NextCheck(Noon.AddSeconds(secondsIn)).TotalSeconds, 3);
    }

    [Fact]
    public void While_off_there_is_nothing_urgent_to_look_at() =>
        Assert.Equal(TimeSpan.FromMinutes(1), new KeepAwakeClock().NextCheck(Noon));
}

public class KeepAwakeTextTests
{
    [Theory]
    [InlineData(58, "58 min")]
    [InlineData(1, "1 min")]
    [InlineData(60, "1 h")]
    [InlineData(118, "1 h 58 m")]
    [InlineData(240, "4 h")]
    [InlineData(719, "11 h 59 m")]
    public void Beside_the_pet_the_time_left_fits_nine_characters(int minutes, string expected)
    {
        Assert.Equal(expected, KeepAwakeText.Short(minutes));
        Assert.True(KeepAwakeText.Short(minutes).Length <= 9);
    }

    [Theory]
    [InlineData(58, "58 min")]
    [InlineData(120, "2 h")]
    [InlineData(118, "1 h 58 min")]
    public void In_a_sentence_minutes_are_spelled_out(int minutes, string expected) => Assert.Equal(expected, KeepAwakeText.Long(minutes));

    [Fact]
    public void The_tile_says_on_with_the_time_left_or_off()
    {
        Assert.Equal("On · 58 min left", KeepAwakeText.TileLine(true, 58));
        Assert.Equal("On · 1 h 30 min left", KeepAwakeText.TileLine(true, 90));
        Assert.Equal("On · 1 min left", KeepAwakeText.TileLine(true, 0));
        Assert.Equal("Off", KeepAwakeText.TileLine(false, 0));
    }

    [Fact]
    public void The_mic_tile_says_live_muted_or_missing()
    {
        Assert.Equal("Mic is live", KeepAwakeText.MicLine(false));
        Assert.Equal("Mic is muted", KeepAwakeText.MicLine(true));
        Assert.Equal("No microphone found", KeepAwakeText.MicLine(null));
    }
}
