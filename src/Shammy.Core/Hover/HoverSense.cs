using System.Globalization;
using System.Text.RegularExpressions;
using Shammy.Core.Conversions;
using Shammy.Core.Search;

namespace Shammy.Core.Hover;

/// <summary>What a hovered word or run is, and so what the bubble answers with.</summary>
public enum HoverKind
{
    /// <summary>Text in another language (a word or a line): its English translation, online.</summary>
    Translation,

    /// <summary>A price: in rupees (dollars when it's already rupees), on the PC with saved rates.</summary>
    Price,

    /// <summary>A measurement: metric or imperial, on the PC.</summary>
    Measure,

    /// <summary>A clock time with a zone: the PC's own time.</summary>
    Time,

    /// <summary>One English word: its meaning in a short line, online.</summary>
    Meaning,
}

/// <summary>A hovered thing, read on the PC. <see cref="Bounds"/> is where it sits in the OCR picture (empty for typed text).</summary>
public sealed record HoverTarget(HoverKind Kind, string Text, Box Bounds)
{
    /// <summary>For <see cref="HoverKind.Price"/>.</summary>
    public Money? Money { get; init; }

    /// <summary>For <see cref="HoverKind.Measure"/>.</summary>
    public UnitConversion? Conversion { get; init; }

    /// <summary>For <see cref="HoverKind.Time"/>: CircleSense's reading, with the local time and the day.</summary>
    public CircleMeaning? Time { get; init; }

    /// <summary>For <see cref="HoverKind.Translation"/>: the language's English name when it is known on the PC ("German"); null when only Google will know.</summary>
    public string? Language { get; init; }
}

/// <summary>
/// Hover to understand, the part that needs no screen: which words sit under the pointer in what OCR read around it,
/// and what they are. Only a price, a measurement, a time with a zone, text in another language or one English word
/// is answered; everything else is null, so the bubble stays quiet rather than guess. Never throws.
/// </summary>
public static class HoverSense
{
    /// <summary>The longest run tried around the pointer for "5 ft 10 in" or "3:30 PM PST" (OCR splits them into words).</summary>
    const int MaxRun = 5;

    /// <summary>How much text another language's line may be; more is cut to the words nearest the pointer.</summary>
    const int MaxLineWords = 16;

    const int MaxText = 160;

    static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(50);
    static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant, Budget);
    static readonly Regex WordRx = new(@"^[\p{L}][\p{L}\p{M}'’\-]*$", RegexOptions.CultureInvariant, Budget);

    /// <summary>
    /// The thing under the point (<paramref name="x"/>, <paramref name="y"/>, in the OCR picture's pixels), or null when
    /// the pointer is between words or on something the bubble doesn't answer.
    /// </summary>
    /// <param name="isEnglishWord">The PC's English spelling check: true, false, or null when it can't tell (no checker).</param>
    public static HoverTarget? Pick(IReadOnlyList<OcrWord> words, double x, double y, DateTime now,
        Func<string, bool?>? isEnglishWord = null, CultureInfo? culture = null, TimeZoneInfo? zone = null)
    {
        try
        {
            if (words.Count == 0) return null;
            var hit = HitWord(words, x, y);
            if (hit is null) return null;
            var line = words.Where(w => w.Line == hit.Line).OrderBy(w => w.Bounds.X).ToList();
            int at = line.IndexOf(hit);

            // A number with its unit, currency or zone first: the longest run around the pointer that is exactly one.
            for (int len = Math.Min(MaxRun, line.Count); len >= 1; len--)
            {
                for (int start = Math.Max(0, at - len + 1); start <= at && start + len <= line.Count; start++)
                {
                    var run = line.GetRange(start, len);
                    if (Exact(Join(run), now, culture, zone) is { } exact) return exact with { Bounds = Union(run) };
                }
            }

            // Another script: the whole run of it on this line ("a foreign word or line").
            if (IsOtherScript(hit.Text))
            {
                int a = at, b = at;
                while (a > 0 && IsOtherScript(line[a - 1].Text) && b - a + 1 < MaxLineWords) a--;
                while (b < line.Count - 1 && IsOtherScript(line[b + 1].Text) && b - a + 1 < MaxLineWords) b++;
                var run = line.GetRange(a, b - a + 1);
                return Classify(Join(run), now, isEnglishWord, culture, zone) is { Kind: HoverKind.Translation } t ? t with { Bounds = Union(run) } : null;
            }

            // A sentence in another Latin-script language: the words of the line around the pointer.
            if (line.Count >= 3)
            {
                int a = Math.Max(0, at - MaxLineWords / 2), b = Math.Min(line.Count - 1, a + MaxLineWords - 1);
                var run = line.GetRange(a, b - a + 1);
                if (CircleSense.Read(Join(run), now, culture ?? CultureInfo.InvariantCulture, zone ?? TimeZoneInfo.Local) is { Kind: CircleKind.Foreign } f)
                    return new HoverTarget(HoverKind.Translation, f.Value, Union(run)) { Language = f.Language };
            }

            // Otherwise just the word.
            return Classify(hit.Text, now, isEnglishWord, culture, zone) is { } one ? one with { Bounds = hit.Bounds } : null;
        }
        catch (Exception)
        {
            // A regex over its budget or odd OCR: no bubble.
            return null;
        }
    }

    /// <summary>What hovered (or typed) text is, or null for anything the bubble shouldn't answer ("hello world foo").</summary>
    public static HoverTarget? Classify(string? text, DateTime now, Func<string, bool?>? isEnglishWord = null,
        CultureInfo? culture = null, TimeZoneInfo? zone = null)
    {
        try
        {
            var t = Clean(text);
            if (t.Length == 0 || t.Length > MaxText) return null;
            if (Exact(t, now, culture, zone) is { } exact) return exact;

            var read = CircleSense.Read(t, now, culture ?? CultureInfo.InvariantCulture, zone ?? TimeZoneInfo.Local);
            if (read is { Kind: CircleKind.Foreign }) return new HoverTarget(HoverKind.Translation, t, default) { Language = read.Language };

            // From here on only one word is answered: a meaning, or a word in another language.
            if (t.Contains(' ') || !WordRx.IsMatch(t)) return null;
            return Word(t, now, isEnglishWord);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A price, a measurement or a time with a zone, and nothing much else; null otherwise.</summary>
    static HoverTarget? Exact(string text, DateTime now, CultureInfo? culture, TimeZoneInfo? zone)
    {
        var t = Clean(text);
        if (t.Length == 0 || t.Length > 60 || !t.Any(char.IsDigit)) return null;
        if (Currency.TryReadPrice(t, out var money)) return new HoverTarget(HoverKind.Price, t, default) { Money = money };
        if (Units.TryReadMeasurement(t, out var conversion)) return new HoverTarget(HoverKind.Measure, t, default) { Conversion = conversion };
        if (CircleSense.Read(t, now, culture ?? CultureInfo.InvariantCulture, zone ?? TimeZoneInfo.Local) is { Kind: CircleKind.ZoneTime } time)
            return new HoverTarget(HoverKind.Time, t, default) { Time = time };
        return null;
    }

    /// <summary>One word: English gets its meaning (when it's a word worth explaining), another language its translation.</summary>
    static HoverTarget? Word(string word, DateTime now, Func<string, bool?>? isEnglishWord)
    {
        if (word.Length < 3) return null;
        // ALL CAPS ("SALE", "NASA") is a label or an acronym, not something to look up.
        if (word.All(c => !char.IsLower(c))) return null;
        bool? english = isEnglishWord?.Invoke(word);
        // "Keep" at the start of a sentence is the word "keep".
        var lower = LowerFirst(word);
        if (english == false && !ReferenceEquals(lower, word)) english = isEnglishWord?.Invoke(lower);

        if (english == false || (english is null && ForeignMarks(word) is not null))
            return new HoverTarget(HoverKind.Translation, word, default) { Language = GuessLanguage(word) };

        // A word worth explaining, by the circle's own rule: not a little word, not a pronoun.
        if (CircleSense.Read(lower, now, CultureInfo.InvariantCulture, TimeZoneInfo.Utc) is { Kind: CircleKind.Term })
            return new HoverTarget(HoverKind.Meaning, lower, default);
        return null;
    }

    /// <summary>"Keep" → "keep"; "iPhone", "McDonald" and "Rahul" keep their capitals only when more than the first letter is upper case.</summary>
    static string LowerFirst(string word) =>
        word.Length > 1 && char.IsUpper(word[0]) && word.Skip(1).All(c => !char.IsUpper(c))
            ? char.ToLowerInvariant(word[0]) + word[1..]
            : word;

    /// <summary>
    /// A Latin-script word's language from its letters alone, or null. Only asked about a word the PC's English check
    /// already turned down, so "school" or "pretzel" never get here.
    /// </summary>
    public static string? GuessLanguage(string word)
    {
        if (ForeignMarks(word) is { } marked) return marked;
        var w = word.ToLowerInvariant();
        string[] german = { "ung", "heit", "keit", "schaft", "chen", "lich", "isch", "ende" };
        if (german.Any(w.EndsWith) || w.Contains("sch", StringComparison.Ordinal) || w.Contains("tz", StringComparison.Ordinal)) return "German";
        return null;
    }

    /// <summary>The language a letter only it uses gives away (ß, ñ, ç…), or null.</summary>
    static string? ForeignMarks(string word)
    {
        var w = word.ToLowerInvariant();
        if (w.IndexOfAny(new[] { 'ß', 'ä', 'ö', 'ü' }) >= 0) return "German";
        if (w.IndexOfAny(new[] { 'ñ', '¿', '¡' }) >= 0) return "Spanish";
        if (w.IndexOfAny(new[] { 'ã', 'õ' }) >= 0) return "Portuguese";
        if (w.IndexOfAny(new[] { 'ğ', 'ş', 'ı' }) >= 0) return "Turkish";
        if (w.IndexOfAny(new[] { 'ç', 'œ', 'è', 'ê', 'ë', 'â', 'î', 'ô', 'û' }) >= 0) return "French";
        if (w.IndexOfAny(new[] { 'ò', 'ì', 'ù' }) >= 0) return "Italian";
        return null;
    }

    /// <summary>Whether a word is written in a script other than Latin (Devanagari, Japanese, Cyrillic…).</summary>
    public static bool IsOtherScript(string word)
    {
        int other = 0, latin = 0;
        foreach (var c in word)
        {
            if (!char.IsLetter(c)) continue;
            if (c < 'ɐ' || c is >= 'Ḁ' and <= 'ỿ') latin++;
            else other++;
        }
        return other > 0 && other >= latin;
    }

    /// <summary>The word whose box holds the point, or the nearest one on its line when the point is just beside it.</summary>
    static OcrWord? HitWord(IReadOnlyList<OcrWord> words, double x, double y)
    {
        OcrWord? best = null;
        double bestDistance = double.MaxValue;
        foreach (var w in words)
        {
            var b = w.Bounds;
            // A little slack around each word: between letters of a word and a hair beside it still counts.
            double padX = Math.Max(2, b.H * 0.3), padY = Math.Max(2, b.H * 0.25);
            if (x < b.X - padX || x > b.X + b.W + padX || y < b.Y - padY || y > b.Y + b.H + padY) continue;
            double dx = x < b.X ? b.X - x : x > b.X + b.W ? x - (b.X + b.W) : 0;
            double dy = y < b.Y ? b.Y - y : y > b.Y + b.H ? y - (b.Y + b.H) : 0;
            double d = dx * dx + dy * dy;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = w;
            }
        }
        return best;
    }

    static string Join(IEnumerable<OcrWord> run) => string.Join(' ', run.Select(w => w.Text));

    static Box Union(IReadOnlyList<OcrWord> run)
    {
        double l = run.Min(w => w.Bounds.X), t = run.Min(w => w.Bounds.Y);
        double r = run.Max(w => w.Bounds.X + w.Bounds.W), b = run.Max(w => w.Bounds.Y + w.Bounds.H);
        return new Box(l, t, r - l, b - t);
    }

    /// <summary>Spaces closed up, and the punctuation a word drags along on screen (quotes, brackets, a full stop) taken off.</summary>
    static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = Spaces.Replace(text, " ").Trim();
        // Quotes, brackets and punctuation in any order ("photosynthesis",) until none is left at either end.
        for (string before = ""; before != t;)
        {
            before = t;
            t = t.Trim('"', '\'', '“', '”', '‘', '’', '(', ')', '[', ']', '{', '}', '«', '»', '„', '‚', '*');
            t = t.TrimEnd(',', ';', ':', '!', '?', '…', '。', '、', '，', '|');
            // A full stop ends a sentence ("Rs. 99" keeps the dot inside it).
            if (t.EndsWith('.') && !t.EndsWith("..", StringComparison.Ordinal)) t = t[..^1];
            t = t.Trim();
        }
        return t;
    }
}
