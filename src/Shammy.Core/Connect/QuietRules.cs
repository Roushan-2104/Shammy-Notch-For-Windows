using Shammy.Core.Calendar;
using Shammy.Core.Productivity;

namespace Shammy.Core.Connect;

/// <summary>Why the notch is quiet; several can hold at once (a focus session during a call).</summary>
public enum QuietReason { Call, Meeting, Focus, Test, DoNotDisturb }

/// <summary>
/// Quiet mode: during a call, a calendar meeting or a focus session other apps' notifications neither ping nor
/// pop up; they wait in the Notifications view and a "3 held" pill says so when quiet ends. Shammy's own
/// alarms, finished timers and reminders are not notifications and always break through.
/// </summary>
public static class QuietRules
{
    // Friendly names (see MeetingSystem.AppNames) of apps whose open microphone means a call. A browser only
    // counts once its window title showed it hosting one of these (it then reports as "Google Meet" etc.).
    static readonly HashSet<string> CallApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Zoom", "Teams", "Google Meet", "Skype", "Webex", "Discord", "Slack", "WhatsApp", "Telegram",
    };

    /// <summary>Whether an app holding the microphone is a call (not a voice recorder or a game).</summary>
    public static bool IsCallApp(string? appName) => appName is not null && CallApps.Contains(appName.Trim());

    public static bool AnyCallApp(IEnumerable<string> appNames) => appNames.Any(IsCallApp);

    /// <summary>A calendar event with a join link that is going on right now.</summary>
    public static CalendarOccurrence? MeetingNow(IEnumerable<CalendarOccurrence> items, DateTime now) =>
        items.Where(o => !o.AllDay && o.HasLink && o.Start <= now && now < o.End).OrderBy(o => o.Start).FirstOrDefault();

    /// <summary>When <see cref="MeetingNow"/> next changes (a linked meeting starts or the current one ends); null if never in the data.</summary>
    public static DateTime? NextMeetingChange(IEnumerable<CalendarOccurrence> items, DateTime now)
    {
        DateTime? next = null;
        foreach (var o in items)
        {
            if (o.AllDay || !o.HasLink) continue;
            DateTime? at = o.Start > now ? o.Start : o.End > now ? o.End : null;
            if (at is { } t && (next is null || t < next)) next = t;
        }
        return next;
    }

    /// <summary>
    /// A focus clock (paused or not) keeps things quiet; a pomodoro only during its focus phases, so its
    /// breaks let the held notifications through.
    /// </summary>
    public static bool FocusQuiet(bool focusClock, PomodoroPhase? pomodoroPhase) =>
        focusClock || pomodoroPhase == PomodoroPhase.Focus;

    /// <summary>The pill shown when quiet ends with notifications waiting: "1 held", "3 held"; null for none.</summary>
    public static string? HeldText(int held) => held <= 0 ? null : $"{held} held";

    /// <summary>"in a call", "in a meeting", "focusing" for the state pipe and the log.</summary>
    public static string Describe(IEnumerable<QuietReason> reasons)
    {
        var list = reasons.Distinct().OrderBy(r => r).Select(r => r switch
        {
            QuietReason.Call => "call",
            QuietReason.Meeting => "meeting",
            QuietReason.Focus => "focus",
            QuietReason.DoNotDisturb => "quiet",
            _ => "test",
        }).ToList();
        return list.Count == 0 ? "off" : string.Join("+", list);
    }
}
