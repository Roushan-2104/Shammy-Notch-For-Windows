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
}
