using System.Globalization;
using System.Text.RegularExpressions;

namespace Shammy.Core.Calendar;

/// <summary>One concrete occurrence in the viewer's local time (a recurring event yields one per instance).</summary>
/// <param name="Start">Local start; for an all-day event, midnight of its first day.</param>
/// <param name="End">Local end; for an all-day event, midnight after its last day.</param>
/// <param name="Link">Video-call link found in the event, if any.</param>
/// <param name="Provider">"Google Meet", "Zoom" or "Teams" for <paramref name="Link"/>.</param>
public sealed record CalendarOccurrence(string Uid, string Title, string Location, DateTime Start, DateTime End,
    bool AllDay, string? Link, string? Provider)
{
    public bool HasLink => !string.IsNullOrEmpty(Link);
    public TimeSpan Length => End - Start;
}

/// <summary>Turns parsed VEVENTs into the occurrences inside a window: recurrences, EXDATEs, moved and cancelled instances.</summary>
public static class CalendarExpander
{
    public static List<CalendarOccurrence> Expand(IEnumerable<IcsEvent> events, DateTime fromUtc, DateTime toUtc, TimeZoneInfo local)
    {
        var all = events.ToList();
        var result = new List<CalendarOccurrence>();

        // A moved or cancelled instance of a series is its own VEVENT with the same UID and a RECURRENCE-ID.
        var overridden = new HashSet<(string, DateTime)>();
        foreach (var e in all)
            if (e.RecurrenceId is { } rid) overridden.Add((e.Uid, Key(rid, local)));

        foreach (var e in all)
        {
            if (e.Start is not { } start) continue;
            if (e.RecurrenceId is not null)
            {
                if (!e.Cancelled) AddIfInside(result, e, start, fromUtc, toUtc, local);
                continue;
            }
            if (e.Cancelled) continue;

            var rule = RecurrenceRule.Parse(e.RRule);
            if (rule is null)
            {
                AddIfInside(result, e, start, fromUtc, toUtc, local);
                continue;
            }

            var length = Length(e, start, local);
            var zone = start.Zone(local);
            var exdates = e.ExDates.Select(x => Key(x, local)).ToHashSet();
            DateTime? untilUtc = rule.Until is { } u ? UntilUtc(u, start, zone, local) : null;
            int n = 0;
            foreach (var wall in rule.Starts(start.Value))
            {
                var instance = start with { Value = wall };
                var s = instance.IsDate ? IcsZones.ToUtc(wall, local) : instance.ToUtc(local);
                if (untilUtc is { } until && s > until) break;
                if (rule.Count is { } count && ++n > count) break;
                if (s >= toUtc) break;
                if (exdates.Contains(Key(instance, local)) || overridden.Contains((e.Uid, Key(instance, local)))) continue;
                var endUtc = instance.IsDate ? IcsZones.ToUtc(wall + length, local) : s + length;
                if (endUtc <= fromUtc && !(endUtc == s && s >= fromUtc)) continue;
                result.Add(Make(e, s, endUtc, instance.IsDate, local));
            }
        }
        return result.OrderBy(o => o.Start).ThenBy(o => o.AllDay ? 0 : 1).ThenBy(o => o.Title, StringComparer.Ordinal).ToList();
    }

    /// <summary>EXDATE and RECURRENCE-ID matching: the same instant, or for whole days the same date.</summary>
    static DateTime Key(IcsTime t, TimeZoneInfo local) => t.IsDate ? t.Value.Date : t.ToUtc(local);

    static DateTime UntilUtc(IcsTime until, IcsTime start, TimeZoneInfo zone, TimeZoneInfo local) =>
        until.Kind switch
        {
            IcsTimeKind.Utc => until.Value,
            // A date UNTIL includes that whole day.
            IcsTimeKind.Date => start.IsDate ? IcsZones.ToUtc(until.Value.Date, local) : IcsZones.ToUtc(until.Value.Date.AddDays(1).AddTicks(-1), zone),
            _ => IcsZones.ToUtc(until.Value, zone),
        };

    static TimeSpan Length(IcsEvent e, IcsTime start, TimeZoneInfo local)
    {
        if (e.End is { } end)
        {
            var d = end.IsDate || start.IsDate ? end.Value.Date - start.Value.Date : end.ToUtc(local) - start.ToUtc(local);
            if (d >= TimeSpan.Zero) return d;
        }
        if (e.Duration is { } dur && dur >= TimeSpan.Zero) return dur;
        // RFC 5545 §3.6.1: no end means one day for a date, a moment for a time.
        return start.IsDate ? TimeSpan.FromDays(1) : TimeSpan.Zero;
    }

    static void AddIfInside(List<CalendarOccurrence> result, IcsEvent e, IcsTime start, DateTime fromUtc, DateTime toUtc, TimeZoneInfo local)
    {
        var length = Length(e, start, local);
        var s = start.ToUtc(local);
        var endUtc = start.IsDate ? IcsZones.ToUtc(start.Value.Date + length, local) : s + length;
        if (s >= toUtc) return;
        if (endUtc <= fromUtc && !(endUtc == s && s >= fromUtc)) return;
        result.Add(Make(e, s, endUtc, start.IsDate, local));
    }

    static CalendarOccurrence Make(IcsEvent e, DateTime startUtc, DateTime endUtc, bool allDay, TimeZoneInfo local)
    {
        var link = MeetingLinks.Find(e.Conference, e.Location, e.Description);
        var title = e.Summary.Length > 0 ? e.Summary : "Busy";
        return new CalendarOccurrence(e.Uid, title, e.Location,
            TimeZoneInfo.ConvertTimeFromUtc(startUtc, local), TimeZoneInfo.ConvertTimeFromUtc(endUtc, local),
            allDay, link?.Url, link?.Provider);
    }
}

public sealed record MeetingLink(string Url, string Provider);

/// <summary>Finds a Google Meet, Zoom or Teams join link in an event's conference field, location or description.</summary>
public static class MeetingLinks
{
    static readonly Regex Url = new(@"https?://[^\s""'<>\\\]\[]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The first meeting link in the texts, searched in order (so pass the most reliable first).</summary>
    public static MeetingLink? Find(params string?[] texts)
    {
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text)) continue;
            foreach (Match m in Url.Matches(text))
            {
                var url = m.Value.Replace("&amp;", "&").TrimEnd('.', ',', ';', ')', '!', '?', ':');
                if (Classify(url) is { } provider) return new MeetingLink(url, provider);
            }
        }
        return null;
    }

    /// <summary>"Google Meet", "Zoom", "Teams", or null when the URL isn't a meeting join link.</summary>
    public static string? Classify(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return null;
        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath;
        if (host == "meet.google.com" && path.Length > 1) return "Google Meet";
        if (host is "hangouts.google.com" && path.Length > 1) return "Google Meet";
        if ((host == "zoom.us" || host.EndsWith(".zoom.us", StringComparison.Ordinal) || host.EndsWith(".zoomgov.com", StringComparison.Ordinal))
            && (path.StartsWith("/j/", StringComparison.Ordinal) || path.StartsWith("/my/", StringComparison.Ordinal)
                || path.StartsWith("/w/", StringComparison.Ordinal) || path.StartsWith("/s/", StringComparison.Ordinal)))
            return "Zoom";
        if (host == "teams.microsoft.com" && path.StartsWith("/l/meetup-join", StringComparison.OrdinalIgnoreCase)) return "Teams";
        if (host == "teams.microsoft.com" && path.StartsWith("/meet/", StringComparison.OrdinalIgnoreCase)) return "Teams";
        if (host == "teams.live.com" && path.StartsWith("/meet", StringComparison.OrdinalIgnoreCase)) return "Teams";
        return null;
    }
}

/// <summary>The private iCal address the user pastes from Google Calendar's settings.</summary>
public static class CalendarUrl
{
    /// <summary>
    /// Trims and normalises a pasted address (webcal:// becomes https://); null unless it is an https
    /// calendar.google.com/calendar/ical/…/basic.ics address.
    /// </summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().Trim('"', '\'', '<', '>');
        if (s.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) s = "https://" + s["webcal://".Length..];
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!uri.Host.Equals("calendar.google.com", StringComparison.OrdinalIgnoreCase)) return null;
        if (!uri.IsDefaultPort || uri.UserInfo.Length > 0) return null;
        var path = uri.AbsolutePath;
        if (!path.StartsWith("/calendar/ical/", StringComparison.OrdinalIgnoreCase)) return null;
        if (!path.EndsWith("/basic.ics", StringComparison.OrdinalIgnoreCase)) return null;
        return uri.GetLeftPart(UriPartial.Path);
    }

    /// <summary>True for the secret ("private-…") address, as opposed to the public one that only works for public calendars.</summary>
    public static bool IsPrivate(string url) => url.Contains("/private-", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Short texts for the card, the glance and the meeting pill. Times follow the user's clock: pass
/// <c>h24</c> = true where the culture writes "14:30" (then there's no AM/PM anywhere).
/// </summary>
public static class CalendarText
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>"10:30" (12-hour, no AM/PM; the card shows that separately) or "14:30".</summary>
    public static string Clock(DateTime t, bool h24 = false) => t.ToString(h24 ? "HH:mm" : "h:mm", Inv);

    /// <summary>"AM"/"PM"; empty on a 24-hour clock.</summary>
    public static string AmPm(DateTime t, bool h24 = false) => h24 ? "" : t.ToString("tt", Inv);

    /// <summary>"11:00 AM" or "11:00".</summary>
    public static string ClockAmPm(DateTime t, bool h24 = false) => t.ToString(h24 ? "HH:mm" : "h:mm tt", Inv);

    /// <summary>"10:30 – 10:45", "2 – 3 PM", "11:30 AM – 12:15 PM"; on a 24-hour clock "14:00 – 15:00".</summary>
    public static string Range(DateTime start, DateTime end, bool h24 = false)
    {
        static string Short(DateTime t) => t.Minute == 0 ? t.ToString("%h", Inv) : t.ToString("h:mm", Inv);
        if (h24) return end <= start ? Clock(start, true) : $"{Clock(start, true)} – {Clock(end, true)}";
        if (end <= start) return ClockAmPm(start);
        bool sameHalf = start.Hour < 12 == end.Hour < 12 && start.Date == end.Date;
        if (sameHalf)
        {
            // Hours alone read as "2 – 3 PM"; minutes keep the design's "10:30 – 10:45".
            bool whole = start.Minute == 0 && end.Minute == 0;
            return whole ? $"{Short(start)} – {Short(end)} {AmPm(end)}" : $"{Clock(start)} – {Clock(end)}";
        }
        return $"{ClockAmPm(start)} – {ClockAmPm(end)}";
    }

    /// <summary>"now", "in 10 min", "in 1 h 20 min", "in 3 h", "tomorrow", "on Thursday".</summary>
    public static string Until(DateTime start, DateTime end, DateTime now)
    {
        if (start <= now) return end > now ? "now" : "ended";
        var d = start - now;
        int minutes = (int)Math.Ceiling(d.TotalMinutes - 1e-9);
        if (minutes < 60) return $"in {minutes} min";
        if (start.Date == now.Date || d < TimeSpan.FromHours(12))
        {
            int h = minutes / 60, m = minutes % 60;
            return m == 0 ? $"in {h} h" : $"in {h} h {m} min";
        }
        if (start.Date == now.Date.AddDays(1)) return "tomorrow";
        return "on " + start.ToString("dddd", Inv);
    }

    /// <summary>"in 45 min · 30 min" style: the length of an event, "45 min", "1 h", "1 h 30 min", "All day".</summary>
    public static string Length(CalendarOccurrence o)
    {
        if (o.AllDay) return "All day";
        int minutes = (int)Math.Round(o.Length.TotalMinutes);
        if (minutes < 60) return $"{minutes} min";
        int h = minutes / 60, m = minutes % 60;
        return m == 0 ? $"{h} h" : $"{h} h {m} min";
    }

    /// <summary>"Standup 10:30" in at most <paramref name="max"/> characters, trimming the title rather than the time.</summary>
    public static string Glance(string title, DateTime start, int max = 18, bool h24 = false)
    {
        var time = Clock(start, h24);
        int room = max - time.Length - 1;
        title = title.Trim();
        if (room < 2) return time;
        if (title.Length > room) title = title[..(room - 1)].TrimEnd() + "…";
        return $"{title} {time}";
    }

    /// <summary>The timeline's hour labels: "8 AM", "12 PM", "6 PM", or "08:00", "18:00" on a 24-hour clock.</summary>
    public static string HourLabel(int hour, bool h24 = false)
    {
        hour = ((hour % 24) + 24) % 24;
        if (h24) return $"{hour:00}:00";
        return hour == 0 ? "12 AM" : hour == 12 ? "12 PM" : hour < 12 ? $"{hour} AM" : $"{hour - 12} PM";
    }

    /// <summary>
    /// "Couldn't refresh · last updated 10:20 AM" (with the day when it wasn't today) while the last fetch failed;
    /// null when it worked.
    /// </summary>
    public static string? SyncProblem(string? error, DateTime? fetchedAt, DateTime now, bool h24 = false)
    {
        if (string.IsNullOrEmpty(error)) return null;
        if (fetchedAt is not { } at) return "Couldn't refresh";
        var when = at.Date == now.Date ? ClockAmPm(at, h24) : $"{at.ToString("ddd", Inv)} {ClockAmPm(at, h24)}";
        return $"Couldn't refresh · last updated {when}";
    }

    /// <summary>"Standup in 2 min", "Standup now" for the meeting pill.</summary>
    public static string Pill(CalendarOccurrence o, DateTime now) =>
        o.Start <= now ? $"{o.Title} now" : $"{o.Title} {Until(o.Start, o.End, now)}";
}

/// <summary>Questions the card, the glance and the pill ask about a day's occurrences.</summary>
public static class CalendarAgenda
{
    public static readonly TimeSpan PillLead = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan PillAfterStart = TimeSpan.FromMinutes(5);

    /// <summary>The next timed occurrence that hasn't ended (an ongoing one counts), soonest first.</summary>
    public static CalendarOccurrence? Next(IEnumerable<CalendarOccurrence> items, DateTime now) =>
        items.Where(o => !o.AllDay && o.End > now && o.Start <= now.AddDays(7)).OrderBy(o => o.Start).FirstOrDefault();

    /// <summary>Occurrences touching a local day, all-day ones first.</summary>
    public static List<CalendarOccurrence> OnDay(IEnumerable<CalendarOccurrence> items, DateTime day)
    {
        var start = day.Date;
        var end = start.AddDays(1);
        return items.Where(o => o.Start < end && (o.End > start || (o.End == o.Start && o.Start >= start)))
            .OrderBy(o => o.AllDay ? 0 : 1).ThenBy(o => o.Start).ToList();
    }

    /// <summary>The home row shows the card when something is left today or starts in the next 12 hours.</summary>
    public static bool IsRelevant(IEnumerable<CalendarOccurrence> items, DateTime now) =>
        items.Any(o => o.End > now && (o.Start < now.Date.AddDays(1) || o.Start <= now.AddHours(12)));

    /// <summary>The meeting a pill is for right now: one with a link, from 5 min before its start to 5 min after.</summary>
    public static CalendarOccurrence? PillFor(IEnumerable<CalendarOccurrence> items, DateTime now) =>
        items.Where(o => !o.AllDay && o.HasLink && now >= o.Start - PillLead && now < o.Start + PillAfterStart && now < o.End)
            .OrderBy(o => o.Start).FirstOrDefault();

    /// <summary>When the next pill should appear (after <paramref name="now"/>), or null if none is coming in the data.</summary>
    public static DateTime? NextPillAt(IEnumerable<CalendarOccurrence> items, DateTime now) =>
        items.Where(o => !o.AllDay && o.HasLink && o.Start - PillLead > now).Select(o => (DateTime?)(o.Start - PillLead)).Min();

    /// <summary>The detail timeline's whole hours: 8 AM to 8 PM, stretched (in 2-hour steps) to take in earlier or later events that day.</summary>
    public static (int Start, int End) TimelineHours(IEnumerable<CalendarOccurrence> items, DateTime day)
    {
        const int DefaultStart = 8, DefaultEnd = 20;
        int start = DefaultStart, end = DefaultEnd;
        var dayStart = day.Date;
        foreach (var o in OnDay(items, day).Where(o => !o.AllDay))
        {
            double s = Math.Max(0, (o.Start - dayStart).TotalHours);
            double e = Math.Min(24, ((o.End > o.Start ? o.End : o.Start) - dayStart).TotalHours);
            start = Math.Min(start, (int)Math.Floor(s));
            end = Math.Max(end, (int)Math.Ceiling(e));
        }
        // Even hours keep the labels on the same 2-hour grid as the default.
        start -= start % 2;
        end += end % 2;
        return (Math.Clamp(start, 0, DefaultStart), Math.Clamp(end, DefaultEnd, 24));
    }

    /// <summary>
    /// The glance: the next meeting today ("Standup 10:30"), priority 50 within the hour, else 20; null when
    /// nothing timed is left today.
    /// </summary>
    public static (string Text, int Priority, CalendarOccurrence Item)? Glance(IEnumerable<CalendarOccurrence> items, DateTime now, bool h24 = false)
    {
        var next = items.Where(o => !o.AllDay && o.End > now && (o.Start.Date == now.Date || o.Start <= now.AddHours(1)))
            .OrderBy(o => o.Start).FirstOrDefault();
        if (next is null) return null;
        int priority = next.Start - now <= TimeSpan.FromMinutes(60) ? 50 : 20;
        return (CalendarText.Glance(next.Title, next.Start, h24: h24), priority, next);
    }

    /// <summary>The Monday-first week containing <paramref name="today"/>.</summary>
    public static IReadOnlyList<DateTime> Week(DateTime today)
    {
        int back = ((int)today.DayOfWeek + 6) % 7;
        var monday = today.Date.AddDays(-back);
        return Enumerable.Range(0, 7).Select(i => monday.AddDays(i)).ToList();
    }

    /// <summary>A month as the calendar wing lays it out: six Monday-first weeks (42 days) from the Monday on or before the 1st.</summary>
    public static IReadOnlyList<DateTime> MonthGrid(int year, int month)
    {
        var first = new DateTime(year, month, 1);
        var monday = Week(first)[0];
        return Enumerable.Range(0, 42).Select(i => monday.AddDays(i)).ToList();
    }

    /// <summary>
    /// The days whose events are kept: from this week's Monday (or the 1st, if earlier) to the end of next month,
    /// so the card's two weeks and the calendar wing's this month and next all have theirs.
    /// </summary>
    public static (DateTime From, DateTime To) FetchWindow(DateTime now)
    {
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var monday = Week(now)[0];
        var from = monday < monthStart ? monday : monthStart;
        var to = monthStart.AddMonths(2);
        var twoWeeks = now.Date.AddDays(15);
        return (from, to > twoWeeks ? to : twoWeeks);
    }
}

/// <summary>How the calendar wing fits beside the open island, which never moves: the wing opens on its right.</summary>
public static class CalendarWingFit
{
    public const double Width = 352, Neck = 14, Margin = 8, MinScale = 0.7;

    /// <summary>
    /// The wing's scale: the island's own, or smaller when the room right of the island (DIPs) is short of it.
    /// shortcut: below <see cref="MinScale"/> it stays at that size and runs off the screen's edge.
    /// </summary>
    public static double Scale(double room, double islandScale) =>
        Math.Clamp((room - Margin) / (Neck + Width), MinScale, islandScale);
}
