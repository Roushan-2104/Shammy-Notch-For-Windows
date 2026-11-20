using System.Text.RegularExpressions;

namespace Shammy.Core.Reports;

/// <summary>
/// Cleans text before it can leave the PC in a report: the Windows user and PC names, file paths, links, emails and
/// long numbers (phone, OTP, card, PIN code) become placeholders. Times, dates, versions and small counts stay, since
/// they're what makes a log useful. The relay runs the same rules again (relay/src/report.js).
/// </summary>
public static class ReportText
{
    const RegexOptions Opts = RegexOptions.CultureInvariant;
    static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);

    static readonly Regex DateOrTime = new(
        @"\b\d{4}-\d{2}-\d{2}(?:[ T]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?)?\b|\b\d{1,2}:\d{2}(?::\d{2}(?:\.\d+)?)?\b", Opts, Timeout);
    static readonly Regex Email = new(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+", Opts, Timeout);
    static readonly Regex Link = new(@"\b(?:https?|ftp)://\S+|\bwww\.\S+", Opts | RegexOptions.IgnoreCase, Timeout);
    // Folders may have spaces ("C:\Users\Jane Doe\AppData\..."): anything up to each backslash is a folder; only the
    // last part (the file) ends at a space. Over-removing a sentence that happens to end in a backslash is fine.
    static readonly Regex DrivePath = new(@"\b[a-zA-Z]:\\(?:[^\\\r\n""'<>|:*?]*\\)*[^\s\\""'<>|:*?]*", Opts, Timeout);
    static readonly Regex SharePath = new(@"\\\\[^\s""'<>|]+", Opts, Timeout);
    static readonly Regex LongNumber = new(@"\d[\d -]{2,}\d", Opts, Timeout);
    static readonly Regex Kept = new("\u0001(\\d+)\u0002", Opts, Timeout);

    /// <param name="privateWords">Words known to identify this person or PC (user name, PC name), removed wherever they appear.</param>
    public static string Clean(string text, IEnumerable<string>? privateWords = null)
    {
        if (string.IsNullOrEmpty(text)) return "";
        try
        {
            // Names first, wherever they are: a path or an email cut short must not leave half a name behind.
            var s = text.Replace("\u0001", "").Replace("\u0002", "");
            if (privateWords is not null)
            {
                // Longest first, so "Jane Doe" goes before "Jane" can split it.
                foreach (var word in privateWords.Where(w => w.Trim().Length >= 3).Select(w => w.Trim()).Distinct().OrderByDescending(w => w.Length))
                    s = Regex.Replace(s, Regex.Escape(word), "‹name›", RegexOptions.IgnoreCase | Opts, Timeout);
            }
            // Dates and times stay: set them aside so the long-number rule can't take them.
            var kept = new List<string>();
            s = DateOrTime.Replace(s, m =>
            {
                kept.Add(m.Value);
                return $"\u0001{kept.Count - 1}\u0002";
            });
            s = Email.Replace(s, "‹email›");
            s = Link.Replace(s, "‹link›");
            s = DrivePath.Replace(s, "‹path›");
            s = SharePath.Replace(s, "‹path›");
            s = LongNumber.Replace(s, m => m.Value.Count(char.IsAsciiDigit) >= 4 ? "‹number›" : m.Value);
            return Kept.Replace(s, m => int.TryParse(m.Groups[1].Value, out var i) && i < kept.Count ? kept[i] : "");
        }
        catch (RegexMatchTimeoutException)
        {
            // Something too odd to clean safely doesn't go at all.
            return "‹removed›";
        }
    }

    /// <summary>How many of each kind of thing <see cref="Clean"/> removed, for the line under the preview.</summary>
    public static IReadOnlyDictionary<string, int> Removed(IEnumerable<string> cleanedLines)
    {
        var counts = new Dictionary<string, int>();
        foreach (var line in cleanedLines)
        {
            foreach (Match m in Regex.Matches(line, "‹(email|link|path|name|number)›", Opts, Timeout))
                counts[m.Groups[1].Value] = counts.GetValueOrDefault(m.Groups[1].Value) + 1;
        }
        return counts;
    }

    /// <summary>"2 file paths, 1 name": what <see cref="Removed"/> found, in words; empty for nothing.</summary>
    public static string RemovedInWords(IReadOnlyDictionary<string, int> counts)
    {
        var words = new (string Key, string One, string Many)[]
        {
            ("name", "name", "names"), ("path", "file path", "file paths"), ("link", "link", "links"),
            ("email", "email", "emails"), ("number", "long number", "long numbers"),
        };
        return string.Join(", ", words.Where(w => counts.GetValueOrDefault(w.Key) > 0)
            .Select(w => counts[w.Key] == 1 ? $"1 {w.One}" : $"{counts[w.Key]} {w.Many}"));
    }
}
