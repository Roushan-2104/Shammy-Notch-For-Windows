using System.Globalization;

namespace Shammy.Core.Productivity;

/// <summary>A meeting in today's brief.</summary>
public sealed record BriefMeeting(DateTime Start, string Title);

/// <summary>
/// The morning brief: one friendly paragraph to say aloud and one short line to show. "Good morning! It's 28° and
/// cloudy. You have 2 meetings, the first at 10:30, Standup. One reminder: pay rent. Battery's at 80%." Only what
/// is known goes in; a clear day says so.
/// </summary>
public static class MorningBrief
{
    /// <summary>The brief is for the morning: from 5 AM until noon.</summary>
    public static bool IsMorning(DateTime local) => local.Hour is >= 5 and < 12;

    /// <summary>"Good morning" (5 AM to noon), "Good afternoon" (to 5 PM), "Good evening" (to midnight), "Hello" in the small hours.</summary>
    public static string Greeting(DateTime local) => local.Hour switch
    {
        >= 5 and < 12 => "Good morning",
        >= 12 and < 17 => "Good afternoon",
        >= 17 => "Good evening",
        _ => "Hello",
    };

    /// <summary>
    /// The brief for <paramref name="now"/>. Asked later in the day it greets by the time of day, and once some of
    /// today's meetings are over it speaks of the ones still to come ("No more meetings today").
    /// </summary>
    public static (string Spoken, string Line) Compose(DateTime now, string? weather, IReadOnlyList<BriefMeeting> meetings,
        IReadOnlyList<string> reminders, int? battery, bool use24Hour)
    {
        var hello = Greeting(now);
        var spoken = new List<string> { hello + "!" };
        var line = new List<string>();

        if (!string.IsNullOrWhiteSpace(weather))
        {
            spoken.Add($"It's {weather.Trim().TrimEnd('.')}.");
            line.Add(weather.Trim());
        }

        var upcoming = meetings.Where(m => m.Start >= now.AddMinutes(-5) && m.Start.Date == now.Date).OrderBy(m => m.Start).ToList();
        // Some of today's meetings have already begun (or ended): what's left is "more".
        bool earlier = meetings.Any(m => m.Start.Date == now.Date && m.Start < now.AddMinutes(-5));
        string Time(DateTime t) => t.ToString(use24Hour ? "HH:mm" : "h:mm tt", CultureInfo.InvariantCulture);
        if (upcoming.Count == 0)
        {
            spoken.Add(earlier ? "No more meetings today." : "No meetings today.");
            if (earlier) line.Add("no more meetings");
        }
        else
        {
            var first = upcoming[0];
            string more = earlier ? " more" : "";
            spoken.Add(upcoming.Count == 1
                ? $"One{more} meeting today, at {Time(first.Start)}, {first.Title}."
                : $"You have {upcoming.Count}{more} meetings, the first at {Time(first.Start)}, {first.Title}.");
            line.Add(upcoming.Count == 1 ? $"{first.Title} {Time(first.Start)}" : $"{upcoming.Count} meetings, first {Time(first.Start)}");
        }

        if (reminders.Count == 1)
        {
            spoken.Add($"One reminder: {reminders[0].Trim().TrimEnd('.')}.");
            line.Add("1 reminder");
        }
        else if (reminders.Count > 1)
        {
            spoken.Add($"{reminders.Count} reminders, starting with {reminders[0].Trim().TrimEnd('.')}.");
            line.Add($"{reminders.Count} reminders");
        }

        if (battery is { } b and < 100)
        {
            spoken.Add(b <= 20 ? $"Battery's low, at {b}%. Plug in soon." : $"Battery's at {b}%.");
            if (b <= 20) line.Add($"battery {b}%");
        }

        return (string.Join(" ", spoken), line.Count == 0 ? $"{hello} · a clear day" : $"{hello} · " + string.Join(" · ", line));
    }
}
