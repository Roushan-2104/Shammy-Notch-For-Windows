using System.Globalization;
using System.Text.RegularExpressions;

namespace Shammy.Core.Search;

// A clock time with a time zone ("3 PM PST", "18:00 UTC+2"), shown in the PC's own time.
public static partial class CircleSense
{
    static readonly Regex ZoneTimeRx = new(
        @"(?<![\d:.])(\d{1,2})(?:[:.](\d{2}))?\s*(?:([ap])\.?\s?m\.?)?\s*(?:hrs?\.?\s*)?\(?" +
        @"(PST|PDT|EST|EDT|CST|CDT|MST|MDT|GMT|UTC|BST|CET|CEST|IST|JST|SGT|AEST|AEDT|GST|(?-i:ET|PT|CT|MT))" +
        @"(?:\s*([+\-−])\s*(\d{1,2})(?::?(\d{2}))?)?\)?(?![a-z0-9])",
        Opt, Budget);

    /// <summary>A zone's offset in minutes and the name the island uses; null minutes means US rules decide (ET, PT…).</summary>
    static readonly Dictionary<string, (int? Minutes, string Name, int UsStandard)> Zones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PST"] = (-480, "Pacific", 0), ["PDT"] = (-420, "Pacific", 0), ["PT"] = (null, "Pacific", -480),
        ["EST"] = (-300, "Eastern", 0), ["EDT"] = (-240, "Eastern", 0), ["ET"] = (null, "Eastern", -300),
        ["CST"] = (-360, "Central", 0), ["CDT"] = (-300, "Central", 0), ["CT"] = (null, "Central", -360),
        ["MST"] = (-420, "Mountain", 0), ["MDT"] = (-360, "Mountain", 0), ["MT"] = (null, "Mountain", -420),
        ["GMT"] = (0, "GMT", 0), ["UTC"] = (0, "UTC", 0), ["BST"] = (60, "UK", 0),
        ["CET"] = (60, "Central Europe", 0), ["CEST"] = (120, "Central Europe", 0),
        ["IST"] = (330, "India", 0), ["JST"] = (540, "Japan", 0), ["SGT"] = (480, "Singapore", 0),
        ["AEST"] = (600, "Sydney", 0), ["AEDT"] = (660, "Sydney", 0), ["GST"] = (240, "Gulf", 0),
    };

    static CircleMeaning? ZoneTime(string text, DateTime now, CultureInfo culture, TimeZoneInfo local)
    {
        if (text.Length > 120) return null;
        var all = ZoneTimeRx.Matches(text);
        if (all.Count != 1) return null;
        var m = all[0];
        int h = Int(m, 1), min = m.Groups[2].Success ? Int(m, 2) : 0;
        bool ampm = m.Groups[3].Success;
        // "3 CET" alone is too thin; a time needs its minutes or AM/PM.
        if (!ampm && !m.Groups[2].Success) return null;
        if (min > 59) return null;
        if (ampm)
        {
            if (h is < 1 or > 12) return null;
            h = h % 12 + (char.ToLowerInvariant(m.Groups[3].Value[0]) == 'p' ? 12 : 0);
        }
        else if (h > 23) return null;

        var abbr = m.Groups[4].Value.ToUpperInvariant();
        var (fixedMinutes, name, usStandard) = Zones[abbr];
        if (m.Groups[5].Success)
        {
            // Only GMT and UTC take an offset ("UTC+2", "GMT+5:30").
            if (abbr is not ("GMT" or "UTC")) return null;
            int oh = Int(m, 6), om = m.Groups[7].Success ? Int(m, 7) : 0;
            if (oh > 14 || om > 59) return null;
            int sign = m.Groups[5].Value == "+" ? 1 : -1;
            fixedMinutes = sign * (oh * 60 + om);
            name = abbr + (sign > 0 ? "+" : "−") + oh.ToString(CultureInfo.InvariantCulture) + (om > 0 ? ":" + om.ToString("00", CultureInfo.InvariantCulture) : "");
        }

        // Words around it: a date ("Sat 21 Nov") sets the day, a few words are a title, more is a sentence.
        var rest = Regex.Replace(Cut(text, m), @"\s+", " ").Trim(' ', '·', '|', ',', '-', '–', '@', ':');
        rest = Regex.Replace(rest, @"(?:\s+|^)(?:at|on|from|by)$", "", Opt).Trim();
        DateTime? day = null;
        string? title = null;
        if (rest.Length > 0)
        {
            if (When(rest, now, culture) is { HasTime: false, At: { } d } date) { day = d.Date; title = date.Title; }
            else if (rest.Split(' ').Length > 3 || Regex.IsMatch(rest, @"\d")) return null;
            else title = rest;
        }

        int Offset(DateTime date) => fixedMinutes ?? (UsSummer(date) ? usStandard + 60 : usStandard);

        var utcNow = now - local.GetUtcOffset(now);
        var theirNow = utcNow.AddMinutes(Offset(utcNow.Date));
        var theirDay = day ?? theirNow.Date;
        var theirs = theirDay.AddHours(h).AddMinutes(min);
        var utc = DateTime.SpecifyKind(theirs.AddMinutes(-Offset(theirDay)), DateTimeKind.Utc);
        var mine = DateTime.SpecifyKind(utc + local.GetUtcOffset(utc), DateTimeKind.Unspecified);

        var dayWord = mine.Date == now.Date ? "today"
            : mine.Date == now.Date.AddDays(1) ? "tomorrow, " + mine.ToString("ddd d MMM", CultureInfo.InvariantCulture)
            : mine.Date == now.Date.AddDays(-1) ? "yesterday, " + mine.ToString("ddd d MMM", CultureInfo.InvariantCulture)
            : mine.ToString(mine.Year == now.Year ? "ddd d MMM" : "ddd d MMM yyyy", CultureInfo.InvariantCulture);

        return new CircleMeaning(CircleKind.ZoneTime, mine.ToString("s", CultureInfo.InvariantCulture), Clock(mine),
            Clock(theirs) + " " + name + " · your time")
        {
            At = mine,
            HasTime = true,
            Title = string.IsNullOrEmpty(title) ? null : title,
            Detail = dayWord,
        };
    }

    static string Clock(DateTime t) => t.ToString("h:mm tt", CultureInfo.InvariantCulture);

    /// <summary>US daylight saving: from the second Sunday in March to the first Sunday in November.</summary>
    static bool UsSummer(DateTime date)
    {
        var start = NthSunday(date.Year, 3, 2);
        var end = NthSunday(date.Year, 11, 1);
        return date.Date >= start && date.Date < end;
    }

    static DateTime NthSunday(int year, int month, int n)
    {
        var first = new DateTime(year, month, 1);
        int toSunday = ((int)DayOfWeek.Sunday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(toSunday + 7 * (n - 1));
    }
}
