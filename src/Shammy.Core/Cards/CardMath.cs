using System.Globalization;

namespace Shammy.Core.Cards;

/// <summary>
/// The numbers behind the v2 notch cards' visuals (rings, bar charts, range bars, scrubbers), kept out of
/// the views so they can be tested: every card draws real data, and a bad input must never draw a ring
/// past full or a bar below zero.
/// </summary>
public static class CardMath
{
    /// <summary>How full a ring or bar is, 0..1. A missing or zero total reads as empty.</summary>
    public static double Fraction(double value, double total) =>
        total <= 0 || double.IsNaN(value) || double.IsNaN(total) ? 0 : Math.Clamp(value / total, 0, 1);

    /// <summary>A battery or meter percentage as a ring fraction.</summary>
    public static double Percent(double percent) => double.IsNaN(percent) ? 0 : Math.Clamp(percent, 0, 100) / 100;

    /// <summary>
    /// Bar heights (as fractions of the chart) for a series like hourly temperatures: the coldest bar is
    /// <paramref name="floor"/> tall and the warmest is full, so small swings still read as a shape.
    /// A flat series draws every bar at full height.
    /// </summary>
    public static double[] BarHeights(IReadOnlyList<double> values, double floor = 0.3)
    {
        if (values.Count == 0) return Array.Empty<double>();
        double min = values.Min(), max = values.Max();
        var heights = new double[values.Count];
        for (int i = 0; i < values.Count; i++)
            heights[i] = max - min < 0.001 ? 1 : floor + (1 - floor) * (values[i] - min) / (max - min);
        return heights;
    }

    /// <summary>
    /// Where one day's low–high range sits on a shared scale (the 5-day list): left edge and width, both 0..1.
    /// A day whose low equals its high still gets a sliver so it stays visible.
    /// </summary>
    public static (double Left, double Width) RangeSpan(double low, double high, double scaleMin, double scaleMax)
    {
        if (high < low) (low, high) = (high, low);
        double span = scaleMax - scaleMin;
        if (span <= 0) return (0, 1);
        double left = Math.Clamp((low - scaleMin) / span, 0, 1);
        double right = Math.Clamp((high - scaleMin) / span, 0, 1);
        return (left, Math.Max(0.04, right - left));
    }

    /// <summary>Glance text is at most ~18 characters; longer names end with an ellipsis.</summary>
    public static string Fit(string text, int max = 18)
    {
        text = text.Trim();
        if (text.Length <= max) return text;
        return text[..Math.Max(1, max - 1)].TrimEnd() + "\u2026";
    }
}

/// <summary>Playback times for the music card's scrubber.</summary>
public static class MediaTime
{
    /// <summary>"1:58", "12:03", "1:02:03"; whole seconds, rounded down like every player does.</summary>
    public static string Clock(TimeSpan t)
    {
        long s = (long)Math.Floor(Math.Max(0, t.TotalSeconds));
        long h = s / 3600, m = s / 60 % 60, sec = s % 60;
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{sec:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{m}:{sec:00}");
    }

    /// <summary>"-2:30": time left in the track.</summary>
    public static string Remaining(TimeSpan position, TimeSpan duration) =>
        "-" + Clock(duration - position < TimeSpan.Zero ? TimeSpan.Zero : duration - position);

    /// <summary>A timeline is worth a scrubber only when the app reports a real duration.</summary>
    public static bool HasTimeline(TimeSpan duration) => duration >= TimeSpan.FromSeconds(1);

    /// <summary>"Kesariya · Arijit Singh" fitted to a glance.</summary>
    public static string Glance(string title, string artist, int max = 18)
    {
        var full = artist.Length > 0 ? $"{title} \u00B7 {artist}" : title;
        return full.Length <= max ? full : CardMath.Fit(title, max);
    }
}
