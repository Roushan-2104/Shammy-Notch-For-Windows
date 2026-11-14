using System.Globalization;

namespace Shammy.Core.Search;

/// <summary>How an answer's big line reads.</summary>
public static class AnswerText
{
    /// <summary>
    /// True when the line is a value - a number, an amount, a time or a measurement ("100%", "9:45 AM", "152.4 cm",
    /// "₹9,425") - which the island sets light in its display face, like the numbers on its home. Words ("Reminder set",
    /// "Alarm · 6:30 AM") stay bold.
    /// </summary>
    public static bool IsValue(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0 || t.Length > 16) return false;
        int i = 0;
        // A leading sign or currency symbol ("-4", "$12", "₹9,425").
        while (i < t.Length && (t[i] is '+' or '-' or '−' or '~' or '≈' || CharUnicodeInfo.GetUnicodeCategory(t[i]) == UnicodeCategory.CurrencySymbol)) i++;
        if (i >= t.Length || !char.IsDigit(t[i])) return false;
        // At most a number and a short unit after it.
        return t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 2;
    }

    static readonly string[] Markers = { "AI Mode reply for", "AI Overview" };

    // The page's own notes and the chatbot's offers at the end ("Would you like a recipe?") are not the answer.
    static readonly string[] Skipped = { "AI can make mistakes", "AI responses may make mistakes" };
    static readonly string[] FollowUps = { "Would you like", "Do you want", "Let me know", "Feel free", "I can also", "If you'd like" };

    /// <summary>
    /// The short answer in a Google page's visible text: its first two real lines under "AI Mode reply for" or
    /// "AI Overview", or null. The same rule as the live probe in the app's hidden page.
    /// </summary>
    public static string? FromPage(string? pageText)
    {
        if (string.IsNullOrEmpty(pageText)) return null;
        int at = -1;
        foreach (var marker in Markers)
        {
            int k = pageText.IndexOf(marker, StringComparison.Ordinal);
            if (k >= 0)
            {
                at = pageText.IndexOf('\n', k);
                break;
            }
        }
        if (at < 0) return null;
        var lines = pageText[(at + 1)..].Split('\n').Select(s => s.Trim())
            .Where(s => (s.Length >= 40 || (s.Length >= 6 && ".!?。)\"”".Contains(s[^1])))
                        && !Skipped.Any(p => s.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                        && !FollowUps.Any(p => s.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                        && !s.StartsWith("Thinking", StringComparison.Ordinal) && !s.StartsWith("Searching", StringComparison.Ordinal))
            .Take(2).ToList();
        return lines.Count > 0 ? string.Join("\n", lines) : null;
    }
}
