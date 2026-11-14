using System.Globalization;

namespace Shammy.Core.CircleMemory;

/// <summary>A stretch of time words named, from <see cref="From"/> up to (not including) <see cref="To"/>; local time.</summary>
public sealed record CircleSpan(DateTime From, DateTime To)
{
    public bool Contains(DateTime t) => t >= From && t < To;
}

/// <summary>
/// The time words in what someone asks about their circles, always looking back (circles are in the past, so "kal"
/// is yesterday): "today", "aaj", "yesterday", "kal", "parso", "this morning", "last night", "last week",
/// "pichhle hafte", "this month", "3 days ago", weekday names in English or Hindi ("Monday", "last fri",
/// "somvar") and dates ("14 Nov", "Nov 14th").
/// </summary>
public static class CircleDates
{
    static readonly Dictionary<string, DayOfWeek> Weekdays = new()
    {
        ["monday"] = DayOfWeek.Monday, ["mon"] = DayOfWeek.Monday, ["somvar"] = DayOfWeek.Monday, ["somwar"] = DayOfWeek.Monday,
        ["tuesday"] = DayOfWeek.Tuesday, ["tue"] = DayOfWeek.Tuesday, ["tues"] = DayOfWeek.Tuesday,
        ["mangalvar"] = DayOfWeek.Tuesday, ["mangalwar"] = DayOfWeek.Tuesday,
        ["wednesday"] = DayOfWeek.Wednesday, ["wed"] = DayOfWeek.Wednesday, ["budhvar"] = DayOfWeek.Wednesday, ["budhwar"] = DayOfWeek.Wednesday,
        ["thursday"] = DayOfWeek.Thursday, ["thu"] = DayOfWeek.Thursday, ["thur"] = DayOfWeek.Thursday, ["thurs"] = DayOfWeek.Thursday,
        ["guruvar"] = DayOfWeek.Thursday, ["guruwar"] = DayOfWeek.Thursday, ["brihaspativar"] = DayOfWeek.Thursday,
        ["friday"] = DayOfWeek.Friday, ["fri"] = DayOfWeek.Friday, ["shukravar"] = DayOfWeek.Friday, ["shukrawar"] = DayOfWeek.Friday,
        ["saturday"] = DayOfWeek.Saturday, ["sat"] = DayOfWeek.Saturday, ["shanivar"] = DayOfWeek.Saturday, ["shaniwar"] = DayOfWeek.Saturday,
        ["sunday"] = DayOfWeek.Sunday, ["sun"] = DayOfWeek.Sunday, ["ravivar"] = DayOfWeek.Sunday, ["raviwar"] = DayOfWeek.Sunday,
        ["itvar"] = DayOfWeek.Sunday, ["itwar"] = DayOfWeek.Sunday,
    };

    static readonly string[] MonthNames = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };

    static readonly Dictionary<string, int> Numbers = new()
    {
        ["one"] = 1, ["a"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
        ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["ek"] = 1, ["do"] = 2, ["teen"] = 3, ["char"] = 4, ["paanch"] = 5,
    };

    static readonly HashSet<string> Last = new() { "last", "past", "previous", "pichhle", "pichle", "pichhla", "pichla", "pichhli", "pichli" };
    static readonly HashSet<string> This = new() { "this", "is", "iss" };
    static readonly HashSet<string> Week = new() { "week", "hafte", "hafta", "hafton" };
    static readonly HashSet<string> MonthWord = new() { "month", "mahine", "mahina" };

    /// <summary>
    /// Finds the first time words in <paramref name="tokens"/> (lower-case words), removes them, and returns the
    /// span they name; null when there are none.
    /// </summary>
    public static CircleSpan? Take(List<string> tokens, DateTime now)
    {
        var today = now.Date;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (Match(tokens, i, today, out int used) is { } span)
            {
                tokens.RemoveRange(i, used);
                return span;
            }
        }
        return null;
    }

    static string At(List<string> t, int i) => i >= 0 && i < t.Count ? t[i] : "";

    static CircleSpan Days(DateTime from, int days) => new(from, from.AddDays(days));

    static CircleSpan? Match(List<string> t, int i, DateTime today, out int used)
    {
        string w = t[i], n1 = At(t, i + 1), n2 = At(t, i + 2);
        used = 1;
        switch (w)
        {
            case "day" when n1 == "before" && n2 == "yesterday":
                used = 3;
                return Days(today.AddDays(-2), 1);
            case "parso" or "parson" or "parsoon":
                return Days(today.AddDays(-2), 1);
            case "today" or "aaj" or "aj":
                if (n1 is "subah" or "morning") { used = 2; return new(today, today.AddHours(12)); }
                if (n1 is "shaam" or "sham" or "evening") { used = 2; return new(today.AddHours(17), today.AddDays(1)); }
                return Days(today, 1);
            case "tonight":
                return new(today.AddHours(17), today.AddDays(1));
            case "yesterday" or "yday" or "kal":
                if (n1 is "raat" or "rat" or "night") { used = 2; return new(today.AddHours(-6), today.AddHours(6)); }
                if (n1 is "subah" or "morning") { used = 2; return new(today.AddDays(-1), today.AddDays(-1).AddHours(12)); }
                if (n1 is "shaam" or "sham" or "evening") { used = 2; return new(today.AddDays(-1).AddHours(17), today); }
                return Days(today.AddDays(-1), 1);
        }

        if (This.Contains(w) || Last.Contains(w))
        {
            bool last = Last.Contains(w);
            used = 2;
            switch (n1)
            {
                case "morning" when !last: return new(today, today.AddHours(12));
                case "afternoon" when !last: return new(today.AddHours(12), today.AddHours(17));
                case "evening" when !last: return new(today.AddHours(17), today.AddDays(1));
                case "night" when last: return new(today.AddHours(-6), today.AddHours(6));
                case "week" or "hafte" or "hafta":
                    if (last) return new(today.AddDays(-7), today.AddDays(1));
                    int sinceMonday = ((int)today.DayOfWeek + 6) % 7;
                    return new(today.AddDays(-sinceMonday), today.AddDays(1));
                case "month" or "mahine" or "mahina":
                    var first = new DateTime(today.Year, today.Month, 1);
                    return last ? new(first.AddMonths(-1), first) : new(first, today.AddDays(1));
            }
            // "last 3 days", "past two weeks"
            if (last && Count(n1) is int count && count > 0)
            {
                used = 3;
                if (n2 is "days" or "day" or "din") return new(today.AddDays(1 - count), today.AddDays(1));
                if (Week.Contains(n2) || n2 == "weeks") return new(today.AddDays(-7 * count), today.AddDays(1));
            }
            // "last friday", "pichhle somvar"
            if (Weekdays.TryGetValue(n1, out var lastDay))
                return Days(MostRecent(today, lastDay, before: last), 1);
            used = 1;
        }

        // "3 days ago", "teen din pehle"
        if (Count(w) is int ago && ago > 0 && n1 is "days" or "day" or "din" && n2 is "ago" or "pehle" or "pahle" or "back")
        {
            used = 3;
            return Days(today.AddDays(-ago), 1);
        }
        if (Count(w) is int weeksAgo && weeksAgo > 0 && (n1 is "weeks" || Week.Contains(n1)) && n2 is "ago" or "pehle" or "pahle" or "back")
        {
            used = 3;
            return Days(today.AddDays(-7 * weeksAgo - 6), 7);
        }

        if (Weekdays.TryGetValue(w, out var day)) return Days(MostRecent(today, day, before: false), 1);

        // "14 nov", "14th november", "nov 14", "november 14th"
        if (DayNumber(w) is int d1 && Month(n1) is int m1)
        {
            used = 2;
            return DateSpan(today, m1, d1);
        }
        if (Month(w) is int m2 && DayNumber(n1) is int d2)
        {
            used = 2;
            return DateSpan(today, m2, d2);
        }
        used = 0;
        return null;
    }

    static int? Count(string w) =>
        int.TryParse(w, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : Numbers.TryGetValue(w, out int k) ? k : null;

    /// <summary>The latest such weekday on or before today (strictly before with <paramref name="before"/>).</summary>
    static DateTime MostRecent(DateTime today, DayOfWeek day, bool before)
    {
        int back = ((int)today.DayOfWeek - (int)day + 7) % 7;
        if (back == 0 && before) back = 7;
        return today.AddDays(-back);
    }

    static int? DayNumber(string w)
    {
        var digits = w.EndsWith("st") || w.EndsWith("nd") || w.EndsWith("rd") || w.EndsWith("th") ? w[..^2] : w;
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int d) && d is >= 1 and <= 31 ? d : null;
    }

    static int? Month(string w)
    {
        if (w.Length < 3) return null;
        for (int m = 0; m < 12; m++)
        {
            if (!w.StartsWith(MonthNames[m], StringComparison.Ordinal)) continue;
            // "mar" but not "march"-like words that aren't months ("marker"): the full name or its first letters only.
            var full = CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(m + 1).ToLowerInvariant();
            if (full.StartsWith(w, StringComparison.Ordinal) || w == "sept") return m + 1;
        }
        return null;
    }

    /// <summary>That day this year, or last year when it hasn't come yet (circles are in the past).</summary>
    static CircleSpan? DateSpan(DateTime today, int month, int day)
    {
        int year = today.Year;
        if (day > DateTime.DaysInMonth(year, month)) return null;
        var date = new DateTime(year, month, day);
        if (date > today)
        {
            year--;
            if (day > DateTime.DaysInMonth(year, month)) return null;
            date = new DateTime(year, month, day);
        }
        return Days(date, 1);
    }
}
