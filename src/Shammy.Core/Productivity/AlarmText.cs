using System.Globalization;

namespace Shammy.Core.Productivity;

/// <summary>How alarms read in the notch: "7:00 AM", "Weekdays", "Mon, Wed", "Tomorrow", "in 8 h 20 min".</summary>
public static class AlarmText
{
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    static readonly (AlarmDays Day, string Name)[] Order =
    {
        (AlarmDays.Monday, "Mon"), (AlarmDays.Tuesday, "Tue"), (AlarmDays.Wednesday, "Wed"), (AlarmDays.Thursday, "Thu"),
        (AlarmDays.Friday, "Fri"), (AlarmDays.Saturday, "Sat"), (AlarmDays.Sunday, "Sun"),
    };

    public static string Time(Alarm a, bool? use24Hour = null) => ClockPreference.Format(a.Hour, a.Minute, use24Hour);

    /// <summary>"Every day", "Weekdays", "Weekends", "Mon, Wed, Fri"; for a one-off, its day ("Today", "Tomorrow", "Sat 14 Nov").</summary>
    public static string Repeat(AlarmDays days) => days switch
    {
        AlarmDays.EveryDay => "Every day",
        AlarmDays.Weekdays => "Weekdays",
        AlarmDays.Weekends => "Weekends",
        AlarmDays.None => "Once",
        _ => string.Join(", ", Order.Where(o => (days & o.Day) != 0).Select(o => o.Name)),
    };

    public static string Repeat(Alarm a, DateTime nowLocal, DateTime? upcomingLocal)
    {
        if (a.Repeats) return Repeat(a.Days);
        var date = a.Date?.Date ?? upcomingLocal?.Date;
        if (date is not { } d) return "Once";
        int days = (d - nowLocal.Date).Days;
        return days switch
        {
            0 => "Today",
            1 => "Tomorrow",
            > 1 and < 7 => d.ToString("dddd", Invariant),
            _ => d.ToString("ddd d MMM", Invariant),
        };
    }

    /// <summary>"in 45 min", "in 8 h 20 min", "in 2 days"; rounded up to the minute so it never says "in 0 min".</summary>
    public static string Until(TimeSpan until)
    {
        long minutes = (long)Math.Ceiling(Math.Max(0, until.TotalMinutes));
        if (minutes < 1) return "now";
        if (minutes < 60) return $"in {minutes} min";
        if (minutes < 48 * 60)
        {
            long h = minutes / 60, m = minutes % 60;
            return m == 0 ? $"in {h} h" : $"in {h} h {m} min";
        }
        return $"in {minutes / (24 * 60)} days";
    }

    /// <summary>"Alarm · 7:00 AM tomorrow", "Alarm · 6:30 AM · Weekdays · Gym" (the palette row and the confirmation).</summary>
    public static string Describe(AlarmSpec spec, DateTime nowLocal)
    {
        var time = ClockPreference.Format(spec.Hour, spec.Minute);
        string when = spec.Days != AlarmDays.None ? " · " + Repeat(spec.Days)
            : spec.Date is { } d ? (d.Date - nowLocal.Date).Days switch
            {
                0 => " today",
                1 => " tomorrow",
                _ => " " + d.ToString("ddd d MMM", Invariant),
            }
            : "";
        var label = spec.Label.Length > 0 ? $" · {spec.Label}" : "";
        return $"Alarm · {time}{when}{label}";
    }

    /// <summary>"Missed alarm 7:00 AM".</summary>
    public static string Missed(Alarm a) => $"Missed alarm {Time(a)}";

    /// <summary>The short form of <see cref="Until"/> for tight rows: "in 45 m", "in 8 h 12 m", "in 3 days".</summary>
    public static string Short(TimeSpan until)
    {
        long minutes = (long)Math.Ceiling(Math.Max(0, until.TotalMinutes));
        if (minutes < 1) return "now";
        if (minutes < 60) return $"in {minutes} m";
        if (minutes < 48 * 60)
        {
            long h = minutes / 60, m = minutes % 60;
            return m == 0 ? $"in {h} h" : $"in {h} h {m} m";
        }
        return $"in {minutes / (24 * 60)} days";
    }

    /// <summary>What the notch says once an alarm is stopped: "Morning", "Afternoon", "Evening" or "Night".</summary>
    public static string Greeting(DateTime nowLocal) => nowLocal.Hour switch
    {
        >= 4 and < 12 => "Morning",
        >= 12 and < 17 => "Afternoon",
        >= 17 and < 21 => "Evening",
        _ => "Night",
    };

    /// <summary>The temperature at the start of a weather glance ("24° Bhubaneswar" → "24°"), or null.</summary>
    public static string? Temperature(string? weatherGlance)
    {
        if (string.IsNullOrWhiteSpace(weatherGlance)) return null;
        var t = weatherGlance.Trim();
        int i = t.IndexOf('°');
        if (i <= 0 || i > 4) return null;
        var number = t[..i];
        return (number.TrimStart('-', '−').Length > 0 && number.TrimStart('-', '−').All(char.IsAsciiDigit)) ? number + "°" : null;
    }

    /// <summary>The ringing screen's header: "Alarm · Thu 5 Nov".</summary>
    public static string RingDate(DateTime nowLocal) => "Alarm · " + nowLocal.ToString("ddd d MMM", Invariant);

    /// <summary>The days row's spoken name for a day toggle ("Monday"), in the design's Monday-first order.</summary>
    public static readonly IReadOnlyList<(AlarmDays Day, string Letter, string Name)> Week = new[]
    {
        (AlarmDays.Monday, "M", "Monday"), (AlarmDays.Tuesday, "T", "Tuesday"), (AlarmDays.Wednesday, "W", "Wednesday"),
        (AlarmDays.Thursday, "T", "Thursday"), (AlarmDays.Friday, "F", "Friday"), (AlarmDays.Saturday, "S", "Saturday"),
        (AlarmDays.Sunday, "S", "Sunday"),
    };
}
