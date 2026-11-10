using Shammy.Core.Nudges;

namespace Shammy.Core.Tests;

public class NudgeSchedulerTests
{
    static readonly DateTime Start = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    static TimeSpan Min(double minutes) => TimeSpan.FromMinutes(minutes);

    /// <summary>Someone at a PC: a check every 30 s, working (fresh input) or away (input getting older).</summary>
    sealed class Desk
    {
        public readonly NudgeScheduler Nudges = new();
        public readonly List<(NudgeKind Kind, TimeSpan At)> Shown = new();
        public DateTime Now = Start;
        public bool EyeBreaks = true, Water, Free = true;

        public Desk(bool eyeBreaks = true, bool water = false)
        {
            EyeBreaks = eyeBreaks;
            Water = water;
            Check(TimeSpan.Zero);
        }

        public void Work(TimeSpan time)
        {
            for (var t = Tick; t <= time; t += Tick) Step(TimeSpan.Zero);
        }

        public void Away(TimeSpan time)
        {
            for (var t = Tick; t <= time; t += Tick) Step(t);
        }

        public void Check(TimeSpan idle)
        {
            if (Nudges.Check(Now, idle, Free, EyeBreaks, Water) is { } kind) Shown.Add((kind, Now - Start));
        }

        void Step(TimeSpan idle)
        {
            Now += Tick;
            Check(idle);
        }
    }

    // ---- Counting active time ---------------------------------------------------------------------

    [Fact]
    public void An_eye_break_shows_after_twenty_minutes_of_use()
    {
        var desk = new Desk();
        desk.Work(Min(19.5));
        Assert.Empty(desk.Shown);
        Assert.Equal(Min(0.5), desk.Nudges.EyeLeft);

        desk.Work(Min(0.5));
        Assert.Equal(new[] { (NudgeKind.EyeBreak, Min(20)) }, desk.Shown);
    }

    [Fact]
    public void The_count_starts_again_once_a_break_has_shown()
    {
        var desk = new Desk();
        desk.Work(Min(60));
        Assert.Equal(new[] { (NudgeKind.EyeBreak, Min(20)), (NudgeKind.EyeBreak, Min(40)), (NudgeKind.EyeBreak, Min(60)) }, desk.Shown);
        Assert.Equal(TimeSpan.Zero, desk.Nudges.EyeActive);
    }

    [Fact]
    public void Water_shows_after_ninety_minutes_of_use()
    {
        var desk = new Desk(eyeBreaks: false, water: true);
        desk.Work(Min(89.5));
        Assert.Empty(desk.Shown);

        desk.Work(Min(0.5));
        Assert.Equal(new[] { (NudgeKind.Water, Min(90)) }, desk.Shown);
        Assert.Equal(NudgeScheduler.WaterEvery, desk.Nudges.WaterLeft);
    }

    [Fact]
    public void More_than_two_minutes_without_input_pauses_the_count()
    {
        var desk = new Desk();
        desk.Work(Min(10));
        // The first two minutes still count (reading needs no keys); the next two don't.
        desk.Away(Min(4));
        Assert.Equal(Min(12), desk.Nudges.EyeActive);

        desk.Work(Min(7.5));
        Assert.Empty(desk.Shown);
        desk.Work(Min(0.5));
        Assert.Equal(new[] { (NudgeKind.EyeBreak, Min(22)) }, desk.Shown);
    }

    [Fact]
    public void Five_minutes_without_input_is_a_break_taken()
    {
        var desk = new Desk();
        desk.Work(Min(15));
        desk.Away(Min(5));
        Assert.Equal(TimeSpan.Zero, desk.Nudges.EyeActive);

        // A whole twenty minutes from coming back, not five.
        desk.Work(Min(19.5));
        Assert.Empty(desk.Shown);
        desk.Work(Min(0.5));
        Assert.Equal(new[] { (NudgeKind.EyeBreak, Min(40)) }, desk.Shown);
    }

    [Fact]
    public void A_long_time_away_only_pauses_the_water_count()
    {
        var desk = new Desk(eyeBreaks: true, water: true);
        desk.Work(Min(15));
        desk.Away(Min(30));
        Assert.Equal(TimeSpan.Zero, desk.Nudges.EyeActive);
        Assert.Equal(Min(17), desk.Nudges.WaterActive);
    }

    [Fact]
    public void A_pc_that_slept_has_given_the_eyes_their_break()
    {
        var desk = new Desk();
        desk.Work(Min(15));
        desk.Now += Min(45);
        // Waking it up is fresh input.
        desk.Check(TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, desk.Nudges.EyeActive);
        Assert.Empty(desk.Shown);
    }

    [Fact]
    public void A_late_check_counts_a_minute_at_most()
    {
        var desk = new Desk();
        desk.Work(Min(5));
        desk.Now += Min(3);
        desk.Check(TimeSpan.Zero);
        Assert.Equal(Min(6), desk.Nudges.EyeActive);
    }

    [Fact]
    public void A_clock_set_back_counts_nothing_and_breaks_nothing()
    {
        var desk = new Desk();
        desk.Work(Min(5));
        desk.Now -= Min(30);
        desk.Check(TimeSpan.Zero);
        Assert.Equal(Min(5), desk.Nudges.EyeActive);

        desk.Work(Min(15));
        Assert.Single(desk.Shown);
    }

    // ---- Waiting for a free moment ----------------------------------------------------------------

    [Fact]
    public void A_break_due_during_a_call_waits_for_the_first_free_moment()
    {
        var desk = new Desk { Free = false };
        desk.Work(Min(23));
        Assert.Empty(desk.Shown);
        Assert.Equal(Start + Min(20), desk.Nudges.EyeDueSince);
        Assert.Equal(NudgeWait.Busy, desk.Nudges.EyeWait);

        desk.Free = true;
        desk.Work(Min(0.5));
        Assert.Equal(new[] { (NudgeKind.EyeBreak, Min(23.5)) }, desk.Shown);
        Assert.Null(desk.Nudges.EyeDueSince);
        Assert.Equal(NudgeWait.None, desk.Nudges.EyeWait);
    }

    [Fact]
    public void It_still_shows_ten_minutes_late()
    {
        var desk = new Desk { Free = false };
        desk.Work(Min(29.5));
        desk.Free = true;
        desk.Work(Min(0.5));
        Assert.Equal(new[] { (NudgeKind.EyeBreak, Min(30)) }, desk.Shown);
        Assert.Equal(0, desk.Nudges.EyeSkipped);
    }

    [Fact]
    public void After_ten_busy_minutes_the_round_is_skipped()
    {
        var desk = new Desk { Free = false };
        desk.Work(Min(30));
        Assert.Empty(desk.Shown);
        Assert.Equal(1, desk.Nudges.EyeSkipped);
        Assert.Null(desk.Nudges.EyeDueSince);
        Assert.Equal(TimeSpan.Zero, desk.Nudges.EyeActive);

        // Free again a moment later: nothing shows until the next round comes up.
        desk.Free = true;
        desk.Work(Min(19.5));
        Assert.Empty(desk.Shown);
        desk.Work(Min(0.5));
        Assert.Equal(new[] { (NudgeKind.EyeBreak, Min(50)) }, desk.Shown);
    }

    [Fact]
    public void Water_waits_and_is_skipped_the_same_way()
    {
        var desk = new Desk(eyeBreaks: false, water: true) { Free = false };
        desk.Work(Min(95));
        Assert.Equal(NudgeWait.Busy, desk.Nudges.WaterWait);
        Assert.Equal(Start + Min(90), desk.Nudges.WaterDueSince);

        desk.Work(Min(5));
        Assert.Empty(desk.Shown);
        Assert.Equal(1, desk.Nudges.WaterSkipped);
        Assert.Equal(TimeSpan.Zero, desk.Nudges.WaterActive);
    }

    [Fact]
    public void Walking_away_while_a_break_waits_drops_it()
    {
        var desk = new Desk { Free = false };
        desk.Work(Min(21));
        desk.Away(Min(5));
        desk.Free = true;
        desk.Work(Min(1));
        Assert.Empty(desk.Shown);
        Assert.Equal(0, desk.Nudges.EyeSkipped);
    }

    // ---- Both at once -----------------------------------------------------------------------------

    [Fact]
    public void When_both_are_due_the_eye_break_comes_first_and_water_two_minutes_later()
    {
        var desk = new Desk(eyeBreaks: true, water: true);
        desk.Nudges.Advance(Min(90));
        desk.Work(Min(0.5));
        Assert.Equal(new[] { (NudgeKind.EyeBreak, Min(0.5)) }, desk.Shown);
        Assert.Equal(NudgeWait.TooSoon, desk.Nudges.WaterWait);

        desk.Work(Min(1.5));
        Assert.Single(desk.Shown);
        desk.Work(Min(0.5));
        Assert.Equal(new[] { (NudgeKind.EyeBreak, Min(0.5)), (NudgeKind.Water, Min(2.5)) }, desk.Shown);
    }

    [Fact]
    public void While_both_wait_for_a_free_moment_both_say_the_moment_is_busy()
    {
        var desk = new Desk(eyeBreaks: true, water: true) { Free = false };
        desk.Nudges.Advance(Min(90));
        desk.Work(Min(1));
        Assert.Equal(NudgeWait.Busy, desk.Nudges.EyeWait);
        Assert.Equal(NudgeWait.Busy, desk.Nudges.WaterWait);
    }

    [Fact]
    public void Water_due_again_right_after_water_still_lets_the_eye_break_go_first()
    {
        var desk = new Desk(eyeBreaks: true, water: true);
        desk.Work(Min(90));
        desk.Nudges.Advance(Min(90));
        desk.Work(Min(0.5));
        // Water showed at 90 minutes, so the eye break waits its two minutes and water waits behind it.
        Assert.Equal(NudgeWait.TooSoon, desk.Nudges.EyeWait);
        Assert.Equal(NudgeWait.EyeBreakFirst, desk.Nudges.WaterWait);

        desk.Work(Min(3.5));
        Assert.Equal(new[] { (NudgeKind.Water, Min(90)), (NudgeKind.EyeBreak, Min(92)), (NudgeKind.Water, Min(94)) }, desk.Shown.Skip(4));
    }

    [Fact]
    public void Water_behind_a_late_eye_break_is_not_skipped_for_those_two_minutes()
    {
        var desk = new Desk(eyeBreaks: true, water: true) { Free = false };
        desk.Nudges.Advance(Min(90));
        // Both fall due at 0:30 and wait nine and a half minutes.
        desk.Work(Min(10));
        desk.Free = true;
        desk.Work(Min(0.5));
        desk.Work(Min(2));
        Assert.Equal(new[] { (NudgeKind.EyeBreak, Min(10.5)), (NudgeKind.Water, Min(12.5)) }, desk.Shown);
        Assert.Equal(0, desk.Nudges.WaterSkipped);
    }

    [Fact]
    public void An_eye_break_also_keeps_two_minutes_from_a_water_reminder()
    {
        var desk = new Desk(eyeBreaks: true, water: true);
        // Eye breaks at 20, 40, 60 and 80 minutes; water at 90, with ten minutes on the eye count.
        desk.Work(Min(90));
        Assert.Equal((NudgeKind.Water, Min(90)), desk.Shown[^1]);

        desk.Nudges.Advance(Min(10));
        desk.Work(Min(0.5));
        Assert.Equal(NudgeWait.TooSoon, desk.Nudges.EyeWait);
        desk.Work(Min(1.5));
        Assert.Equal((NudgeKind.EyeBreak, Min(92)), desk.Shown[^1]);
    }

    // ---- Switched off -----------------------------------------------------------------------------

    [Fact]
    public void Nudges_that_are_off_never_show_and_count_nothing()
    {
        var desk = new Desk(eyeBreaks: false, water: false);
        desk.Work(Min(240));
        Assert.Empty(desk.Shown);
        Assert.Equal(TimeSpan.Zero, desk.Nudges.EyeActive);
        Assert.Equal(TimeSpan.Zero, desk.Nudges.WaterActive);
    }

    [Fact]
    public void With_only_eye_breaks_on_water_never_shows()
    {
        var desk = new Desk(eyeBreaks: true, water: false);
        desk.Work(Min(200));
        Assert.Equal(10, desk.Shown.Count);
        Assert.DoesNotContain(desk.Shown, s => s.Kind == NudgeKind.Water);
    }

    [Fact]
    public void Turning_eye_breaks_off_drops_one_that_was_waiting()
    {
        var desk = new Desk { Free = false };
        desk.Work(Min(21));
        desk.EyeBreaks = false;
        desk.Work(Min(0.5));
        Assert.Null(desk.Nudges.EyeDueSince);

        // Back on: a fresh twenty minutes.
        desk.Free = desk.EyeBreaks = true;
        desk.Work(Min(19.5));
        Assert.Empty(desk.Shown);
        desk.Work(Min(0.5));
        Assert.Single(desk.Shown);
    }

    [Fact]
    public void Time_pretended_for_a_test_cannot_bring_back_a_nudge_that_is_off()
    {
        var desk = new Desk(eyeBreaks: true, water: false);
        desk.Nudges.Advance(Min(90));
        desk.Work(Min(5));
        Assert.Equal(new[] { (NudgeKind.EyeBreak, Min(0.5)) }, desk.Shown);
    }

    // ---- By hand ----------------------------------------------------------------------------------

    [Fact]
    public void A_break_taken_by_hand_starts_the_count_again()
    {
        var desk = new Desk();
        desk.Work(Min(15));
        desk.Nudges.Shown(NudgeKind.EyeBreak, desk.Now);
        Assert.Equal(TimeSpan.Zero, desk.Nudges.EyeActive);

        desk.Work(Min(19.5));
        Assert.Empty(desk.Shown);
        desk.Work(Min(0.5));
        Assert.Equal(new[] { (NudgeKind.EyeBreak, Min(35)) }, desk.Shown);
    }
}

public class WaterTallyTests
{
    static readonly DateTime Evening = new(2026, 11, 10, 23, 59, 0, DateTimeKind.Local);

    [Fact]
    public void Each_confirmed_glass_adds_one_to_today()
    {
        var tally = new WaterTally();
        Assert.Equal(0, tally.Today(Evening));
        Assert.Equal(1, tally.Add(Evening.AddHours(-9)));
        Assert.Equal(2, tally.Add(Evening.AddHours(-4)));
        Assert.Equal(2, tally.Today(Evening));
    }

    [Fact]
    public void The_count_starts_again_at_local_midnight()
    {
        var tally = new WaterTally();
        tally.Add(Evening);
        tally.Add(Evening);
        Assert.Equal(2, tally.Today(Evening.AddSeconds(59)));
        Assert.Equal(0, tally.Today(Evening.AddMinutes(1)));

        Assert.Equal(1, tally.Add(Evening.AddMinutes(2)));
        Assert.Equal("2026-11-11", tally.Day);
    }

    [Fact]
    public void A_count_saved_on_another_day_reads_as_none()
    {
        var saved = new WaterTally { Day = "2026-11-09", Glasses = 5 };
        Assert.Equal(0, saved.Today(Evening));
        Assert.Equal(1, saved.Add(Evening));
    }

    [Fact]
    public void A_damaged_saved_count_never_goes_below_zero()
    {
        var saved = new WaterTally { Day = "2026-11-10", Glasses = -3 };
        Assert.Equal(0, saved.Today(Evening));
        Assert.Equal(1, saved.Add(Evening));
    }
}

public class NudgeTextTests
{
    static readonly DateTime Started = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, "0:20")]
    [InlineData(0.4, "0:20")]
    [InlineData(1, "0:19")]
    [InlineData(10, "0:10")]
    [InlineData(19.2, "0:01")]
    [InlineData(20, "0:00")]
    [InlineData(25, "0:00")]
    public void The_eye_break_counts_down_from_twenty_seconds(double secondsIn, string expected) =>
        Assert.Equal(expected, NudgeText.Countdown(Started, Started.AddSeconds(secondsIn)));

    [Fact]
    public void A_clock_set_back_mid_break_never_shows_more_than_twenty_seconds() =>
        Assert.Equal("0:20", NudgeText.Countdown(Started, Started.AddMinutes(-5)));

    [Theory]
    [InlineData(0, "0 today")]
    [InlineData(2, "2 today")]
    [InlineData(12, "12 today")]
    [InlineData(-1, "0 today")]
    public void Water_says_how_many_glasses_today(int glasses, string expected) =>
        Assert.Equal(expected, NudgeText.Glasses(glasses));

    [Fact]
    public void The_words_fit_beside_the_pet()
    {
        Assert.True(NudgeText.EyeLabel.Length <= 12);
        Assert.True(NudgeText.WaterLabel.Length <= 12);
        Assert.True(NudgeText.Glasses(100).Length <= 9);
    }

    [Theory]
    [InlineData("eye breaks on", NudgeCommand.EyeBreaksOn)]
    [InlineData("Eye breaks off.", NudgeCommand.EyeBreaksOff)]
    [InlineData("turn on eye breaks", NudgeCommand.EyeBreaksOn)]
    [InlineData("turn eye breaks off", NudgeCommand.EyeBreaksOff)]
    [InlineData("turn off the eye breaks", NudgeCommand.EyeBreaksOff)]
    [InlineData("water reminders on", NudgeCommand.WaterOn)]
    [InlineData("Water reminders, off", NudgeCommand.WaterOff)]
    [InlineData("water reminder on", NudgeCommand.WaterOn)]
    [InlineData("turn off water reminders", NudgeCommand.WaterOff)]
    [InlineData("take an eye break", NudgeCommand.EyeBreakNow)]
    [InlineData("Take an eye break!", NudgeCommand.EyeBreakNow)]
    [InlineData("eye break", NudgeCommand.EyeBreakNow)]
    [InlineData("eye break now", NudgeCommand.EyeBreakNow)]
    public void It_understands_the_nudge_commands(string text, NudgeCommand expected) =>
        Assert.Equal(expected, NudgeText.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("water")]
    [InlineData("eye")]
    [InlineData("how much water should I drink")]
    [InlineData("eye strain breaks")]
    [InlineData("water reminders")]
    [InlineData("eye breaks")]
    [InlineData("turn on the lights")]
    [InlineData("water reminders on android phones")]
    [InlineData("take a break")]
    [InlineData("on")]
    public void Anything_else_stays_a_search(string text) =>
        Assert.Null(NudgeText.Parse(text));
}
