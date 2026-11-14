using System.Globalization;
using System.Text.RegularExpressions;
using Shammy.Core.Conversions;

namespace Shammy.Core.Search;

// A clock time with a time zone ("3 PM PST", "18:00 UTC+2"), shown in the PC's own time.
public static partial class CircleSense
{
    static readonly Regex ZoneTimeRx = new(
        @"(?<![\d:.])(\d{1,2})(?:[:.](\d{2}))?\s*(?:([ap])\.?\s?m\.?)?\s*(?:hrs?\.?\s*)?\(?" +
        @"(PST|PDT|EST|EDT|CST|CDT|MST|MDT|GMT|UTC|BST|CET|CEST|IST|JST|SGT|AEST|AEDT|GST|(?-i:ET|PT|CT|MT))" +
        @"(?:\s*([+\-−])\s*(\d{1,2})(?::?(\d{2}))?)?\)?(?![a-z0-9])",
        Opt, Budget);

    /// <summary>
    /// The name the island uses for each zone. The clock itself comes from the world clock's Windows zone, so an
    /// abbreviation names the region as there: "6 PM EST" in July is 6 PM New York time, the same answer everywhere.
    /// </summary>
    static readonly Dictionary<string, string> ZoneNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PST"] = "Pacific", ["PDT"] = "Pacific", ["PT"] = "Pacific",
        ["EST"] = "Eastern", ["EDT"] = "Eastern", ["ET"] = "Eastern",
        ["CST"] = "Central", ["CDT"] = "Central", ["CT"] = "Central",
        ["MST"] = "Mountain", ["MDT"] = "Mountain", ["MT"] = "Mountain",
        ["GMT"] = "GMT", ["UTC"] = "UTC", ["BST"] = "UK", ["CET"] = "Central Europe", ["CEST"] = "Central Europe",
        ["IST"] = "India", ["JST"] = "Japan", ["SGT"] = "Singapore", ["AEST"] = "Sydney", ["AEDT"] = "Sydney", ["GST"] = "Gulf",
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
        var name = ZoneNames[abbr];
        TimeZoneInfo theirZone;
        if (m.Groups[5].Success)
        {
            // Only GMT and UTC take an offset ("UTC+2", "GMT+5:30").
            if (abbr is not ("GMT" or "UTC")) return null;
            int oh = Int(m, 6), om = m.Groups[7].Success ? Int(m, 7) : 0;
            if (oh > 14 || om > 59) return null;
            int sign = m.Groups[5].Value == "+" ? 1 : -1;
            name = abbr + (sign > 0 ? "+" : "−") + oh.ToString(CultureInfo.InvariantCulture) + (om > 0 ? ":" + om.ToString("00", CultureInfo.InvariantCulture) : "");
            theirZone = TimeZoneInfo.CreateCustomTimeZone(name, TimeSpan.FromMinutes(sign * (oh * 60 + om)), name, name);
        }
        else if (WorldClock.TryFindZone(abbr, out var zone)) theirZone = zone.Info;
        else return null;

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

        var utcNow = DateTime.SpecifyKind(now - local.GetUtcOffset(now), DateTimeKind.Utc);
        var theirNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, theirZone);
        var theirDay = day ?? theirNow.Date;
        var theirs = DateTime.SpecifyKind(theirDay.AddHours(h).AddMinutes(min), DateTimeKind.Unspecified);
        if (theirZone.IsInvalidTime(theirs)) theirs = theirs.AddHours(1); // skipped by a DST jump
        var utc = TimeZoneInfo.ConvertTimeToUtc(theirs, theirZone);
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
}
