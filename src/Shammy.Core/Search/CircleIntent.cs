using System.Text.RegularExpressions;

namespace Shammy.Core.Search;

/// <summary>What the user wants done with what they circled.</summary>
public enum CircleAction { Translate, Search, Convert, AskAi, Copy, Track, Call, WhatsApp, Remind, Open }

/// <summary>
/// What the user said or typed while circling, in English or Hinglish ("translate karo", "hindi mein", "what time is
/// this for me", "is this vegan?"), read as one action. Pure and offline; never throws.
/// </summary>
/// <param name="Action">What to do.</param>
/// <param name="TargetLanguage">
/// The language they asked for, as an ISO 639-1 code ("hi" for "hindi mein", "en" for "in english"), or null when they
/// named none. It is set for any action, so "explain in hindi" is <see cref="CircleAction.AskAi"/> with "hi".
/// </param>
/// <param name="Question">Their own words, tidied (trimmed, spaces collapsed); what to ask the AI for <see cref="CircleAction.AskAi"/>.</param>
public sealed record CircleIntent(CircleAction Action, string? TargetLanguage, string Question)
{
    /// <summary>
    /// True for <see cref="CircleAction.Translate"/> when no language was named ("translate", "matlab", "meaning"): the
    /// caller picks English, or Hindi when the circled text is already English (<see cref="TargetFor"/> does that).
    /// </summary>
    public bool AutoLanguage { get; init; }

    /// <summary>True when they asked what it means ("meaning", "matlab", "what does this mean") rather than just to translate.</summary>
    public bool WantsMeaning { get; init; }

    /// <summary>The language to translate into: the one asked for, else English, or Hindi when the text is already English.</summary>
    /// <param name="sourceLanguage">The circled text's language code ("ja"), or null when unknown (read as not English).</param>
    public string TargetFor(string? sourceLanguage) =>
        TargetLanguage ?? (string.Equals(sourceLanguage, "en", StringComparison.OrdinalIgnoreCase) ? "hi" : "en");

    const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(50);

    static Regex Rx(string pattern) => new(pattern, Opt, Budget);

    static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["english"] = "en", ["angrezi"] = "en", ["angreji"] = "en", ["hindi"] = "hi", ["hindee"] = "hi", ["bengali"] = "bn",
        ["bangla"] = "bn", ["tamil"] = "ta", ["telugu"] = "te", ["marathi"] = "mr", ["gujarati"] = "gu", ["kannada"] = "kn",
        ["malayalam"] = "ml", ["punjabi"] = "pa", ["urdu"] = "ur", ["odia"] = "or", ["oriya"] = "or", ["assamese"] = "as",
        ["nepali"] = "ne", ["sanskrit"] = "sa", ["spanish"] = "es", ["french"] = "fr", ["german"] = "de", ["japanese"] = "ja",
        ["chinese"] = "zh", ["mandarin"] = "zh", ["korean"] = "ko", ["arabic"] = "ar", ["russian"] = "ru", ["portuguese"] = "pt",
        ["italian"] = "it", ["dutch"] = "nl", ["turkish"] = "tr", ["indonesian"] = "id", ["malay"] = "ms", ["thai"] = "th",
        ["vietnamese"] = "vi", ["persian"] = "fa", ["farsi"] = "fa", ["hebrew"] = "he", ["greek"] = "el", ["polish"] = "pl",
        ["ukrainian"] = "uk", ["swedish"] = "sv",
    };

    static readonly string LanguageNames = string.Join("|", Languages.Keys.OrderByDescending(k => k.Length));

    // "in hindi", "into English", "to french"; "hindi mein", "english me", "hindi main"; "hindi" said alone.
    static readonly Regex LanguageRx = Rx($@"(?:\b(?:in|into|to)\s+(?<l>{LanguageNames})\b|\b(?<l>{LanguageNames})\s+(?:mein|me|main|men|mai|mei)\b|^(?<l>{LanguageNames})$|\b(?<l>{LanguageNames})\s+(?:translation|translate|batao|bolo|karo)\b)");

    static readonly Regex ConvertRx = Rx(@"\b(?:what\s+time|my\s+time|local\s+time|in\s+my\s+time|my\s+timezone|convert|conversion|kitne\s+baje|kitna\s+baje|kab\s+hai\s+mere|mere\s+time|in\s+(?:rupees?|rs|inr|dollars?|usd|euros?|eur|pounds?|gbp|dirhams?|aed)|to\s+(?:rupees?|inr|dollars?|usd|euros?)|rupees?\s+(?:mein|me)|kitne\s+(?:rupa?y?e+s?|rs|dollars?))\b");
    static readonly Regex TranslateRx = Rx(@"\b(?:translate|translation|translated|anuvad|anuvaad|tarjuma)\b");
    static readonly Regex MeaningRx = Rx(@"\b(?:matlab|meaning|means?|arth)\b");
    static readonly Regex RemindRx = Rx(@"\b(?:remind|reminder|ya+d\s+dil(?:a+|ana|ao|a\s+dena|a\s+do)|ya+d\s+kara)\w*");
    /// <summary>Asking the AI to do something with it; with a language named ("explain in hindi") it is still the AI, answering in that language.</summary>
    static readonly Regex AskVerbRx = Rx(@"\b(?:explain|samjhao|samjha\s+do|samjhaiye|samjhana|summari[sz]e|summary|solve|fix|why|kyun|kyon|how\s+do\s+i|how\s+to|how\s+can\s+i|tell\s+me\s+about|answer|reply|write)\b");

    /// <summary>Polite openings that come before a command ("please copy", "can you open it", "hey Shammy, track this").</summary>
    static readonly Regex OpeningRx = Rx(@"^(?:(?:hey|hi|ok|okay)\s+shammy\s*,?\s*|shammy\s*,?\s*|please\s+|pls\s+|plz\s+|just\s+|now\s+|ok\s+|okay\s+|can\s+you\s+|could\s+you\s+|would\s+you\s+|will\s+you\s+|zara\s+|ise\s+|isko\s+|iska\s+|isse\s+|ye\s+|yeh\s+)+");

    static readonly (CircleAction Action, Regex Verb, Regex Hinglish)[] Commands =
    {
        (CircleAction.WhatsApp, Rx(@"^whats\s*app\b"), Rx(@"\bwhats\s*app\s+(?:karo|kar\s+do|kardo|bhejo|pe\s+bhejo|par\s+bhejo)\b")),
        (CircleAction.Call, Rx(@"^(?:call|ring|dial|phone)\b"), Rx(@"\b(?:call|phone|fone)\s+(?:karo|kar\s+do|kardo|lagao|laga\s+do|milao)\b")),
        (CircleAction.Copy, Rx(@"^copy\b"), Rx(@"\bcopy\s+(?:karo|kar\s+do|kardo|kar\s+lo|karlo)\b")),
        (CircleAction.Track, Rx(@"^track\b"), Rx(@"\btrack\s+(?:karo|kar\s+do|kardo)\b")),
        (CircleAction.Open, Rx(@"^(?:open|launch|go\s+to|visit)\b"), Rx(@"\b(?:open\s+(?:karo|kar\s+do|kardo)|kholo|khol\s+do|kholna)\b")),
        (CircleAction.Search, Rx(@"^(?:search|google|look\s+(?:it\s+)?up|look\s+for|find)\b"),
            Rx(@"\b(?:search\s+(?:karo|kar\s+do|kardo)|google\s+(?:karo|kar\s+do|kardo)|google\s+it|dhundo|dhundho|dhoondo|dhoondho|dhundh\s+do|khojo)\b")),
    };

    /// <summary>The action in <paramref name="spokenOrTyped"/>, or null when nothing was said.</summary>
    public static CircleIntent? Parse(string? spokenOrTyped)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(spokenOrTyped)) return null;
            var words = Regex.Replace(spokenOrTyped, @"\s+", " ").Trim();
            // Hindi said in Devanagari reads the same once spelt out ("हिंदी में" is "hindi mein").
            var t = Devanagari.Contains(words) ? Devanagari.ToLatin(words).Replace("ee", "i").Replace("aa", "a") : words;
            t = Regex.Replace(t.ToLowerInvariant(), @"[^\p{L}\p{N}'\s?]", " ");
            t = Regex.Replace(t, @"\s+", " ").Trim();
            if (t.Length == 0) return new CircleIntent(CircleAction.AskAi, null, words);

            string? language = LanguageRx.Match(t) is { Success: true } lm && Languages.TryGetValue(lm.Groups["l"].Value, out var code) ? code : null;
            var bare = OpeningRx.Replace(t, "").Trim(' ', '?');
            bool question = t.EndsWith('?') || Regex.IsMatch(bare, @"^(?:is|are|does|do|did|can|could|will|would|should|was|were|has|have|which|who|whom|whose|where|when|how)\b", Opt, Budget);

            if (ConvertRx.IsMatch(t)) return new CircleIntent(CircleAction.Convert, language, words);

            bool meaning = MeaningRx.IsMatch(t);
            bool asking = AskVerbRx.IsMatch(t);
            // "explain in hindi" asks the AI, in Hindi; "in hindi", "translate karo", "iska matlab kya hai" translate.
            if (TranslateRx.IsMatch(t) || meaning || (language is not null && !asking))
                return new CircleIntent(CircleAction.Translate, language, words) { AutoLanguage = language is null, WantsMeaning = meaning };

            if (RemindRx.IsMatch(t)) return new CircleIntent(CircleAction.Remind, language, words);

            int count = bare.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            foreach (var (action, verb, hinglish) in Commands)
            {
                // A command opens what they said ("copy this", "please open it") or is said the Hinglish way
                // ("isko copy karo"); "is this open on Sunday?" is a question, not a command.
                if (hinglish.IsMatch(t) || (!question && verb.IsMatch(bare)) || (count <= 2 && verb.IsMatch(bare)))
                    return new CircleIntent(action, language, words);
            }

            // "explain", "kya hai", "fix", "why", "summarise", or anything else asked: the AI, in their words.
            return new CircleIntent(CircleAction.AskAi, language, words);
        }
        catch (Exception)
        {
            return string.IsNullOrWhiteSpace(spokenOrTyped) ? null : new CircleIntent(CircleAction.AskAi, null, spokenOrTyped.Trim());
        }
    }
}
