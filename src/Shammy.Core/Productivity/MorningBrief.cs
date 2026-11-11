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

    public static (string Spoken, string Line) Compose(DateTime now, string? weather, IReadOnlyList<BriefMeeting> meetings,
        IReadOnlyList<string> reminders, int? battery, bool use24Hour)
    {
        var spoken = new List<string> { "Good morning!" };
        var line = new List<string>();

        if (!string.IsNullOrWhiteSpace(weather))
        {
            spoken.Add($"It's {weather.Trim().TrimEnd('.')}.");
            line.Add(weather.Trim());
        }

        var upcoming = meetings.Where(m => m.Start >= now.AddMinutes(-5) && m.Start.Date == now.Date).OrderBy(m => m.Start).ToList();
        string Time(DateTime t) => t.ToString(use24Hour ? "HH:mm" : "h:mm tt", CultureInfo.InvariantCulture);
        if (upcoming.Count == 0)
        {
            spoken.Add("No meetings today.");
        }
        else
        {
            var first = upcoming[0];
            spoken.Add(upcoming.Count == 1
                ? $"One meeting today, at {Time(first.Start)}, {first.Title}."
                : $"You have {upcoming.Count} meetings, the first at {Time(first.Start)}, {first.Title}.");
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

        return (string.Join(" ", spoken), line.Count == 0 ? "Good morning · a clear day" : "Good morning · " + string.Join(" · ", line));
    }
}
