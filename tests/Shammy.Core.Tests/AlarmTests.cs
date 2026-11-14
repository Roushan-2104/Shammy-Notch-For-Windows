using Shammy.Core.Productivity;
using Shammy.Core.Search;

namespace Shammy.Core.Tests;

/// <summary>Zones built here so the tests don't depend on the PC's time zone database.</summary>
static class TestZones
{
    /// <summary>UTC+0 with EU-style summer time: +1 from 02:00 on the last Sunday of March to 03:00 on the last Sunday of October.</summary>
    public static readonly TimeZoneInfo Dst = Create();

    public static readonly TimeZoneInfo India = TimeZoneInfo.CreateCustomTimeZone("Test IST", TimeSpan.FromHours(5.5), "Test IST", "Test IST");

    static TimeZoneInfo Create()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2000, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone("Test DST", TimeSpan.Zero, "Test DST", "Test Standard", "Test Summer", new[] { rule });
    }

    public static DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    /// <summary>A local India wall-clock time as UTC (India has no DST, so it's just −5:30).</summary>
    public static DateTime Ist(int y, int mo, int d, int h, int mi = 0) => new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Utc).AddHours(-5.5);
}

public class AlarmScheduleTests
{
    static readonly TimeZoneInfo India = TestZones.India;

    static Alarm Make(int hour, int minute, AlarmDays days = AlarmDays.None, DateTime? date = null, DateTime? since = null) => new()
    {
        Hour = hour,
        Minute = minute,
        Days = days,
        Date = date,
        SinceUtc = since ?? TestZones.Ist(2026, 11, 1, 0),
    };

    [Fact]
    public void Weekday_alarm_on_friday_morning_next_rings_on_monday()
    {
        // 6 Nov 2026 is a Friday.
        var a = Make(7, 0, AlarmDays.Weekdays);
        var next = AlarmSchedule.NextOccurrenceUtc(a, TestZones.Ist(2026, 11, 6, 8), India);
        Assert.Equal(TestZones.Ist(2026, 11, 9, 7), next);
    }

    [Fact]
    public void Weekend_alarm_skips_the_week()
    {
        var a = Make(9, 30, AlarmDays.Weekends);
        Assert.Equal(TestZones.Ist(2026, 11, 7, 9, 30), AlarmSchedule.NextOccurrenceUtc(a, TestZones.Ist(2026, 11, 4, 10), India));
        Assert.Equal(TestZones.Ist(2026, 11, 8, 9, 30), AlarmSchedule.NextOccurrenceUtc(a, TestZones.Ist(2026, 11, 7, 9, 30), India));
    }

    [Fact]
    public void Chosen_days_ring_on_those_days_only()
    {
        var a = Make(6, 0, AlarmDays.Monday | AlarmDays.Wednesday | AlarmDays.Friday);
        // Wednesday 4 Nov after 6:00 → Friday 6 Nov; Friday after → Monday 9 Nov.
        Assert.Equal(TestZones.Ist(2026, 11, 6, 6), AlarmSchedule.NextOccurrenceUtc(a, TestZones.Ist(2026, 11, 4, 10), India));
        Assert.Equal(TestZones.Ist(2026, 11, 9, 6), AlarmSchedule.NextOccurrenceUtc(a, TestZones.Ist(2026, 11, 6, 6), India));
        // Wednesday before 6:00 → today.
        Assert.Equal(TestZones.Ist(2026, 11, 4, 6), AlarmSchedule.NextOccurrenceUtc(a, TestZones.Ist(2026, 11, 4, 5, 59), India));
    }

    [Fact]
    public void Midnight_alarm_is_the_start_of_the_next_day()
    {
        var a = Make(0, 0, AlarmDays.EveryDay);
        Assert.Equal(TestZones.Ist(2026, 11, 5, 0), AlarmSchedule.NextOccurrenceUtc(a, TestZones.Ist(2026, 11, 4, 23, 59), India));
        // Exactly at midnight it's the next one (strictly after).
        Assert.Equal(TestZones.Ist(2026, 11, 6, 0), AlarmSchedule.NextOccurrenceUtc(a, TestZones.Ist(2026, 11, 5, 0), India));
    }

    [Fact]
    public void One_off_without_a_date_rings_the_next_time_the_clock_shows_it()
    {
        // Set at 23:45 for 23:30: tomorrow night. Set at 23:45 for 0:15: in half an hour.
        var since = TestZones.Ist(2026, 11, 4, 23, 45);
        Assert.Equal(TestZones.Ist(2026, 11, 5, 23, 30), AlarmSchedule.NextOccurrenceUtc(Make(23, 30, since: since), since, India));
        Assert.Equal(TestZones.Ist(2026, 11, 5, 0, 15), AlarmSchedule.NextOccurrenceUtc(Make(0, 15, since: since), since, India));
    }

    [Fact]
    public void One_off_with_a_date_rings_then_is_spent()
    {
        var a = Make(7, 0, date: new DateTime(2026, 11, 5));
        var at = TestZones.Ist(2026, 11, 5, 7);
        Assert.Equal(at, AlarmSchedule.NextOccurrenceUtc(a, a.SinceUtc, India));
        var check = AlarmSchedule.Check(a, at.AddSeconds(1), India);
        Assert.Equal(AlarmDecision.Ring, check.Decision);
        AlarmSchedule.StartRinging(a, check.OccurrenceUtc, at.AddSeconds(1));
        AlarmSchedule.Stop(a);
        Assert.False(a.Enabled);
        Assert.Null(AlarmSchedule.NextOccurrenceUtc(a, a.SinceUtc, India));
        Assert.Equal(AlarmDecision.None, AlarmSchedule.Check(a, at.AddDays(1), India).Decision);
    }

    [Fact]
    public void One_off_in_the_past_never_rings()
    {
        var a = Make(7, 0, date: new DateTime(2026, 11, 1), since: TestZones.Ist(2026, 11, 2, 0));
        Assert.Null(AlarmSchedule.NextOccurrenceUtc(a, a.SinceUtc, India));
    }

    [Fact]
    public void Spring_forward_gap_rings_when_the_clocks_jump()
    {
        // 29 Mar 2026: 02:00 becomes 03:00, so 02:30 doesn't exist that night; it rings at 03:00 local (02:00 UTC), the jump itself.
        var zone = TestZones.Dst;
        var a = Make(2, 30, AlarmDays.EveryDay, since: TestZones.Utc(2026, 3, 28, 12));
        var first = AlarmSchedule.NextOccurrenceUtc(a, a.SinceUtc, zone);
        Assert.Equal(TestZones.Utc(2026, 3, 29, 2), first);
        // The night after, 02:30 summer time is 01:30 UTC.
        Assert.Equal(TestZones.Utc(2026, 3, 30, 1, 30), AlarmSchedule.NextOccurrenceUtc(a, first!.Value, zone));
    }

    [Fact]
    public void Seven_am_stays_seven_am_across_the_change()
    {
        var zone = TestZones.Dst;
        var a = Make(7, 0, AlarmDays.EveryDay, since: TestZones.Utc(2026, 3, 28, 12));
        // Before the change 07:00 is 07:00 UTC; after it, 06:00 UTC.
        Assert.Equal(TestZones.Utc(2026, 3, 29, 6), AlarmSchedule.NextOccurrenceUtc(a, TestZones.Utc(2026, 3, 28, 12), zone));
        Assert.Equal(TestZones.Utc(2026, 10, 25, 7), AlarmSchedule.NextOccurrenceUtc(a, TestZones.Utc(2026, 10, 24, 12), zone));
    }

    [Fact]
    public void Fall_back_repeat_rings_only_the_first_time()
    {
        // 25 Oct 2026: 03:00 summer becomes 02:00 winter, so 02:30 happens twice (01:30 UTC, then 02:30 UTC).
        var zone = TestZones.Dst;
        var a = Make(2, 30, AlarmDays.EveryDay, since: TestZones.Utc(2026, 10, 24, 12));
        var first = AlarmSchedule.NextOccurrenceUtc(a, a.SinceUtc, zone);
        Assert.Equal(TestZones.Utc(2026, 10, 25, 1, 30), first);

        var check = AlarmSchedule.Check(a, first!.Value, zone);
        Assert.Equal(AlarmDecision.Ring, check.Decision);
        AlarmSchedule.StartRinging(a, check.OccurrenceUtc, first.Value);
        AlarmSchedule.Stop(a);
        // The second 02:30 (02:30 UTC) is the same alarm already handled.
        Assert.Equal(AlarmDecision.None, AlarmSchedule.Check(a, TestZones.Utc(2026, 10, 25, 2, 30), zone).Decision);
        Assert.Equal(TestZones.Utc(2026, 10, 26, 2, 30), AlarmSchedule.NextOccurrenceUtc(a, a.LastOccurrenceUtc!.Value, zone));
    }

    [Fact]
    public void Snooze_rings_again_after_its_length_and_keeps_the_occurrence()
    {
        var a = Make(7, 0, AlarmDays.EveryDay);
        var seven = TestZones.Ist(2026, 11, 4, 7);
        var c = AlarmSchedule.Check(a, seven, India);
        Assert.Equal(AlarmDecision.Ring, c.Decision);
        AlarmSchedule.StartRinging(a, c.OccurrenceUtc, seven);
        Assert.True(a.IsRinging);

        AlarmSchedule.Snooze(a, seven.AddMinutes(1), AlarmSchedule.SnoozeLength(a, 10), auto: false);
        Assert.Equal(seven.AddMinutes(11), a.SnoozedUntilUtc);
        Assert.Equal(seven.AddMinutes(11), AlarmSchedule.UpcomingUtc(a, seven.AddMinutes(2), India));
        Assert.Equal(AlarmDecision.None, AlarmSchedule.Check(a, seven.AddMinutes(10), India).Decision);

        var again = AlarmSchedule.Check(a, seven.AddMinutes(11), India);
        Assert.Equal(AlarmDecision.Ring, again.Decision);
        AlarmSchedule.StartRinging(a, again.OccurrenceUtc, seven.AddMinutes(11));
        Assert.Equal(seven, a.LastOccurrenceUtc);
        Assert.Null(a.SnoozedUntilUtc);
        Assert.Equal(0, a.AutoSnoozes);
    }

    [Theory]
    [InlineData(null, 10, 10)]
    [InlineData(5, 10, 5)]
    [InlineData(15, 10, 15)]
    [InlineData(0, 10, 1)]
    [InlineData(null, 500, 60)]
    public void Snooze_length_uses_the_alarm_then_the_setting(int? own, int setting, int expected)
    {
        var a = Make(7, 0);
        a.SnoozeMinutes = own;
        Assert.Equal(expected, AlarmSchedule.SnoozeLength(a, setting));
    }

    [Fact]
    public void Unanswered_alarm_snoozes_itself_three_times_then_is_missed()
    {
        var a = Make(7, 0, AlarmDays.EveryDay);
        var t = TestZones.Ist(2026, 11, 4, 7);
        AlarmSchedule.StartRinging(a, t, t);
        for (int round = 0; round < AlarmSchedule.MaxAutoSnoozes; round++)
        {
            Assert.Equal(AlarmDecision.KeepRinging, AlarmSchedule.Check(a, t + TimeSpan.FromMinutes(4.9), India).Decision);
            t += AlarmSchedule.RingTimeout;
            Assert.Equal(AlarmDecision.AutoSnooze, AlarmSchedule.Check(a, t, India).Decision);
            AlarmSchedule.Snooze(a, t, 10, auto: true);
            t += TimeSpan.FromMinutes(10);
            var ring = AlarmSchedule.Check(a, t, India);
            Assert.Equal(AlarmDecision.Ring, ring.Decision);
            AlarmSchedule.StartRinging(a, ring.OccurrenceUtc, t);
        }
        Assert.Equal(3, a.AutoSnoozes);
        var last = AlarmSchedule.Check(a, t + AlarmSchedule.RingTimeout, India);
        Assert.Equal(AlarmDecision.Missed, last.Decision);
        AlarmSchedule.MarkMissed(a, last.OccurrenceUtc);
        Assert.Equal(TestZones.Ist(2026, 11, 4, 7), a.MissedUtc);
        Assert.True(a.Enabled); // a repeating alarm stays on for tomorrow
        Assert.Equal(TestZones.Ist(2026, 11, 5, 7), AlarmSchedule.UpcomingUtc(a, t, India));
    }

    [Fact]
    public void Stopping_resets_the_auto_snooze_count()
    {
        var a = Make(7, 0, AlarmDays.EveryDay);
        var t = TestZones.Ist(2026, 11, 4, 7);
        AlarmSchedule.StartRinging(a, t, t);
        AlarmSchedule.Snooze(a, t.AddMinutes(5), 10, auto: true);
        AlarmSchedule.StartRinging(a, null, t.AddMinutes(15));
        AlarmSchedule.Stop(a);
        Assert.Equal(0, a.AutoSnoozes);
        Assert.False(a.IsRinging);
        Assert.True(a.Enabled);
    }

    [Theory]
    [InlineData(0, AlarmDecision.Ring)]
    [InlineData(9, AlarmDecision.Ring)]
    [InlineData(10, AlarmDecision.Ring)]
    [InlineData(11, AlarmDecision.Missed)]
    [InlineData(120, AlarmDecision.Missed)]
    public void After_sleep_a_late_alarm_still_rings_within_ten_minutes(int minutesLate, AlarmDecision expected)
    {
        var a = Make(7, 0, AlarmDays.EveryDay);
        var c = AlarmSchedule.Check(a, TestZones.Ist(2026, 11, 4, 7).AddMinutes(minutesLate), India);
        Assert.Equal(expected, c.Decision);
        Assert.Equal(TestZones.Ist(2026, 11, 4, 7), c.OccurrenceUtc);
    }

    [Fact]
    public void Missed_alarm_is_reported_once_and_tomorrows_still_rings()
    {
        var a = Make(7, 0, AlarmDays.EveryDay);
        var wake = TestZones.Ist(2026, 11, 4, 8);
        var c = AlarmSchedule.Check(a, wake, India);
        Assert.Equal(AlarmDecision.Missed, c.Decision);
        AlarmSchedule.MarkMissed(a, c.OccurrenceUtc);
        Assert.Equal(AlarmText.Missed(a), $"Missed alarm {ClockPreference.Format(7, 0)}");
        Assert.Equal(AlarmDecision.None, AlarmSchedule.Check(a, wake.AddMinutes(1), India).Decision);
        Assert.Equal(AlarmDecision.Ring, AlarmSchedule.Check(a, TestZones.Ist(2026, 11, 5, 7), India).Decision);
    }

    [Fact]
    public void Missed_one_off_turns_itself_off()
    {
        var a = Make(6, 30, date: new DateTime(2026, 11, 4));
        var c = AlarmSchedule.Check(a, TestZones.Ist(2026, 11, 4, 9), India);
        Assert.Equal(AlarmDecision.Missed, c.Decision);
        AlarmSchedule.MarkMissed(a, c.OccurrenceUtc);
        Assert.False(a.Enabled);
    }

    [Fact]
    public void Long_sleep_reports_only_the_latest_occurrence()
    {
        var a = Make(7, 0, AlarmDays.EveryDay, since: TestZones.Ist(2026, 11, 1, 0));
        // Asleep from the 1st to the 4th at 7:05: today's still rings, the older ones are skipped.
        var ring = AlarmSchedule.Check(a, TestZones.Ist(2026, 11, 4, 7, 5), India);
        Assert.Equal(AlarmDecision.Ring, ring.Decision);
        Assert.Equal(TestZones.Ist(2026, 11, 4, 7), ring.OccurrenceUtc);
        // At 9:00 it's missed, once.
        var missed = AlarmSchedule.Check(a, TestZones.Ist(2026, 11, 4, 9), India);
        Assert.Equal(AlarmDecision.Missed, missed.Decision);
        Assert.Equal(TestZones.Ist(2026, 11, 4, 7), missed.OccurrenceUtc);
    }

    [Fact]
    public void Sleeping_through_a_ring_or_a_snooze_is_a_miss()
    {
        var a = Make(7, 0, AlarmDays.EveryDay);
        var t = TestZones.Ist(2026, 11, 4, 7);
        AlarmSchedule.StartRinging(a, t, t);
        Assert.Equal(AlarmDecision.Missed, AlarmSchedule.Check(a, t.AddMinutes(40), India).Decision);

        var b = Make(7, 0, AlarmDays.EveryDay);
        AlarmSchedule.StartRinging(b, t, t);
        AlarmSchedule.Snooze(b, t.AddMinutes(1), 10, auto: false);
        Assert.Equal(AlarmDecision.Ring, AlarmSchedule.Check(b, t.AddMinutes(15), India).Decision);
        Assert.Equal(AlarmDecision.Missed, AlarmSchedule.Check(b, t.AddMinutes(30), India).Decision);
    }

    [Fact]
    public void Restart_while_ringing_picks_the_ring_up_again()
    {
        var a = Make(7, 0, AlarmDays.EveryDay);
        var t = TestZones.Ist(2026, 11, 4, 7);
        AlarmSchedule.StartRinging(a, t, t);
        Assert.Equal(AlarmDecision.KeepRinging, AlarmSchedule.Check(a, t.AddMinutes(2), India).Decision);
    }

    [Fact]
    public void Turned_off_alarm_never_rings()
    {
        var a = Make(7, 0, AlarmDays.EveryDay);
        a.Enabled = false;
        Assert.Equal(AlarmDecision.None, AlarmSchedule.Check(a, TestZones.Ist(2026, 11, 4, 7), India).Decision);
        Assert.Null(AlarmSchedule.UpcomingUtc(a, TestZones.Ist(2026, 11, 4, 6), India));
    }

    [Fact]
    public void Reset_counts_from_now()
    {
        var a = Make(7, 0, AlarmDays.EveryDay);
        AlarmSchedule.MarkMissed(a, TestZones.Ist(2026, 11, 3, 7));
        AlarmSchedule.Reset(a, TestZones.Ist(2026, 11, 4, 7, 30));
        Assert.Null(a.MissedUtc);
        Assert.Equal(AlarmDecision.None, AlarmSchedule.Check(a, TestZones.Ist(2026, 11, 4, 7, 31), India).Decision);
        Assert.Equal(TestZones.Ist(2026, 11, 5, 7), AlarmSchedule.UpcomingUtc(a, TestZones.Ist(2026, 11, 4, 7, 31), India));
    }

    [Fact]
    public void Next_wake_is_the_earliest_thing_to_look_at()
    {
        var now = TestZones.Ist(2026, 11, 4, 6);
        var later = Make(9, 0, AlarmDays.EveryDay, since: now);
        var soon = Make(6, 30, AlarmDays.EveryDay, since: now);
        var off = Make(6, 5, AlarmDays.EveryDay, since: now);
        off.Enabled = false;
        Assert.Equal(TestZones.Ist(2026, 11, 4, 6, 30), AlarmSchedule.NextWakeUtc(new[] { later, soon, off }, now, India));

        var ringing = Make(5, 58, AlarmDays.EveryDay);
        AlarmSchedule.StartRinging(ringing, TestZones.Ist(2026, 11, 4, 5, 58), TestZones.Ist(2026, 11, 4, 5, 58));
        Assert.Equal(TestZones.Ist(2026, 11, 4, 6, 3), AlarmSchedule.NextWakeUtc(new[] { later, soon, ringing }, now, India));
        Assert.Null(AlarmSchedule.NextWakeUtc(new[] { off }, now, India));
    }

    [Fact]
    public void Target_matches_by_time()
    {
        var seven = Make(7, 0);
        var nineteen = Make(19, 0);
        Assert.True(new AlarmTarget(7, 0, Exact: true).Matches(seven));
        Assert.False(new AlarmTarget(7, 0, Exact: true).Matches(nineteen));
        Assert.True(new AlarmTarget(7, 0, Exact: false).Matches(nineteen));
        Assert.False(new AlarmTarget(7, 30, Exact: false).Matches(seven));
    }
}

public class AlarmTextTests
{
    [Theory]
    [InlineData(AlarmDays.EveryDay, "Every day")]
    [InlineData(AlarmDays.Weekdays, "Weekdays")]
    [InlineData(AlarmDays.Weekends, "Weekends")]
    [InlineData(AlarmDays.Monday | AlarmDays.Wednesday | AlarmDays.Friday, "Mon, Wed, Fri")]
    [InlineData(AlarmDays.Sunday | AlarmDays.Monday, "Mon, Sun")]
    [InlineData(AlarmDays.None, "Once")]
    public void Repeat_names(AlarmDays days, string expected) => Assert.Equal(expected, AlarmText.Repeat(days));

    [Theory]
    [InlineData(7, 0, false, "7:00 AM")]
    [InlineData(0, 5, false, "12:05 AM")]
    [InlineData(12, 0, false, "12:00 PM")]
    [InlineData(19, 30, false, "7:30 PM")]
    [InlineData(7, 0, true, "07:00")]
    [InlineData(19, 30, true, "19:30")]
    public void Clock_follows_the_12_or_24_hour_choice(int h, int m, bool use24, string expected) =>
        Assert.Equal(expected, ClockPreference.Format(h, m, use24));

    [Fact]
    public void Culture_decides_when_not_set()
    {
        Assert.False(ClockPreference.CultureUses24Hour(new System.Globalization.CultureInfo("en-US", false)));
        Assert.True(ClockPreference.CultureUses24Hour(new System.Globalization.CultureInfo("de-DE", false)));
    }

    [Theory]
    [InlineData(0.2, "in 1 min")]
    [InlineData(45, "in 45 min")]
    [InlineData(60, "in 1 h")]
    [InlineData(500, "in 8 h 20 min")]
    [InlineData(3 * 24 * 60, "in 3 days")]
    public void Until(double minutes, string expected) => Assert.Equal(expected, AlarmText.Until(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void One_off_reads_as_its_day()
    {
        var now = new DateTime(2026, 11, 4, 22, 0, 0);
        var a = new Alarm { Hour = 7, Date = new DateTime(2026, 11, 5) };
        Assert.Equal("Tomorrow", AlarmText.Repeat(a, now, null));
        a.Date = new DateTime(2026, 11, 20);
        Assert.Equal("Fri 20 Nov", AlarmText.Repeat(a, now, null));
    }
}

public class AlarmParserTests
{
    // Tuesday 10 March 2026, 3 PM.
    static readonly DateTime Now = new(2026, 3, 10, 15, 0, 0);

    static AlarmSpec Spec(string text)
    {
        var c = ProductivityParser.Parse(text, Now);
        Assert.NotNull(c);
        Assert.Equal(ProductivityAction.SetAlarm, c!.Action);
        return c.Alarm!;
    }

    [Theory]
    [InlineData("set an alarm for 7 am", 7, 0, 2026, 3, 11)]
    [InlineData("alarm at 7 tomorrow", 7, 0, 2026, 3, 11)]
    [InlineData("Wake me up at 6:30 am.", 6, 30, 2026, 3, 11)]
    [InlineData("set alarm for 5 pm", 17, 0, 2026, 3, 10)]
    [InlineData("alarm in 20 minutes", 15, 20, 2026, 3, 10)]
    [InlineData("wake me up at 7", 7, 0, 2026, 3, 11)]
    [InlineData("set a 6:45am alarm", 6, 45, 2026, 3, 11)]
    [InlineData("alarm at 9", 9, 0, 2026, 3, 11)]
    [InlineData("alarm at 3:30", 15, 30, 2026, 3, 10)]
    [InlineData("alarm at 4", 4, 0, 2026, 3, 11)]
    [InlineData("alarm for 2 tomorrow", 14, 0, 2026, 3, 11)]
    [InlineData("alarm tonight at 10", 22, 0, 2026, 3, 10)]
    [InlineData("alarm on friday at 6", 6, 0, 2026, 3, 13)]
    [InlineData("kal subah 7 baje ka alarm laga do", 7, 0, 2026, 3, 11)]
    [InlineData("Kal Subah Saat Bajay Ka Alam Lagado.", 7, 0, 2026, 3, 11)]
    [InlineData("Kal subah 06:30 baje ka alarm laga do", 6, 30, 2026, 3, 11)]
    [InlineData("aaj raat 10 baje alarm set karo", 22, 0, 2026, 3, 10)]
    public void One_off_alarms(string text, int hour, int minute, int y, int mo, int d)
    {
        var s = Spec(text);
        Assert.Equal((hour, minute), (s.Hour, s.Minute));
        Assert.Equal(AlarmDays.None, s.Days);
        Assert.Equal(new DateTime(y, mo, d), s.Date);
    }

    [Theory]
    [InlineData("alarm at 6:30 every weekday", 6, 30, AlarmDays.Weekdays)]
    [InlineData("wake me up at 7 on weekends", 7, 0, AlarmDays.Weekends)]
    [InlineData("set an alarm for 6 am every day", 6, 0, AlarmDays.EveryDay)]
    [InlineData("alarm daily at 5:45", 5, 45, AlarmDays.EveryDay)]
    [InlineData("alarm at 7 on weekdays", 7, 0, AlarmDays.Weekdays)]
    [InlineData("alarm every monday and wednesday at 6", 6, 0, AlarmDays.Monday | AlarmDays.Wednesday)]
    [InlineData("alarm at 8 on mondays", 8, 0, AlarmDays.Monday)]
    [InlineData("alarm at 7 am mon, wed, fri", 7, 0, AlarmDays.Monday | AlarmDays.Wednesday | AlarmDays.Friday)]
    [InlineData("alarm at 7 every tues and thurs", 7, 0, AlarmDays.Tuesday | AlarmDays.Thursday)]
    [InlineData("alarm at 6 monday to friday", 6, 0, AlarmDays.Weekdays)]
    [InlineData("alarm at 6 monday-thursday", 6, 0, AlarmDays.Monday | AlarmDays.Tuesday | AlarmDays.Wednesday | AlarmDays.Thursday)]
    [InlineData("set an alarm every weekday at 9 pm", 21, 0, AlarmDays.Weekdays)]
    [InlineData("roz subah 7 baje ka alarm laga do", 7, 0, AlarmDays.EveryDay)]
    public void Repeating_alarms(string text, int hour, int minute, AlarmDays days)
    {
        var s = Spec(text);
        Assert.Equal((hour, minute), (s.Hour, s.Minute));
        Assert.Equal(days, s.Days);
        Assert.Null(s.Date);
    }

    [Theory]
    [InlineData("alarm at 7 for gym", "Gym")]
    [InlineData("wake me up at 6 to go running", "Go running")]
    [InlineData("alarm at 6:30 every weekday called standup prep", "Standup prep")]
    [InlineData("set an alarm for 7 am", "")]
    public void Labels(string text, string label) => Assert.Equal(label, Spec(text).Label);

    [Theory]
    [InlineData("alarm on monday at 7", 16)]
    [InlineData("alarm at 7 on thursday", 12)]
    public void Singular_day_is_a_one_off(string text, int day)
    {
        var s = Spec(text);
        Assert.Equal(AlarmDays.None, s.Days);
        Assert.Equal(new DateTime(2026, 3, day), s.Date);
    }

    [Theory]
    [InlineData("alarm clock app")]
    [InlineData("alarm sounds")]
    [InlineData("best alarm clock")]
    [InlineData("alarm")]
    [InlineData("alarm laga do gaana")]
    [InlineData("cancel the alarm clock subscription for my phone")]
    public void Searches_stay_searches(string text) => Assert.Null(ProductivityParser.Parse(text, Now));

    [Theory]
    [InlineData("set an alarm")]
    [InlineData("set an alarm every weekday")]
    public void Without_a_time_it_asks_for_one(string text) =>
        Assert.Equal(ProductivityAction.ReminderNeedsTime, ProductivityParser.Parse(text, Now)!.Action);

    [Fact]
    public void Repeating_alarm_in_twenty_minutes_makes_no_sense() =>
        Assert.NotEqual(ProductivityAction.SetAlarm, ProductivityParser.Parse("alarm in 20 minutes every day", Now)?.Action);

    [Theory]
    [InlineData("alarms")]
    [InlineData("show my alarms")]
    [InlineData("open alarms")]
    [InlineData("my alarms")]
    [InlineData("list all alarms")]
    public void Show(string text) => Assert.Equal(ProductivityAction.ShowAlarms, ProductivityParser.Parse(text, Now)!.Action);

    [Theory]
    [InlineData("delete the 7 am alarm", ProductivityAction.DeleteAlarm, 7, 0, true)]
    [InlineData("remove my 6:30 alarm", ProductivityAction.DeleteAlarm, 6, 30, false)]
    [InlineData("cancel my alarm for 7", ProductivityAction.DeleteAlarm, 7, 0, false)]
    [InlineData("turn off the 7 am alarm", ProductivityAction.DisableAlarms, 7, 0, true)]
    [InlineData("turn on the 19:00 alarm", ProductivityAction.EnableAlarms, 19, 0, true)]
    public void Manage_by_time(string text, ProductivityAction action, int hour, int minute, bool exact)
    {
        var c = ProductivityParser.Parse(text, Now)!;
        Assert.Equal(action, c.Action);
        Assert.Equal(new AlarmTarget(hour, minute, exact), c.Target);
        Assert.False(c.All);
    }

    [Theory]
    [InlineData("turn off my alarms", ProductivityAction.DisableAlarms)]
    [InlineData("turn off all alarms", ProductivityAction.DisableAlarms)]
    [InlineData("disable alarms", ProductivityAction.DisableAlarms)]
    [InlineData("delete all my alarms", ProductivityAction.DeleteAlarm)]
    [InlineData("turn on my alarms", ProductivityAction.EnableAlarms)]
    public void Manage_all(string text, ProductivityAction action)
    {
        var c = ProductivityParser.Parse(text, Now)!;
        Assert.Equal(action, c.Action);
        Assert.True(c.All);
        Assert.Null(c.Target);
    }

    [Fact]
    public void Manage_by_label()
    {
        var c = ProductivityParser.Parse("delete the gym alarm", Now)!;
        Assert.Equal(ProductivityAction.DeleteAlarm, c.Action);
        Assert.Equal("gym", c.Text);
    }

    [Theory]
    [InlineData("stop the alarm", ProductivityAction.StopAlarm, 0)]
    [InlineData("dismiss alarm", ProductivityAction.StopAlarm, 0)]
    [InlineData("snooze", ProductivityAction.SnoozeAlarm, 0)]
    [InlineData("snooze the alarm", ProductivityAction.SnoozeAlarm, 0)]
    [InlineData("snooze for 5 minutes", ProductivityAction.SnoozeAlarm, 5)]
    [InlineData("snooze 15", ProductivityAction.SnoozeAlarm, 15)]
    public void While_ringing(string text, ProductivityAction action, int minutes)
    {
        var c = ProductivityParser.Parse(text, Now)!;
        Assert.Equal(action, c.Action);
        Assert.Equal(TimeSpan.FromMinutes(minutes), c.Duration);
    }

    [Fact]
    public void Palette_describes_the_alarm()
    {
        var c = ProductivityParser.Parse("alarm at 6:30 every weekday", Now)!;
        var text = Shammy.Core.Palette.PaletteQuery.DescribeProductivity(c, Now)!;
        Assert.Contains("Alarm", text);
        Assert.Contains("Weekdays", text);
        var once = Shammy.Core.Palette.PaletteQuery.DescribeProductivity(ProductivityParser.Parse("set an alarm for 7 am", Now)!, Now)!;
        Assert.EndsWith("tomorrow", once);
    }

    [Theory]
    [InlineData(7, 7)]
    [InlineData(4, 4)]
    [InlineData(11, 11)]
    [InlineData(12, 12)]
    [InlineData(1, 13)]
    [InlineData(3, 15)]
    public void Bare_hours_read_as_alarm_hours(int said, int hour) => Assert.Equal(hour, AlarmParser.AlarmHour(said));
}
