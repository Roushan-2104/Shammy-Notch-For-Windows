using System.Globalization;
using System.Text;

namespace Shammy.Core.CircleMemory;

/// <summary>What someone asked of their circles: open the card, or find circles by words and time.</summary>
public enum CircleAsk { Open, Find }

/// <param name="Terms">The words left to match, lower-case ("flights", "whatsapp").</param>
/// <param name="Span">The time named ("yesterday"), or null for any time.</param>
public sealed record CircleQuery(CircleAsk Ask, IReadOnlyList<string> Terms, CircleSpan? Span)
{
    public bool IsEmpty => Terms.Count == 0 && Span is null;
}

/// <summary>
/// Finding circles on the PC, in the user's own words: "what did I circle yesterday about flights", "my circles",
/// "circles from WhatsApp", "kal kya circle kiya tha". The words are matched against what was circled, the answer,
/// the kind ("flights", "times", "photo"), the app and the date. Nothing is sent anywhere.
/// </summary>
public static class CircleSearch
{
    static readonly HashSet<string> CircleWords = new() { "circle", "circled", "circles", "circling" };

    static readonly HashSet<string> Me = new() { "i", "maine", "mene", "meine", "humne" };

    static readonly HashSet<string> HindiDid = new() { "kiya", "kiye", "kia", "kari", "kara", "kare", "kiyaa" };

    /// <summary>Whole phrases that just open the card.</summary>
    static readonly HashSet<string> OpenPhrases = new()
    {
        "circles", "my circles", "show circles", "show my circles", "open circles", "open my circles", "all circles",
        "all my circles", "show all circles", "show all my circles", "circle memory", "circle history", "my circle memory",
        "my circle history", "open circle memory", "show circle memory", "mere circles", "mere circles dikhao", "circles dikhao",
        "my circled things", "what have i circled", "what did i circle", "things i circled", "list my circles", "list circles",
        "recent circles", "my recent circles", "show me my circles",
    };

    static readonly HashSet<string> Stop = new()
    {
        "what", "whats", "which", "did", "do", "does", "done", "i", "me", "my", "mine", "circle", "circled", "circles", "circling",
        "about", "from", "in", "on", "at", "the", "a", "an", "of", "for", "to", "that", "this", "those", "these", "show", "find",
        "search", "open", "get", "all", "any", "was", "were", "is", "it", "have", "has", "had", "ago", "please", "shammy", "list",
        "memory", "history", "again", "back", "where", "when", "something", "anything", "thing", "things", "stuff", "earlier",
        "recently", "recent", "with", "some", "one", "ones", "can", "you", "tell", "remind", "remember", "look", "up", "and", "or",
        "kya", "kiya", "kiye", "kia", "kari", "kara", "kare", "tha", "thi", "the", "maine", "mene", "meine", "mera", "mere", "meri",
        "se", "ka", "ki", "ke", "mein", "main", "wala", "wali", "wale", "jo", "dikhao", "batao", "kaha", "kahan", "kuch", "koi",
        "hai", "ho", "hua", "app", "message", "messages", "chat", "screen",
    };

    /// <summary>
    /// Whether typed or spoken text is about the user's circles, and what it asks; null when it isn't (so "how do I
    /// draw a circle" still goes to the web). Needs a circle word with "I"/"my" right by it, a Hindi "circle kiya",
    /// or the text starting with "circles"/"circled".
    /// </summary>
    public static CircleQuery? ParseCommand(string text, DateTime now)
    {
        var tokens = Tokens(text);
        if (tokens.Count == 0) return null;
        var phrase = string.Join(' ', tokens);
        if (OpenPhrases.Contains(phrase)) return new CircleQuery(CircleAsk.Open, Array.Empty<string>(), null);

        bool about = false;
        for (int i = 0; i < tokens.Count && !about; i++)
        {
            if (!CircleWords.Contains(tokens[i])) continue;
            string prev = i > 0 ? tokens[i - 1] : "", next = i + 1 < tokens.Count ? tokens[i + 1] : "";
            about = Me.Contains(prev)                                       // "did I circle", "maine circle kiya"
                    || (prev is "my" or "mere" or "meri" && tokens[i] == "circles") // "my circles from WhatsApp"
                    || HindiDid.Contains(next)                              // "kal kya circle kiya tha"
                    || (i == 0 && tokens[i] is "circles" or "circled")      // "circles from WhatsApp"
                    || (prev is "find" or "search" && tokens[i] is "circles" or "circle"); // "find circle flights"
        }
        if (!about) return null;

        var query = Parse(tokens, now, CircleAsk.Find);
        return query.IsEmpty ? query with { Ask = CircleAsk.Open } : query;
    }

    /// <summary>The card's search box: any words, with the same time words and matching.</summary>
    public static CircleQuery ParseFilter(string text, DateTime now) => Parse(Tokens(text), now, CircleAsk.Find);

    static CircleQuery Parse(List<string> tokens, DateTime now, CircleAsk ask)
    {
        var span = CircleDates.Take(tokens, now);
        var terms = tokens.Where(t => !Stop.Contains(t)).Distinct().ToList();
        return new CircleQuery(ask, terms, span);
    }

    /// <summary>
    /// The circles that match, best first: in the time named, and matching every word when any circle does (else the
    /// most words). The title and app weigh more than the words, the answer and the kind; ties go to the newest.
    /// </summary>
    public static IReadOnlyList<RememberedCircle> Find(IEnumerable<RememberedCircle> circles, CircleQuery query, bool requireAll = false)
    {
        var pool = query.Span is { } span ? circles.Where(c => span.Contains(c.At)) : circles;
        if (query.Terms.Count == 0) return pool.OrderByDescending(c => c.At).ToList();

        var scored = new List<(RememberedCircle Circle, int Matched, int Score)>();
        foreach (var c in pool)
        {
            var fields = Fields(c);
            int matched = 0, score = 0;
            foreach (var term in query.Terms)
            {
                int best = 0;
                foreach (var (words, weight) in fields)
                    if (weight > best && Matches(words, term)) best = weight;
                if (best == 0) continue;
                matched++;
                score += best;
            }
            if (matched > 0) scored.Add((c, matched, score));
        }
        if (scored.Count == 0) return Array.Empty<RememberedCircle>();
        int top = scored.Max(s => s.Matched);
        int need = requireAll ? query.Terms.Count : top;
        return scored.Where(s => s.Matched >= need)
            .OrderByDescending(s => s.Matched).ThenByDescending(s => s.Score).ThenByDescending(s => s.Circle.At)
            .Select(s => s.Circle).ToList();
    }

    /// <summary>The words of each field, folded, with how much a match there counts.</summary>
    static (string[] Words, int Weight)[] Fields(RememberedCircle c) => new[]
    {
        (Words(c.Title), 3),
        (Words(c.App), 3),
        (Words(c.Words), 2),
        (Words(c.Line), 2),
        (Words(CircleKinds.Words(c.Kind) + " " + c.Kind), 2),
        (Words(c.At.ToString("dddd MMMM", CultureInfo.InvariantCulture)), 1),
    };

    /// <summary>A word of the field starts with the term (or the term without its plural ending).</summary>
    static bool Matches(string[] words, string term)
    {
        var stem = Stem(term);
        foreach (var w in words)
        {
            if (w.StartsWith(term, StringComparison.Ordinal) || (stem != term && w.StartsWith(stem, StringComparison.Ordinal))) return true;
            // Words run together ("6e2134" for "6E 2134", "whatsapp" inside "whatsappweb").
            if (term.Length >= 4 && w.Contains(term, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    static string Stem(string term)
    {
        if (term.Length > 4 && term.EndsWith("ies")) return term[..^3] + "y";
        if (term.Length > 4 && term.EndsWith("es") && term[^3] is 's' or 'x' or 'h') return term[..^2];
        if (term.Length > 3 && term.EndsWith('s') && !term.EndsWith("ss")) return term[..^1];
        return term;
    }

    static string[] Words(string? text) => Tokens(text).ToArray();

    /// <summary>Lower-case words without accents or punctuation; scripts without spaces stay as one word each run.</summary>
    internal static List<string> Tokens(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var folded = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(folded.Length);
        foreach (var ch in folded)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            {
                // Accents come off Latin letters ("café" → "cafe"); in other scripts the marks are part of the word
                // (Hindi vowel signs), so they stay.
                if (sb.Length > 0 && sb[^1] != ' ' && sb[^1] > 'ɏ') sb.Append(ch);
                continue;
            }
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (ch == '\'' || ch == '’') continue; // "what's" → "whats"
            else sb.Append(' ');
        }
        foreach (var t in sb.ToString().Normalize(NormalizationForm.FormC).Split(' ', StringSplitOptions.RemoveEmptyEntries)) result.Add(t);
        return result;
    }
}
