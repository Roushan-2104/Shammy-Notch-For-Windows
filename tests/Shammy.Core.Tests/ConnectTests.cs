using Shammy.Core.Calendar;
using Shammy.Core.Connect;
using Shammy.Core.Devices;
using Shammy.Core.Palette;
using Shammy.Core.Productivity;

namespace Shammy.Core.Tests;

public class QuietRulesTests
{
    static readonly DateTime Now = new(2026, 11, 4, 10, 0, 0);

    static CalendarOccurrence Event(string title, int startMin, int lengthMin, bool link = true, bool allDay = false) =>
        new(title, title, "", Now.AddMinutes(startMin), Now.AddMinutes(startMin + lengthMin), allDay,
            link ? "https://meet.google.com/abc-defg-hij" : null, link ? "Google Meet" : null);

    [Theory]
    [InlineData("Zoom", true)]
    [InlineData("Teams", true)]
    [InlineData("Google Meet", true)]
    [InlineData(" discord ", true)]
    [InlineData("Jitsi Meet", true)]
    [InlineData("Whereby", true)]
    [InlineData("Sound Recorder", false)]
    [InlineData("Chrome", false)]
    [InlineData("OBS Studio", false)]
    [InlineData(null, false)]
    public void Only_call_apps_on_the_mic_mean_a_call(string? app, bool call) => Assert.Equal(call, QuietRules.IsCallApp(app));

    [Theory]
    [InlineData("firefox.exe", "Jitsi Meet — Mozilla Firefox")]
    [InlineData("chrome.exe", "Team sync | Whereby - Google Chrome")]
    public void A_web_call_on_the_mic_is_a_call_like_the_wheel_sees_it(string browser, string title)
    {
        Assert.True(QuietRules.IsCallApp(Shammy.Core.MeetingSystem.AppNames.Friendly(browser, new[] { title })));
        Assert.True(Shammy.Core.Wheel.WheelMoments.IsMeetingInFront(browser[..^4], title));
    }

    [Fact]
    public void Any_call_app_among_several_counts()
    {
        Assert.True(QuietRules.AnyCallApp(new[] { "Sound Recorder", "Zoom" }));
        Assert.False(QuietRules.AnyCallApp(new[] { "Chrome", "Audacity" }));
        Assert.False(QuietRules.AnyCallApp(Array.Empty<string>()));
    }

    [Fact]
    public void A_linked_meeting_in_progress_is_quiet_time()
    {
        var items = new[] { Event("Standup", -5, 15), Event("Lunch", 60, 60, link: false) };
        Assert.Equal("Standup", QuietRules.MeetingNow(items, Now)?.Title);
        // Not yet, already over, no link, all-day: not a meeting now.
        Assert.Null(QuietRules.MeetingNow(new[] { Event("Later", 5, 30) }, Now));
        Assert.Null(QuietRules.MeetingNow(new[] { Event("Done", -30, 30) }, Now));
        Assert.Null(QuietRules.MeetingNow(new[] { Event("Focus block", -5, 60, link: false) }, Now));
        Assert.Null(QuietRules.MeetingNow(new[] { Event("Offsite", -60, 24 * 60, allDay: true) }, Now));
    }

    [Fact]
    public void The_next_change_is_the_current_meetings_end_or_the_next_start()
    {
        Assert.Equal(Now.AddMinutes(10), QuietRules.NextMeetingChange(new[] { Event("Standup", -5, 15), Event("1:1", 30, 30) }, Now));
        Assert.Equal(Now.AddMinutes(30), QuietRules.NextMeetingChange(new[] { Event("1:1", 30, 30), Event("Gym", 20, 30, link: false) }, Now));
        Assert.Null(QuietRules.NextMeetingChange(new[] { Event("Done", -30, 10) }, Now));
    }

    [Fact]
    public void Focus_is_quiet_but_pomodoro_breaks_are_not()
    {
        Assert.True(QuietRules.FocusQuiet(focusClock: true, null));
        Assert.True(QuietRules.FocusQuiet(false, PomodoroPhase.Focus));
        Assert.False(QuietRules.FocusQuiet(false, PomodoroPhase.ShortBreak));
        Assert.False(QuietRules.FocusQuiet(false, PomodoroPhase.LongBreak));
        Assert.False(QuietRules.FocusQuiet(false, null));
    }

    [Fact]
    public void Held_pill_and_reason_texts()
    {
        Assert.Null(QuietRules.HeldText(0));
        Assert.Equal("1 held", QuietRules.HeldText(1));
        Assert.Equal("3 held", QuietRules.HeldText(3));
        Assert.Equal("off", QuietRules.Describe(Array.Empty<QuietReason>()));
        Assert.Equal("call+focus", QuietRules.Describe(new[] { QuietReason.Focus, QuietReason.Call, QuietReason.Focus }));
    }
}

public class PrivacyTextTests
{
    static readonly DateTime Now = new(2026, 11, 4, 10, 28, 0);

    [Fact]
    public void Copied_text_and_reminders_hide_while_presenting()
    {
        Assert.Equal("my password is hunter2", PrivacyText.CopyPreview("my password is hunter2", false));
        Assert.Equal("Copied", PrivacyText.CopyPreview("my password is hunter2", true));
        Assert.Equal("Call the bank", PrivacyText.Reminder("Call the bank", false));
        Assert.Equal("Reminder", PrivacyText.Reminder("Call the bank", true));
    }

    [Fact]
    public void Calendar_glance_and_pill_say_meeting_while_presenting()
    {
        var start = new DateTime(2026, 11, 4, 10, 30, 0);
        Assert.Equal("Standup 10:30", PrivacyText.CalendarGlance("Standup", start, false));
        Assert.Equal("Meeting 10:30", PrivacyText.CalendarGlance("Interview: Priya", start, true));
        var o = new CalendarOccurrence("u", "Interview: Priya", "", start, start.AddMinutes(30), false, null, null);
        Assert.Equal("Interview: Priya in 2 min", PrivacyText.MeetingPill(o, Now, false));
        Assert.Equal("Meeting in 2 min", PrivacyText.MeetingPill(o, Now, true));
    }

    [Fact]
    public void Summaries_count_without_content()
    {
        Assert.Equal("3 notes · 2 reminders", PrivacyText.NotesSummary(3, 2));
        Assert.Equal("1 note", PrivacyText.NotesSummary(1, 0));
        Assert.Equal("1 reminder", PrivacyText.NotesSummary(0, 1));
        Assert.Equal("Nothing saved", PrivacyText.NotesSummary(0, 0));
        Assert.Equal("Empty", PrivacyText.ClipboardSummary(0));
        Assert.Equal("1 item", PrivacyText.ClipboardSummary(1));
        Assert.Equal("12 items", PrivacyText.ClipboardSummary(12));
    }
}

public class MeetingReminderTests
{
    static readonly DateTime Now = new(2026, 11, 4, 9, 0, 0);

    static CalendarOccurrence Event(string title, int hour, int minute = 0, int days = 0) =>
        new(title, title, "", Now.Date.AddDays(days).AddHours(hour).AddMinutes(minute), Now.Date.AddDays(days).AddHours(hour).AddMinutes(minute + 30), false, null, null);

    static readonly CalendarOccurrence[] Day =
    {
        Event("Daily Standup", 10, 30),
        Event("Design review", 14),
        Event("Standup", 10, 30, days: 1),
        new("all", "Offsite", "", Now.Date, Now.Date.AddDays(1), true, null, null),
    };

    [Theory]
    [InlineData("remind me before my next meeting", 5, null)]
    [InlineData("Remind me 10 minutes before the meeting.", 10, null)]
    [InlineData("remind me 1 hour before next call", 60, null)]
    [InlineData("remind me 5 min before standup", 5, "standup")]
    [InlineData("remind me before design review", 5, "design review")]
    [InlineData("hey shammy, remind me fifteen mins before my standup", 15, "standup")]
    [InlineData("remind me an hour before the design review", 60, "design review")]
    public void Parses_how_long_before_and_which_meeting(string text, int minutes, string? target)
    {
        var r = MeetingReminder.Parse(text);
        Assert.NotNull(r);
        Assert.Equal(TimeSpan.FromMinutes(minutes), r!.Before);
        Assert.Equal(target, r.Target);
        Assert.Equal(target is null, r.IsNextMeeting);
    }

    [Theory]
    [InlineData("remind me at 6 pm to call mom")]
    [InlineData("remind me in 5 minutes")]
    [InlineData("before my next meeting")]
    [InlineData("remind me 0 minutes before standup")]
    [InlineData("")]
    public void Other_phrases_are_not_meeting_reminders(string text) => Assert.Null(MeetingReminder.Parse(text));

    [Fact]
    public void Next_meeting_text_after_to_is_kept()
    {
        var r = MeetingReminder.Parse("remind me before my next meeting to send the deck");
        Assert.Equal("send the deck", r!.Text);
        var plan = MeetingReminder.Plan(r, Day, Now)!;
        Assert.Equal("send the deck · Daily Standup at 10:30 AM", plan.Text);
    }

    [Fact]
    public void Plans_the_reminder_before_the_matching_meeting()
    {
        var plan = MeetingReminder.Plan(MeetingReminder.Parse("remind me 5 minutes before standup")!, Day, Now)!;
        Assert.Equal("Daily Standup", plan.Meeting.Title);
        Assert.Equal(Now.Date.AddHours(10).AddMinutes(25), plan.Due);
        Assert.Equal("Daily Standup at 10:30 AM", plan.Text);

        var review = MeetingReminder.Plan(MeetingReminder.Parse("remind me 10 min before design review")!, Day, Now)!;
        Assert.Equal(Now.Date.AddHours(13).AddMinutes(50), review.Due);
    }

    [Fact]
    public void Next_meeting_skips_all_day_and_started_events()
    {
        var at = Now.Date.AddHours(10).AddMinutes(40); // standup started 10 min ago
        var plan = MeetingReminder.Plan(MeetingReminder.Parse("remind me before my next meeting")!, Day, at)!;
        Assert.Equal("Design review", plan.Meeting.Title);
    }

    [Fact]
    public void A_meeting_too_close_gets_its_reminder_now_and_unknown_ones_none()
    {
        var at = Now.Date.AddHours(10).AddMinutes(28);
        var plan = MeetingReminder.Plan(MeetingReminder.Parse("remind me 5 minutes before standup")!, Day, at)!;
        Assert.Equal(at, plan.Due);
        Assert.Null(MeetingReminder.Plan(MeetingReminder.Parse("remind me before the board meeting")!, Day, Now));
        Assert.Null(MeetingReminder.Plan(MeetingReminder.Parse("remind me before my next meeting")!, Array.Empty<CalendarOccurrence>(), Now));
    }
}

public class DayLinksTests
{
    static readonly DateTime Now = new(2026, 11, 4, 11, 0, 0);

    static CalendarOccurrence Event(string title, int hour, int length = 30, int days = 0, bool link = false, bool allDay = false) =>
        new(title, title, "", Now.Date.AddDays(days).AddHours(hour), Now.Date.AddDays(days).AddHours(hour).AddMinutes(length), allDay,
            link ? "https://zoom.us/j/1" : null, link ? "Zoom" : null);

    [Fact]
    public void Upcoming_today_keeps_ongoing_and_later_timed_events()
    {
        var items = new[]
        {
            Event("Standup", 10, 30),          // over
            Event("Review", 10, 90),           // going on
            Event("Lunch", 13),
            Event("Tomorrow", 9, days: 1),
            Event("Holiday", 0, 24 * 60, allDay: true),
            Event("Late", 18),
        };
        Assert.Equal(new[] { "Review", "Lunch", "Late" }, DayLinks.UpcomingToday(items, Now).Select(o => o.Title));
        Assert.Equal(new[] { "Review" }, DayLinks.UpcomingToday(items, Now, max: 1).Select(o => o.Title));
    }

    [Fact]
    public void Reminders_today_are_open_ones_by_time()
    {
        var reminders = new[]
        {
            (Text: "pay rent", Due: Now.AddHours(3), Done: false),
            (Text: "call mom", Due: Now.AddHours(-1), Done: false),
            (Text: "done one", Due: Now.AddHours(1), Done: true),
            (Text: "tomorrow", Due: Now.AddDays(1), Done: false),
        };
        var today = DayLinks.RemindersToday(reminders, r => r.Due, r => r.Done, Now);
        Assert.Equal(new[] { "call mom", "pay rent" }, today.Select(r => r.Text));
    }

    [Fact]
    public void Event_meta_shows_time_or_now_and_the_provider()
    {
        Assert.Equal("1:00 PM · Zoom", DayLinks.EventMeta(Event("Sync", 13, link: true), Now));
        Assert.Equal("now", DayLinks.EventMeta(Event("Review", 10, 90), Now));
    }
}

public class EarbudAlertsTests
{
    const string Buds = "25BF04C71669";

    [Fact]
    public void Alerts_once_at_20_and_once_at_10_per_discharge()
    {
        var a = new EarbudAlerts();
        Assert.Null(a.Observe(Buds, 60));
        Assert.Null(a.Observe(Buds, 21));
        Assert.Equal(20, a.Observe(Buds, 18));
        Assert.Null(a.Observe(Buds, 17));
        Assert.Null(a.Observe(Buds, 20)); // a wobble back up doesn't re-alert
        Assert.Null(a.Observe(Buds, 19));
        Assert.Equal(10, a.Observe(Buds, 10));
        Assert.Null(a.Observe(Buds, 6));
    }

    [Fact]
    public void Connecting_already_low_alerts_once_at_the_lower_threshold()
    {
        var a = new EarbudAlerts();
        Assert.Equal(10, a.Observe(Buds, 8));
        Assert.Null(a.Observe(Buds, 18));
    }

    [Fact]
    public void Charging_rearms_the_thresholds()
    {
        var a = new EarbudAlerts();
        Assert.Equal(20, a.Observe(Buds, 19));
        Assert.Equal(10, a.Observe(Buds, 9));
        Assert.Null(a.Observe(Buds, 16));       // 10 re-armed, 20 not yet
        Assert.Equal(10, a.Observe(Buds, 10));
        Assert.Null(a.Observe(Buds, 80));       // fully charged
        Assert.Equal(20, a.Observe(Buds, 20));
        Assert.Null(a.Observe("OTHER", 50));    // devices are tracked separately
        Assert.Null(a.Observe(Buds, 150));      // nonsense readings are ignored
    }

    [Fact]
    public void Pill_text()
    {
        Assert.Equal("Airdopes 311 Pro · 18% · charge soon", EarbudAlerts.Pill("Airdopes 311 Pro", 18, 20));
        Assert.Equal("Airdopes 311 Pro · 9% · charge now", EarbudAlerts.Pill("Airdopes 311 Pro", 9, 10));
    }
}

public class ConnectTextTests
{
    [Fact]
    public void Download_shows_percent_only_with_a_known_total()
    {
        Assert.Equal("42%", ConnectText.DownloadProgress(42_000, 100_000));
        Assert.Equal("100%", ConnectText.DownloadProgress(100_000, 100_000));
        Assert.Equal("11.8 MB", ConnectText.DownloadProgress(12_400_000, null));
        Assert.Equal("11.8 MB", ConnectText.DownloadProgress(12_400_000, 0));
        // The temp file outgrew the reported size: the size can't be trusted, show bytes.
        Assert.Equal("195 KB", ConnectText.DownloadProgress(200_000, 100_000));
    }

    [Fact]
    public void Notification_reminder_text_names_the_app_and_flattens()
    {
        Assert.Equal("WhatsApp · Mom: are you\ncoming?".Replace("\n", " "), ConnectText.NotificationReminder("WhatsApp", "Mom", "are you\ncoming?"));
        Assert.Equal("Outlook · Invoice due", ConnectText.NotificationReminder("Outlook", "Invoice due", ""));
        Assert.Equal("Notification", ConnectText.NotificationReminder("", "", ""));
        var longText = ConnectText.NotificationReminder("Slack", new string('x', 300), "", max: 40);
        Assert.Equal(40, longText.Length);
        Assert.EndsWith("…", longText);
    }

    [Fact]
    public void Notes_from_text_are_trimmed_and_capped()
    {
        Assert.Null(ConnectText.NoteFrom("   "));
        Assert.Equal("line one\nline two", ConnectText.NoteFrom("  line one\r\nline two \n"));
        Assert.Equal(10, ConnectText.NoteFrom(new string('a', 50), max: 10)!.Length);
    }
}

public class PaletteSourcesTests
{
    [Theory]
    [InlineData("calendar", "calendar", 100)]
    [InlineData("cal", "calendar", 90)]
    [InlineData("notifications", "alerts", 100)]
    [InlineData("Weather", "weather", 100)]
    [InlineData("timers", "timer", 100)]
    [InlineData("lyrics", "music", 100)]
    public void Views_are_found_by_name_or_prefix(string query, string id, int match)
    {
        var found = PaletteSources.FindViews(query);
        Assert.Contains(found, f => f.View.Id == id && f.Match == match);
    }

    [Fact]
    public void Views_need_two_letters_and_can_be_filtered()
    {
        Assert.Empty(PaletteSources.FindViews("c"));
        Assert.Empty(PaletteSources.FindViews("spotify"));
        Assert.DoesNotContain(PaletteSources.FindViews("settings", id => id != "settings"), f => f.View.Id == "settings");
    }

    [Fact]
    public void Text_match_prefers_start_then_word_then_anywhere()
    {
        Assert.Equal(80, PaletteSources.TextMatch("buy", "Buy milk and eggs"));
        Assert.Equal(70, PaletteSources.TextMatch("milk", "Buy milk and eggs"));
        Assert.Equal(55, PaletteSources.TextMatch("ilk", "Buy milk and eggs"));
        Assert.Equal(0, PaletteSources.TextMatch("bread", "Buy milk and eggs"));
        Assert.Equal(0, PaletteSources.TextMatch("bu", "Buy milk"));
        Assert.Equal(80, PaletteSources.TextMatch("buy  MILK", "buy milk"));
        // Only the start of a huge copy is searched.
        var log = new string('x', PaletteSources.MaxScan) + " needle";
        Assert.Equal(0, PaletteSources.TextMatch("needle", log));
        Assert.Equal(80, PaletteSources.TextMatch("xxx", log));
    }

    [Fact]
    public void Texts_rank_by_match_then_source_order()
    {
        var items = new[]
        {
            new PaletteText("1", "Invoice for October"),
            new PaletteText("2", "send the invoice"),
            new PaletteText("3", "Invoice reminder"),
            new PaletteText("4", "nothing here"),
        };
        var found = PaletteSources.FindTexts("invoice", items, 2);
        Assert.Equal(new[] { "1", "3" }, found.Select(f => f.Item.Key));
    }

    [Fact]
    public void Snippets_match_by_trigger_first()
    {
        var items = new[]
        {
            new PaletteText(";addr", "221B Baker Street", ";addr"),
            new PaletteText(";sig", "Best, Roushan — address on request", ";sig"),
        };
        Assert.Equal((";addr", 95), Pick(PaletteSources.FindTexts(";addr", items, 2)));
        Assert.Equal((";addr", 85), Pick(PaletteSources.FindTexts("addr", items, 1)));
        Assert.Equal((";sig", 70), Pick(PaletteSources.FindTexts("address", items, 1)));
        Assert.Equal((";addr", 70), Pick(PaletteSources.FindTexts("baker", items, 1)));

        static (string, int) Pick(List<(PaletteText Item, int Match)> found) => (found[0].Item.Key, found[0].Match);
    }

    [Fact]
    public void Clipboard_and_notes_stay_out_while_presenting()
    {
        Assert.False(PaletteSources.Allowed(PaletteKind.Clip, presenting: true));
        Assert.False(PaletteSources.Allowed(PaletteKind.Note, presenting: true));
        Assert.True(PaletteSources.Allowed(PaletteKind.Snippet, presenting: true));
        Assert.True(PaletteSources.Allowed(PaletteKind.View, presenting: true));
        Assert.True(PaletteSources.Allowed(PaletteKind.Clip, presenting: false));
    }

    [Fact]
    public void New_kinds_rank_below_apps_and_above_files()
    {
        var items = new[]
        {
            new PaletteItem("file:a", PaletteKind.File, "notes.txt", "", 90),
            new PaletteItem("clip:1", PaletteKind.Clip, "notes for call", "", 80),
            new PaletteItem("view:notes", PaletteKind.View, "Notes", "", 100),
            new PaletteItem("app:x", PaletteKind.App, "Sticky Notes", "", 80),
            new PaletteItem("note:1", PaletteKind.Note, "notes", "", 80),
            new PaletteItem("cmd:a", PaletteKind.Command, "Show notes", "", 100),
        };
        var ranked = PaletteRanker.Rank(items, max: 10);
        Assert.Equal(new[] { "cmd:a", "view:notes", "app:x", "note:1", "clip:1", "file:a" }, ranked.Items.Select(i => i.Key));
    }

    [Fact]
    public void Lines_are_single_and_short()
    {
        Assert.Equal("first line", PaletteSources.Line("\n  first   line \nsecond"));
        Assert.Equal(10, PaletteSources.Line(new string('x', 40), 10).Length);
        Assert.StartsWith("Try: play kesariya · remind me at 6 pm · 100 usd to inr", PaletteSources.TryRow);
    }

    [Fact]
    public void Top_picks_are_the_strongest_recent_ones()
    {
        var now = new DateTime(2026, 11, 4, 12, 0, 0);
        var store = new FrecencyStore();
        store.Record("app:spotify", "spot", now.AddDays(-1));
        store.Record("app:spotify", "spot", now.AddDays(-1));
        store.Record("view:calendar", "cal", now.AddHours(-1));
        store.Record("note:1", "milk", now.AddHours(-2));
        store.Record("file:old", "old", now.AddDays(-200));
        var top = store.Top(3, now, key => !key.StartsWith("note:", StringComparison.Ordinal));
        Assert.Equal(new[] { "app:spotify", "view:calendar" }, top.Select(e => e.Key));
        Assert.Single(store.Top(1, now));
    }
}
