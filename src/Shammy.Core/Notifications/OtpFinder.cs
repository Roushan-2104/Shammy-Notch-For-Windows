using System.Text.RegularExpressions;

namespace Shammy.Core.Notifications;

/// <summary>
/// Finds the one-time code in a message: "482913 is your OTP for login", "Your verification code: 4821",
/// "G-123456 is your Google verification code", "Use 739 201 to sign in", "आपका OTP 5521 है". Only when the
/// message says it is a code (OTP, verification, security, sign-in, passcode…); amounts (₹500, Rs 1,299),
/// phone numbers, card endings and years are never taken for one.
/// </summary>
public static class OtpFinder
{
    const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    static readonly Regex Says = new(@"\b(?:otp|one[\s-]?time\s+(?:password|passcode|code|pin)|verification\s+code|verify|security\s+code|" +
        @"(?:login|log-in|sign[\s-]?in|signin|auth(?:entication)?|confirmation|access)\s+code|passcode|2fa|two[\s-]factor|code\s+is|your\s+code|" +
        @"is\s+your\s+(?:\w+\s+)?(?:code|pin|otp)|pin\s+(?:is|for)|use\s+code)\b|ओटीपी|कोड|OTP", Opts);

    // 4 to 8 digits, possibly split in two halves by a space or a dash ("739 201", "482-913"), not part of a longer number.
    static readonly Regex Code = new(@"(?<![\d.,₹$€£\w])(?:[A-Z]-)?(\d{3,4})[\s-](\d{3,4})(?!\d|[.,]\d|\s?(?:%|rs|inr))|(?<![\d.,₹$€£\w])(?:[A-Z]-)?(\d{4,8})(?!\d|[.,]\d|\s?(?:%|rs|inr))", Opts);

    static readonly Regex Money = new(@"(?:₹|rs\.?|inr|\$|€|£)\s*$", Opts);
    static readonly Regex CardEnding = new(@"(?:ending|ends\s+with|xx+|\*{2,}|a/c|account|card)\s*(?:no\.?|number)?\s*[:\-]?\s*$", Opts);

    /// <summary>The code in a notification's title and body, digits only; null when there isn't one.</summary>
    public static string? Find(string? title, string? body)
    {
        var text = $"{title} {body}".Trim();
        if (text.Length == 0 || !Says.IsMatch(text)) return null;
        string? best = null;
        int bestScore = int.MinValue;
        foreach (Match m in Code.Matches(text))
        {
            var digits = m.Groups[3].Success ? m.Groups[3].Value : m.Groups[1].Value + m.Groups[2].Value;
            if (digits.Length is < 4 or > 8) continue;
            var before = text[..m.Index];
            if (Money.IsMatch(before) || CardEnding.IsMatch(before)) continue;
            // A year on its own ("2026") isn't a code unless nothing else is.
            int score = digits.Length == 6 ? 3 : digits.Length is 4 or 5 or 8 ? 2 : 1;
            if (digits.Length == 4 && Regex.IsMatch(digits, "^(19|20)\\d\\d$")) score -= 3;
            // Closer to the word that says it is a code is better.
            var says = Says.Match(text);
            score -= Math.Min(3, Math.Abs(m.Index - says.Index) / 40);
            if (score > bestScore)
            {
                bestScore = score;
                best = digits;
            }
        }
        return bestScore > -1 ? best : null;
    }

    /// <summary>"482 913" for reading at a glance (6 digits split in two; others as they are).</summary>
    public static string Spaced(string code) => code.Length == 6 ? code[..3] + " " + code[3..] : code.Length == 8 ? code[..4] + " " + code[4..] : code;
}
