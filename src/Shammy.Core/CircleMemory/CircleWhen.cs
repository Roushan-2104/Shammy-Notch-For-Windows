using System.Globalization;

namespace Shammy.Core.CircleMemory;

/// <summary>How a remembered circle's time reads: the row's chip, the answer's label and a dragged picture's file name.</summary>
public static class CircleWhen
{
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    static string Clock(DateTime t, bool h24) => t.ToString(h24 ? "HH:mm" : "h:mm tt", Invariant);

    /// <summary>The row's "when" chip: "Today 2:14 PM", "Yesterday", "Mon" (this past week), else "14 Nov".</summary>
    public static string Chip(DateTime at, DateTime now, bool h24 = false)
    {
        int days = (now.Date - at.Date).Days;
        return days switch
        {
            <= 0 => $"Today {Clock(at, h24)}",
            1 => "Yesterday",
            < 7 => at.ToString("ddd", Invariant),
            _ when at.Year != now.Year => at.ToString("d MMM yyyy", Invariant),
            _ => at.ToString("d MMM", Invariant),
        };
    }

    /// <summary>The day in a sentence: "today", "yesterday", "on Mon", "on 14 Nov".</summary>
    public static string Day(DateTime at, DateTime now)
    {
        int days = (now.Date - at.Date).Days;
        return days switch
        {
            <= 0 => "today",
            1 => "yesterday",
            < 7 => "on " + at.ToString("ddd", Invariant),
            _ => "on " + at.ToString(at.Year != now.Year ? "d MMM yyyy" : "d MMM", Invariant),
        };
    }

    /// <summary>
    /// Where and when, for a found circle's answer: "You circled it in WhatsApp yesterday at 6:40 PM." (the app left
    /// out when unknown).
    /// </summary>
    public static string Sentence(RememberedCircle c, DateTime now, bool h24 = false)
    {
        var app = string.IsNullOrWhiteSpace(c.App) ? "" : $" in {c.App}";
        return $"You circled it{app} {Day(c.At, now)} at {Clock(c.At, h24)}.";
    }

    /// <summary>A dragged picture's file name, by when it was circled: "Circle 14 Nov 4.12 PM.png".</summary>
    public static string FileName(DateTime at, bool h24 = false) =>
        $"Circle {at.ToString("d MMM", Invariant)} {at.ToString(h24 ? "HH.mm" : "h.mm tt", Invariant)}.png";
}
