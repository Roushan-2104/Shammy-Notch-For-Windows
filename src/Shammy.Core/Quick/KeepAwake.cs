using System.Globalization;

namespace Shammy.Core.Quick;

/// <summary>
/// Keep-awake's clock: it is either off or on until a set moment, never open-ended, so a forgotten one can't
/// drain the battery overnight. Pure: whoever holds it tells it the time.
/// </summary>
public sealed class KeepAwakeClock
{
    public const int DefaultMinutes = 60, MinMinutes = 1, MaxMinutes = 12 * 60;

    /// <summary>The lengths the tile offers.</summary>
    public static readonly IReadOnlyList<int> Choices = new[] { 30, 60, 120, 240 };

    public DateTime? EndsUtc { get; private set; }

    /// <summary>How long the running one was started for; 0 while off.</summary>
    public int StartedMinutes { get; private set; }

    public bool IsOn => EndsUtc is not null;

    /// <summary>Between a minute and twelve hours, whatever was asked.</summary>
    public static int Clamp(int minutes) => Math.Clamp(minutes, MinMinutes, MaxMinutes);

    /// <summary>Starts (or restarts) it; returns the minutes it will really run.</summary>
    public int Start(DateTime nowUtc, int minutes)
    {
        StartedMinutes = Clamp(minutes);
        EndsUtc = nowUtc.AddMinutes(StartedMinutes);
        return StartedMinutes;
    }

    public void Stop()
    {
        EndsUtc = null;
        StartedMinutes = 0;
    }

    /// <summary>Turns it off once its time is up; true only at that moment.</summary>
    public bool Expire(DateTime nowUtc)
    {
        if (EndsUtc is not { } ends || nowUtc < ends) return false;
        Stop();
        return true;
    }

    /// <summary>Whole minutes left, rounded up so it reads "1 min" until the very end; 0 when off or over.</summary>
    public int MinutesLeft(DateTime nowUtc) =>
        EndsUtc is { } ends && ends > nowUtc ? Math.Max(1, (int)Math.Ceiling((ends - nowUtc).TotalMinutes - 1e-6)) : 0;

    /// <summary>Brings the end closer, as if that much time had passed (for testing without waiting).</summary>
    public void Advance(TimeSpan by)
    {
        if (EndsUtc is { } ends) EndsUtc = ends - by;
    }

    /// <summary>
    /// When to look again: as the minutes-left number changes or it ends. Between a second and a minute, so a
    /// clock that jumped (the PC slept) is noticed soon after.
    /// </summary>
    public TimeSpan NextCheck(DateTime nowUtc)
    {
        if (EndsUtc is not { } ends) return TimeSpan.FromMinutes(1);
        var left = ends - nowUtc;
        if (left <= TimeSpan.Zero) return TimeSpan.FromSeconds(1);
        double intoMinute = left.TotalSeconds % 60;
        return TimeSpan.FromSeconds(Math.Clamp(intoMinute < 0.05 ? 60 : intoMinute + 0.05, 1, 60));
    }
}

/// <summary>How keep-awake's times read: "58 min", "1 h 30 m" beside the pet, "On · 58 min left" on the tile.</summary>
public static class KeepAwakeText
{
    static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>Beside the pet, at most nine characters: "58 min", "2 h", "1 h 58 m".</summary>
    public static string Short(int minutes)
    {
        minutes = Math.Max(0, minutes);
        int h = minutes / 60, m = minutes % 60;
        return h == 0 ? $"{N(m)} min" : m == 0 ? $"{N(h)} h" : $"{N(h)} h {N(m)} m";
    }

    /// <summary>In a sentence: "58 min", "2 h", "1 h 58 min".</summary>
    public static string Long(int minutes)
    {
        minutes = Math.Max(0, minutes);
        int h = minutes / 60, m = minutes % 60;
        return h == 0 ? $"{N(m)} min" : m == 0 ? $"{N(h)} h" : $"{N(h)} h {N(m)} min";
    }

    /// <summary>The keep-awake tile's second line.</summary>
    public static string TileLine(bool on, int minutesLeft) => on ? $"On · {Long(Math.Max(1, minutesLeft))} left" : "Off";

    /// <summary>The microphone tile's second line; null means there is no microphone.</summary>
    public static string MicLine(bool? muted) => muted switch
    {
        true => "Mic is muted",
        false => "Mic is live",
        null => "No microphone found",
    };
}
