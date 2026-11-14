using System.Text.RegularExpressions;
using Shammy.Core.Launcher;
using Shammy.Core.Music;
using Shammy.Core.Productivity;

namespace Shammy.Core.Palette;

/// <summary>Media keys the palette offers as rows.</summary>
public enum MediaControl { PlayPause, Next, Previous }

/// <summary>Cheap reads of what the typed text is likely to be, used to decide which rows to offer.</summary>
public static class PaletteQuery
{
    const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    static readonly (string Word, MediaControl Control)[] MediaWords =
    {
        ("pause", MediaControl.PlayPause), ("play", MediaControl.PlayPause), ("resume", MediaControl.PlayPause),
        ("next", MediaControl.Next), ("skip", MediaControl.Next),
        ("previous", MediaControl.Previous), ("prev", MediaControl.Previous),
    };

    /// <summary>
    /// Media rows for a typed prefix of a transport word ("pa" → play/pause, "nex" → next). Two letters at
    /// least, and only for a single word, so "next week" stays a search. 100 for the whole word.
    /// </summary>
    public static IReadOnlyList<(MediaControl Control, int Match)> MediaControls(string text)
    {
        var q = AppMatcher.Normalize(text ?? "");
        if (q.Length < 2 || q.Contains(' ')) return Array.Empty<(MediaControl, int)>();
        return MediaWords
            .Where(w => w.Word.StartsWith(q, StringComparison.Ordinal))
            .Select(w => (w.Control, Match: w.Word == q ? 100 : 80))
            .GroupBy(w => w.Control)
            .Select(g => (g.Key, g.Max(x => x.Match)))
            .ToList();
    }

    /// <summary>
    /// What to offer as "Play … on YouTube Music", only when the text asks to play something: "play X",
    /// "put on X", or Hindi/Hinglish "X bajao / chalao / lagao / sunao" (also "bajao X"). Plain words are
    /// never guessed to be songs. Null when there's no play intent.
    /// </summary>
    public static string? MusicQuery(string text)
    {
        if (MusicCommand.Parse(text) is { Action: MusicAction.PlayYouTubeMusic } intent) return intent.Query;
        var t = Regex.Replace((text ?? "").Trim().TrimEnd('.', '!', '?'), @"\s+", " ");
        var m = HindiPlayAfter.Match(t);
        if (!m.Success) m = HindiPlayBefore.Match(t);
        if (!m.Success) return null;
        var q = SongWord.Replace(m.Groups["q"].Value.Trim(), "").Trim();
        if (q.Length < 2) return null;
        // What "play X" would actually do; "play store" and friends are searches, not songs.
        return MusicCommand.Parse("play " + q) is { Action: MusicAction.PlayYouTubeMusic } guess ? guess.Query : null;
    }

    // Not "lagao" / "laga do": that's how timers and alarms are set ("alarm laga do").
    const string HindiVerb = @"(?:baja|bajao|baja\s+do|chalao|chala\s+do|sunao|suna\s+do)";
    static readonly Regex HindiPlayAfter = new($@"^(?<q>.+?)\s+{HindiVerb}(?:\s+(?:na|please|yaar))?$", Opts);
    static readonly Regex HindiPlayBefore = new($@"^{HindiVerb}\s+(?<q>.+)$", Opts);

    // "arijit ke gaane bajao" → "arijit"; "tum hi ho song chalao" → "tum hi ho".
    static readonly Regex SongWord = new(@"\s+(?:ke|ka|ki)\s+(?:ga+n(?:y?e|ey|a)|songs?)$|\s+(?:wala\s+|vala\s+)?(?:gaana|gana|gaane|gane|song|songs)$", Opts);

    /// <summary>What a timer/reminder/note command will do, in a few words; null when there's nothing useful to say.</summary>
    static string AlarmTarget(ProductivityCommand c) =>
        c.Target is { } t ? $" · {(t.Exact ? ClockPreference.Format(t.Hour, t.Minute) : $"{t.Hour}:{t.Minute:00}")}"
        : c.Text.Length > 0 ? $" · {c.Text}" : "";

    public static string? DescribeProductivity(ProductivityCommand c, DateTime now) => c.Action switch
    {
        ProductivityAction.StartTimer => c.Text.Length > 0
            ? $"Start a {TimeFormat.Describe(c.Duration)} timer · {c.Text}"
            : $"Start a {TimeFormat.Describe(c.Duration)} timer",
        ProductivityAction.StopTimer => "Cancel timer",
        ProductivityAction.PauseTimer => "Pause timer",
        ProductivityAction.ResumeTimer => "Resume timer",
        ProductivityAction.StartStopwatch => "Start a stopwatch",
        ProductivityAction.StopStopwatch => "Stop the stopwatch",
        ProductivityAction.StartPomodoro => "Start a pomodoro",
        ProductivityAction.StopPomodoro => "Stop the pomodoro",
        ProductivityAction.StartFocus => $"Focus for {TimeFormat.Describe(c.Duration)}",
        ProductivityAction.StopFocus => "End focus session",
        ProductivityAction.Remind => c.Text.Length > 0
            ? $"Reminder · {TimeFormat.When(c.Due, now)} · {c.Text}"
            : $"Reminder · {TimeFormat.When(c.Due, now)}",
        ProductivityAction.ReminderNeedsTime => "Reminder · add a time",
        ProductivityAction.Note => $"Save note · {c.Text}",
        ProductivityAction.ShowNotes => "Show notes",
        ProductivityAction.SetAlarm when c.Alarm is { } alarm => AlarmText.Describe(alarm, now),
        ProductivityAction.ShowAlarms => "Show alarms",
        ProductivityAction.DeleteAlarm => c.All ? "Delete all alarms" : $"Delete alarm{AlarmTarget(c)}",
        ProductivityAction.DisableAlarms => c.All ? "Turn off all alarms" : $"Turn off alarm{AlarmTarget(c)}",
        ProductivityAction.EnableAlarms => c.All ? "Turn on all alarms" : $"Turn on alarm{AlarmTarget(c)}",
        ProductivityAction.StopAlarm => "Stop the alarm",
        ProductivityAction.SnoozeAlarm => c.Duration > TimeSpan.Zero ? $"Snooze alarm · {TimeFormat.Describe(c.Duration)}" : "Snooze alarm",
        _ => null,
    };
}
