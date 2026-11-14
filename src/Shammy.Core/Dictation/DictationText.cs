using System.Text;
using System.Text.RegularExpressions;
using Shammy.Core.Search;

namespace Shammy.Core.Dictation;

/// <summary>
/// What dictation types, worked out from what Gnani heard: Hindi in Roman letters unless "in Hindi" was said, the
/// trigger words taken out, and the light clean-up (punctuation, capitals, no fillers or stutters). Never rephrases.
/// </summary>
public static class DictationText
{
    const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Letters and their marks: Devanagari vowel signs are spacing marks, which \b and \w don't count as letters.
    const string NotLetterBefore = @"(?<![\p{L}\p{M}\p{N}'’])", NotLetterAfter = @"(?![\p{L}\p{M}\p{N}'’])";
    const string HindiWord = "(?:hindi|हिंदी|हिन्दी)";

    /// <summary>"in Hindi" or "Hindi mein", in Roman letters or in Devanagari, with the commas around it.</summary>
    static readonly Regex HindiTrigger = new(
        @"[,;]?\s*" + NotLetterBefore + "(?:(?:in|इन)\\s+" + HindiWord + "|" + HindiWord + @"\s+(?:mein|mei|me|main|mai|में|मे|मैं))" + NotLetterAfter + @"\s*[,;]?",
        Opts);

    static readonly Regex Filler = new(NotLetterBefore + @"(?:u+m+|u+h+|er|erm|h+m+)" + NotLetterAfter + @"[,.]?", Opts);
    static readonly Regex Spaces = new(@"\s+");
    static readonly Regex SpaceBeforeMark = new(@"\s+([,.!?;:।…])");
    static readonly Regex LeadingMarks = new(@"^[\s,;:.]+");
    static readonly Regex DoubledCommas = new(@"([,;])(\s*[,;])+");
    static readonly Regex LoneI = new(NotLetterBefore + "i(?=$|[\\s,.!?;:'’])");
    static readonly Regex SentenceStart = new(@"(^|[.!?]\s+)(\p{Ll})");

    /// <summary>Hindi doubles words on purpose ("kabhi kabhi", "jaldi jaldi"); those pairs stay.</summary>
    static readonly HashSet<string> MeantTwice = new(StringComparer.OrdinalIgnoreCase)
    {
        "kabhi", "jaldi", "dheere", "dhire", "alag", "thoda", "thodi", "bahut", "saath", "sath", "kya", "kaun", "kahan",
        "ghar", "baar", "roz", "haan", "nahi", "very", "bye", "कभी", "जल्दी", "धीरे", "अलग", "थोड़ा", "बहुत", "साथ", "क्या",
    };

    /// <summary>Whether the words ask for Devanagari ("… in Hindi", "Hindi mein …").</summary>
    public static bool AsksForHindi(string? text) => !string.IsNullOrEmpty(text) && HindiTrigger.IsMatch(text);

    /// <summary>The words without "in Hindi" / "Hindi mein".</summary>
    public static string RemoveHindiTrigger(string? text) =>
        string.IsNullOrEmpty(text) ? "" : Tidy(HindiTrigger.Replace(text, " "));

    /// <summary>The words shown while speaking: no trigger, Roman letters, one line of spaces.</summary>
    public static string Live(string? heard) => Search.Devanagari.ToLatin(RemoveHindiTrigger(heard));

    /// <summary>
    /// What gets typed: the trigger taken out, Roman letters unless <paramref name="hindi"/>, and the light clean-up
    /// when <paramref name="light"/> is on (otherwise only the spacing is tidied).
    /// </summary>
    public static string Finish(string? heard, bool hindi, bool light)
    {
        var t = RemoveHindiTrigger(heard);
        if (!hindi) t = Search.Devanagari.ToLatin(t);
        return light ? Clean(t) : Tidy(t);
    }

    /// <summary>
    /// Light clean-up: drops fillers (um, uh, er, hmm), says a word that came out twice once ("I I think"), fixes the
    /// spaces around punctuation, capitals at the start of each sentence (and "I"), and a full stop at the end when
    /// there is no end mark. Gnani's own punctuation stays; nothing is reworded.
    /// </summary>
    public static string Clean(string? text)
    {
        var t = Tidy(text);
        if (t.Length == 0) return "";
        t = Tidy(Filler.Replace(t, " "));
        t = DoubledCommas.Replace(t, "$1");
        t = OnceEach(t);
        t = SpaceBeforeMark.Replace(t, "$1");
        t = LeadingMarks.Replace(t, "");
        if (t.Length == 0) return "";
        t = LoneI.Replace(t, "I");
        t = SentenceStart.Replace(t, m => m.Groups[1].Value + m.Groups[2].Value.ToUpperInvariant());
        char last = t[^1];
        if (last is ',' or ';' or ':') t = t[..^1].TrimEnd();
        if (t.Length > 0 && t[^1] is not ('.' or '!' or '?' or '।' or '…')) t += ".";
        return t;
    }

    /// <summary>The number of words (what the log and the "Typed" pill say; never the words themselves).</summary>
    public static int Words(string? text) => string.IsNullOrWhiteSpace(text) ? 0 : text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    static string Tidy(string? text) => string.IsNullOrWhiteSpace(text) ? "" : Spaces.Replace(text, " ").Trim();

    /// <summary>"I I think" → "I think": a word straight after itself goes (the second one, with its punctuation, stays).</summary>
    static string OnceEach(string text)
    {
        var words = text.Split(' ');
        var kept = new List<string>(words.Length);
        foreach (var w in words)
        {
            var prev = kept.Count > 0 ? kept[^1] : "";
            var before = Bare(prev);
            // Only when the earlier one has no punctuation after it: "no, no" stays.
            if (before.Length > 0 && before.Length == prev.Length && string.Equals(before, Bare(w), StringComparison.OrdinalIgnoreCase)
                && !MeantTwice.Contains(before))
            {
                kept[^1] = char.IsUpper(prev[0]) && !char.IsUpper(w[0]) ? char.ToUpperInvariant(w[0]) + w[1..] : w;
                continue;
            }
            kept.Add(w);
        }
        var sb = new StringBuilder(text.Length);
        foreach (var w in kept)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(w);
        }
        return sb.ToString();
    }

    static string Bare(string word) => word.TrimEnd(',', '.', '!', '?', ';', ':', '।', '…');
}

/// <summary>
/// When to ask for the live words again while someone is still talking: the growing recording goes up at most
/// every <see cref="Every"/>, only when nothing is on its way already, and only when there was speech since.
/// </summary>
public sealed class LiveWordsPacer
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(1.5);

    long _sentMs;
    bool _inFlight;

    public LiveWordsPacer(long startMs) => _sentMs = startMs;

    public bool InFlight => _inFlight;

    public bool Due(long nowMs, bool spokeSince) => !_inFlight && spokeSince && nowMs - _sentMs >= (long)Every.TotalMilliseconds;

    public void Sent(long nowMs)
    {
        _inFlight = true;
        _sentMs = nowMs;
    }

    public void Answered() => _inFlight = false;
}
