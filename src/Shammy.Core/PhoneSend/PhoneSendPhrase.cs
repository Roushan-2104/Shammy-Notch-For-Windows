using System.Text;
using System.Text.RegularExpressions;

namespace Shammy.Core.PhoneSend;

/// <summary>Understands "send to phone" / "send report.pdf to my phone" and finds the shelf file meant.</summary>
public static class PhoneSendPhrase
{
    static readonly Regex Pattern = new(
        @"^(?:please\s+)?(?:send|share|airdrop|transfer)\s+(?:(?<q>.+?)\s+)?to\s+(?:my\s+|the\s+)?(?:phone|mobile|iphone|android)(?:\s+please)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static readonly HashSet<string> MostRecentWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "it", "this", "that", "file", "this file", "that file", "the file", "my file", "latest", "the latest",
        "latest file", "the latest file", "last file", "the last file", "most recent", "the most recent file",
        "recent file", "the recent file",
    };

    /// <summary>Words that name nothing in particular: "send this to my phone" asks what to send.</summary>
    static readonly HashSet<string> AskWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "it", "this", "that", "this one", "that one", "something", "anything", "stuff", "things",
    };

    static readonly HashSet<string> Filler =new(StringComparer.OrdinalIgnoreCase) { "the", "my", "file", "dot", "a" };

    /// <summary>
    /// True if the text is a send-to-phone command. <paramref name="query"/> is the file the user named,
    /// or null for "the most recent shelf item".
    /// </summary>
    public static bool TryParse(string? text, out string? query)
    {
        query = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = Pattern.Match(text.Trim().TrimEnd('.', '!', '?').Trim());
        if (!m.Success) return false;
        var q = m.Groups["q"].Success ? m.Groups["q"].Value.Trim().Trim('"', '\'', '“', '”') : "";
        query = q.Length == 0 || MostRecentWords.Contains(q) ? null : q;
        return true;
    }

    /// <summary>
    /// True for a send-to-phone command that names nothing ("send to phone", "send this to my phone"): the choice
    /// sheet asks what to send. "send the latest file to my phone" still means the newest shelf file, and a named file
    /// is looked up on the shelf, so both are false.
    /// </summary>
    public static bool AsksWhat(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = Pattern.Match(text.Trim().TrimEnd('.', '!', '?').Trim());
        if (!m.Success) return false;
        var q = m.Groups["q"].Success ? m.Groups["q"].Value.Trim().Trim('"', '\'', '“', '”') : "";
        return q.Length == 0 || AskWords.Contains(q);
    }

    /// <summary>
    /// Index into <paramref name="names"/> (oldest first, as the shelf stores them) of the best match for
    /// <paramref name="query"/>, preferring the most recent among equals; -1 when nothing fits.
    /// A null query means the most recent item.
    /// </summary>
    public static int FindItem(IReadOnlyList<string> names, string? query)
    {
        if (names.Count == 0) return -1;
        if (query is null) return names.Count - 1;

        var q = query.Trim();
        var qNorm = Normalize(q);
        var qWords = qNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !Filler.Contains(w)).ToArray();
        int best = -1, bestRank = 0;
        for (int i = names.Count - 1; i >= 0; i--)
        {
            var name = names[i];
            var norm = Normalize(name);
            int rank =
                name.Equals(q, StringComparison.OrdinalIgnoreCase) ? 5
                : Path.GetFileNameWithoutExtension(name).Equals(q, StringComparison.OrdinalIgnoreCase) ? 4
                : qNorm.Length > 0 && norm.StartsWith(qNorm, StringComparison.Ordinal) ? 3
                : qNorm.Length > 0 && norm.Contains(qNorm, StringComparison.Ordinal) ? 2
                : qWords.Length > 0 && qWords.All(w => norm.Split(' ').Any(n => n.StartsWith(w, StringComparison.Ordinal))) ? 1
                : 0;
            if (rank > bestRank)
            {
                best = i;
                bestRank = rank;
            }
        }
        return best;
    }

    /// <summary>True if the query looks like a filename ("notes.txt"), so a miss should be reported rather than searched.</summary>
    public static bool LooksLikeFileName(string query) => Regex.IsMatch(query, @"\.[A-Za-z0-9]{1,8}$");

    /// <summary>Lower-case letters and digits separated by single spaces: "Q3_Report-final.PDF" -> "q3 report final pdf".</summary>
    static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (var c in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                if (space && sb.Length > 0) sb.Append(' ');
                sb.Append(c);
                space = false;
            }
            else
            {
                space = true;
            }
        }
        return sb.ToString();
    }
}
