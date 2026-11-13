using System.Globalization;

namespace Shammy.Core.Notch;

/// <summary>What the open island's one big "now" moment is about.</summary>
public enum HeroKind
{
    /// <summary>A song playing: title, artist, time and the controls, glowing in the album's colours.</summary>
    Music,

    /// <summary>A call, or a calendar meeting going on now: its name, the app and how long it has run.</summary>
    Call,

    /// <summary>A focus session, pomodoro or timer counting down (paused ones too, once nothing else is on).</summary>
    Focus,

    /// <summary>The next meeting today or soon: its time and Join.</summary>
    Meeting,

    /// <summary>A song paused: the same as <see cref="Music"/>, quieter.</summary>
    PausedMusic,

    /// <summary>The weather where you are.</summary>
    Weather,

    /// <summary>Nothing else to show: the time and the date.</summary>
    Clock,
}

/// <summary>One of the three numbers beside the moment.</summary>
public enum NowStat
{
    Weather,
    Battery,

    /// <summary>Minutes on the call (or in the meeting going on now).</summary>
    CallMinutes,

    /// <summary>Minutes left on the focus session or timer.</summary>
    FocusLeft,

    /// <summary>Minutes to the next meeting.</summary>
    MeetingIn,

    /// <summary>The day of the month.</summary>
    Date,

    /// <summary>The time.</summary>
    Time,
}

/// <summary>
/// What the open island can see as it opens.
/// </summary>
/// <param name="MusicPlaying">A song is playing now.</param>
/// <param name="HasMusic">A media session has a song (playing or paused).</param>
/// <param name="InCall">A call app holds the microphone.</param>
/// <param name="CountdownLeft">The time left on a focus session, pomodoro or timer; null when none is counting down.</param>
/// <param name="CountdownPaused">That countdown is paused.</param>
/// <param name="MeetingStartsIn">Until the next calendar meeting starts (zero or less: it has started); null when there is none.</param>
/// <param name="MeetingEndsIn">Until that meeting ends.</param>
/// <param name="HasWeather">A forecast is known.</param>
/// <param name="HasBattery">This PC has a battery.</param>
public sealed record NowSignals(
    bool MusicPlaying = false,
    bool HasMusic = false,
    bool InCall = false,
    TimeSpan? CountdownLeft = null,
    bool CountdownPaused = false,
    TimeSpan? MeetingStartsIn = null,
    TimeSpan? MeetingEndsIn = null,
    bool HasWeather = false,
    bool HasBattery = false)
{
    /// <summary>A calendar meeting is going on right now.</summary>
    public bool MeetingNow => MeetingStartsIn is { } s && s <= TimeSpan.Zero && MeetingEndsIn is { } e && e > TimeSpan.Zero;

    /// <summary>The next meeting starts within <see cref="NowHome.MeetingHorizon"/>.</summary>
    public bool MeetingSoon => MeetingStartsIn is { } s && s > TimeSpan.Zero && s <= NowHome.MeetingHorizon;

    public bool CountdownRunning => CountdownLeft is not null && !CountdownPaused;
}

/// <summary>
/// The open island's home (layout "Now + moment row"): which one thing the big moment shows, and which three
/// numbers sit beside it. The moment goes music, then a call or a meeting going on, then a running focus or
/// timer, then the next meeting, then whatever is paused, then the weather, then the clock. The third number
/// fits the moment: minutes on the call, minutes of focus left, or minutes to the next meeting.
/// </summary>
public static class NowHome
{
    /// <summary>A meeting further off than this isn't "next" enough for the moment or the numbers.</summary>
    public static readonly TimeSpan MeetingHorizon = TimeSpan.FromHours(12);

    public static HeroKind Hero(NowSignals s)
    {
        if (s.MusicPlaying) return HeroKind.Music;
        if (s.InCall || s.MeetingNow) return HeroKind.Call;
        if (s.CountdownRunning) return HeroKind.Focus;
        if (s.MeetingSoon) return HeroKind.Meeting;
        if (s.CountdownLeft is not null) return HeroKind.Focus;
        if (s.HasMusic) return HeroKind.PausedMusic;
        return s.HasWeather ? HeroKind.Weather : HeroKind.Clock;
    }

    /// <summary>The number that fits the moment, or null when nothing is going on.</summary>
    public static NowStat? MomentStat(NowSignals s)
    {
        if (s.InCall || s.MeetingNow) return NowStat.CallMinutes;
        if (s.CountdownRunning) return NowStat.FocusLeft;
        if (s.MeetingSoon) return NowStat.MeetingIn;
        if (s.CountdownLeft is not null) return NowStat.FocusLeft;
        return null;
    }

    /// <summary>
    /// The numbers, top to bottom: the weather (else the time), the battery (else the date), and the moment's own;
    /// with nothing going on the date (or the time) takes the third place, never shown twice.
    /// </summary>
    public static IReadOnlyList<NowStat> Stats(NowSignals s)
    {
        var stats = new List<NowStat> { s.HasWeather ? NowStat.Weather : NowStat.Time, s.HasBattery ? NowStat.Battery : NowStat.Date };
        if (MomentStat(s) is { } moment) stats.Add(moment);
        else if (!stats.Contains(NowStat.Date)) stats.Add(NowStat.Date);
        else if (!stats.Contains(NowStat.Time)) stats.Add(NowStat.Time);
        return stats;
    }
}

/// <summary>The words and numbers of the open island's home.</summary>
public static class NowText
{
    const string Lead = "Right now";

    /// <summary>The fallback when nothing needs you.</summary>
    public const string Quiet = "Nothing needs you";

    /// <summary>"Right now · Playing Kesariya · Standup in 12 min": up to <paramref name="max"/> parts, each once.</summary>
    public static string Sentence(IEnumerable<string?> parts, int max = 3)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = parts.Select(p => (p ?? "").Trim()).Where(p => p.Length > 0 && seen.Add(p)).Take(Math.Max(1, max)).ToList();
        return $"{Lead} · {(kept.Count == 0 ? Quiet : string.Join(" · ", kept))}";
    }

    /// <summary>
    /// A span as the big number and its unit: "12" "min" under 100 minutes, "1:40" "h" from there. Time still to
    /// go rounds up (a meeting 30 s away is "1 min" away); time gone rounds down.
    /// </summary>
    public static (string Value, string Unit) Minutes(TimeSpan span, bool roundUp)
    {
        double total = Math.Max(0, span.TotalMinutes);
        long m = (long)(roundUp ? Math.Ceiling(total - 1e-9) : Math.Floor(total + 1e-9));
        if (m < 100) return (m.ToString(CultureInfo.InvariantCulture), "min");
        return ($"{m / 60}:{m % 60:00}", "h");
    }

    /// <summary>"24° Partly cloudy" → ("24", "Partly cloudy"); "−3° Oslo" → ("−3", "Oslo"); null without a degree sign.</summary>
    public static (string Number, string Words)? SplitTemperature(string? text)
    {
        var t = (text ?? "").Trim();
        int degree = t.IndexOf('°');
        if (degree <= 0) return null;
        var number = t[..degree].Trim();
        if (number.Length == 0 || !number.Replace("−", "").Replace("-", "").All(char.IsDigit)) return null;
        return (number, t[(degree + 1)..].Trim());
    }

    /// <summary>"12:34" (minutes and seconds) or "1:02:03" back to a span; null for anything else.</summary>
    public static TimeSpan? ParseClock(string? text)
    {
        var parts = (text ?? "").Trim().Split(':');
        if (parts.Length is < 2 or > 3) return null;
        var n = new long[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return null;
        return parts.Length == 2 ? TimeSpan.FromSeconds(n[0] * 60 + n[1]) : TimeSpan.FromSeconds(n[0] * 3600 + n[1] * 60 + n[2]);
    }

    /// <summary>The battery's grey line: "Charging", "Plugged in", "About 3 h left", or "On battery" while Windows is still estimating.</summary>
    public static string BatteryLine(bool pluggedIn, bool charging, int percent, TimeSpan? left)
    {
        if (charging) return percent >= 100 ? "Fully charged" : "Charging";
        if (pluggedIn) return percent >= 100 ? "Fully charged" : "Plugged in";
        return left is { } l && l > TimeSpan.Zero && l < TimeSpan.FromDays(2) ? Cards.PowerText.TimeLeft(l) : "On battery";
    }
}
