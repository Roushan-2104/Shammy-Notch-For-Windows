using System.Text.RegularExpressions;

namespace Shammy.Core.Search;

/// <summary>Why an English transcript is worth a second listen in Hindi, or None when it looks fine.</summary>
public enum TranscriptDoubt { None, OtherScript, LowConfidence, Hallucination, Hinglish }

/// <summary>
/// Decides when an English-only transcript probably isn't what was said. Whisper forced to English turns
/// Hinglish into Urdu script, into mush ("Kalsubah Sathbaje Ka Alam Lagadu"), or into a fluent
/// mistranslation; those are the cases worth re-running in Hindi. Plain English must never trip this,
/// because every re-run costs another full pass.
/// </summary>
public static class TranscriptCheck
{
    /// <summary>
    /// Average token probability below which an English pass is doubted. Clean English commands score about
    /// 0.8-0.9 on the turbo model; garbled Hinglish often lands near 0.5-0.6.
    /// </summary>
    public const float LowConfidence = 0.6f;

    // What Whisper says over silence or noise (learned from subtitled videos).
    static readonly Regex KnownHallucination = new(
        @"^(?:thank\s+you(?:\s+(?:so\s+much|very\s+much|for\s+watching))?|thanks\s+for\s+watching|you|bye(?:\s+bye)?|okay|" +
        @"please\s+subscribe(?:\s+to\s+(?:my|our)\s+channel)?|subscribe|subtitles?\s+by\b.*|translated\s+by\b.*|" +
        @"see\s+you\s+(?:next\s+time|in\s+the\s+next\s+video)|i'?ll\s+see\s+you\s+(?:next\s+time|in\s+the\s+next\s+video))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static readonly Regex Letters = new(@"\p{L}", RegexOptions.CultureInvariant);
    static readonly Regex Latin = new(@"\p{IsBasicLatin}|\p{IsLatin-1Supplement}|\p{IsLatinExtended-A}|\p{IsLatinExtended-B}|\p{IsLatinExtendedAdditional}", RegexOptions.CultureInvariant);

    // Hindi words that English never uses, as Whisper tends to spell them. Two of them make a sentence Hinglish.
    static readonly HashSet<string> HindiWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "hai", "hain", "ka", "ki", "ke", "ko", "mein", "mujhe", "mera", "meri", "mere", "kya", "kaise", "kaisa", "kaisi",
        "karo", "karna", "kardo", "kar", "dena", "dijiye", "batao", "bata", "bataiye", "laga", "lagao", "lagado", "lagadu",
        "lagadoo", "lagaa", "bajao", "bajau", "aaj", "abhi", "nahi", "nahin", "kitna", "kitne", "kahan", "kab", "wala",
        "wali", "wale", "yaar", "achha", "acha", "accha", "achcha", "subah", "shaam", "baje", "bajay", "bajey", "raat",
        "kal", "parso", "ghante", "ghanta", "yaad", "dilana", "dila", "sunao", "chalao", "chahiye", "kuch", "koi", "aur",
        "bhi", "toh", "hum", "tum", "aap", "apna", "apni", "liye", "wahan", "yahan", "kaun", "kyun", "mausam",
    };

    // English words among those that would make an ordinary sentence look Hinglish ("kal" is a name, "kar" a
    // misspelt "car"); they only count alongside a clearly Hindi word.
    static readonly HashSet<string> Ambiguous = new(StringComparer.OrdinalIgnoreCase) { "ka", "ki", "ko", "kar", "kal", "hum", "tum", "koi", "bhi", "aur" };

    /// <summary>
    /// Whether an English-language transcript should be re-checked in Hindi, and why.
    /// <paramref name="probability"/> is Whisper's average token probability (negative if unknown).
    /// </summary>
    public static TranscriptDoubt Assess(string text, float probability)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return TranscriptDoubt.None;
        if (IsMostlyOtherScript(t)) return TranscriptDoubt.OtherScript;
        if (KnownHallucination.IsMatch(t.Trim('.', '!', '?', ' ', ','))) return TranscriptDoubt.Hallucination;
        if (LooksHinglish(t)) return TranscriptDoubt.Hinglish;
        if (probability >= 0 && probability < LowConfidence) return TranscriptDoubt.LowConfidence;
        return TranscriptDoubt.None;
    }

    /// <summary>True when most letters are not Latin (Urdu, Devanagari...).</summary>
    public static bool IsMostlyOtherScript(string text)
    {
        int letters = Letters.Matches(text).Count;
        if (letters == 0) return false;
        int latin = text.Count(c => char.IsLetter(c) && Latin.IsMatch(c.ToString()));
        return latin * 2 < letters;
    }

    /// <summary>True for Latin text with at least two Hindi words, one of them unambiguous.</summary>
    public static bool LooksHinglish(string text)
    {
        var words = Regex.Split(text.ToLowerInvariant(), @"[^\p{L}]+").Where(w => w.Length > 0).ToList();
        var hindi = words.Where(HindiWords.Contains).ToList();
        return hindi.Count >= 2 && hindi.Any(w => !Ambiguous.Contains(w));
    }

    /// <summary>
    /// Whether the Hindi re-run should replace the English transcript. Text in another script never beats
    /// something readable unless the English was in another script too; otherwise the likelier one wins.
    /// </summary>
    public static bool PreferRetry(TranscriptDoubt doubt, string english, float englishProbability, string retry, float retryProbability)
    {
        if (string.IsNullOrWhiteSpace(retry)) return false;
        if (doubt is TranscriptDoubt.OtherScript or TranscriptDoubt.Hallucination) return true;
        if (string.IsNullOrWhiteSpace(english)) return true;
        return retryProbability >= englishProbability;
    }
}
