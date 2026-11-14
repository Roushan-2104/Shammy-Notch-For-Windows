using System.Globalization;
using System.Text.RegularExpressions;
using Shammy.Core.Conversions;

namespace Shammy.Core.Hover;

/// <summary>
/// What the hover bubble shows: a small label in the accent tint ("In rupees"), the value (a light number, or bold
/// words), and for a time the day under it ("tomorrow").
/// </summary>
/// <param name="Short">How the test pipe names it ("rupees", "cm", "translation", "time", "meaning").</param>
public sealed record HoverBubble(HoverKind Kind, string Label, string Value, bool IsNumber, string Short, string? Detail = null)
{
    /// <summary>The test pipe's line: "cm: 178 cm", "time: 4:30 AM tomorrow".</summary>
    public string TestLine => $"{Short}: {Value}{(Detail is null ? "" : " " + Detail)}";
}

/// <summary>Builds the bubble's words for each kind, and tidies an online answer into one short line. Never throws.</summary>
public static class HoverText
{
    /// <summary>How long a meaning may be; longer is cut at a word with "…".</summary>
    public const int MeaningLength = 60;

    /// <summary>How long a translation shown in the bubble may be.</summary>
    public const int TranslationLength = 90;

    static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(50);
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A price in the other currency: rupees, or dollars when it's already rupees.</summary>
    public static HoverBubble Price(double value, string toCode)
    {
        var (label, name) = toCode switch
        {
            "INR" => ("In rupees", "rupees"),
            "USD" => ("In dollars", "dollars"),
            "EUR" => ("In euros", "euros"),
            "GBP" => ("In pounds", "pounds"),
            _ => ("In " + toCode, toCode.ToLowerInvariant()),
        };
        return new HoverBubble(HoverKind.Price, label, Currency.Format(value, toCode), true, name);
    }

    /// <summary>A measurement on the other side of the metric/imperial line, rounded for a glance ("178 cm", "177°C").</summary>
    public static HoverBubble Measure(UnitConversion c)
    {
        var to = c.To;
        string value = c.FeetAndInches ? c.Title : to.Format(Glance(c.Value));
        var label = UnitNames.TryGetValue(to.Symbol, out var name) ? "In " + name : "In " + to.Symbol;
        return new HoverBubble(HoverKind.Measure, label, value, true, to.Symbol);
    }

    /// <summary>A number rounded the way a glance wants it: whole from 10 up, one decimal below, two below 1.</summary>
    public static double Glance(double value)
    {
        var a = Math.Abs(value);
        int decimals = a >= 10 ? 0 : a >= 1 ? 1 : 2;
        return Math.Round(value, decimals, MidpointRounding.AwayFromZero);
    }

    static readonly Dictionary<string, string> UnitNames = new(StringComparer.Ordinal)
    {
        ["mm"] = "millimetres", ["cm"] = "centimetres", ["m"] = "metres", ["km"] = "kilometres",
        ["in"] = "inches", ["ft"] = "feet", ["yd"] = "yards", ["mi"] = "miles",
        ["mg"] = "milligrams", ["g"] = "grams", ["kg"] = "kilograms", ["oz"] = "ounces", ["lb"] = "pounds",
        ["°C"] = "Celsius", ["°F"] = "Fahrenheit",
        ["mL"] = "millilitres", ["L"] = "litres", ["fl oz"] = "fluid ounces", ["gal"] = "gallons",
        ["km/h"] = "km per hour", ["mph"] = "miles per hour", ["m/s"] = "metres per second",
        ["cm²"] = "square centimetres", ["m²"] = "square metres", ["km²"] = "square kilometres",
        ["in²"] = "square inches", ["ft²"] = "square feet", ["mi²"] = "square miles", ["acre"] = "acres", ["ha"] = "hectares",
    };

    /// <summary>A time with a zone in the PC's time, with the day under it ("today", "tomorrow").</summary>
    public static HoverBubble Time(DateTime mine, string? dayWord)
    {
        // "tomorrow, Sat 14 Nov" → "tomorrow": the bubble is a glance; the date is there only when it isn't today or tomorrow.
        var day = dayWord;
        if (day is not null && (day.StartsWith("tomorrow", StringComparison.Ordinal) || day.StartsWith("yesterday", StringComparison.Ordinal)))
            day = day.Split(',')[0];
        return new HoverBubble(HoverKind.Time, "Your time", mine.ToString("h:mm tt", Inv), true, "time", day);
    }

    /// <summary>
    /// A translation: "German · weekend" over a bold "weekend" for a word, the language alone over a longer line, and
    /// "In English" when the PC couldn't tell the language. Null when it came back the same as it went (a name).
    /// </summary>
    public static HoverBubble? Translation(string original, string? translated, string? language)
    {
        if (string.IsNullOrWhiteSpace(translated)) return null;
        var t = Regex.Replace(translated, @"\s+", " ", RegexOptions.None, Budget).Trim();
        if (t.Length == 0 || string.Equals(Fold(t), Fold(original), StringComparison.Ordinal)) return null;
        t = Shorten(t, TranslationLength);
        string label = language is null ? "In English" : t.Length <= 18 && !t.Contains(' ') ? $"{language} · {t}" : language;
        return new HoverBubble(HoverKind.Translation, label, t, false, "translation");
    }

    static string Fold(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>A meaning in one short line; null when the answer had none.</summary>
    public static HoverBubble? Meaning(string word, string? answer)
    {
        var line = MeaningLine(word, answer);
        return line is null ? null : new HoverBubble(HoverKind.Meaning, "Meaning", line, false, "meaning");
    }

    /// <summary>
    /// The first sentence of an online answer about <paramref name="word"/>, with its lead-in taken off ("Ephemeral means
    /// …", "Ephemeral (adjective): …") and cut to about <see cref="MeaningLength"/> characters.
    /// </summary>
    public static string? MeaningLine(string word, string? answer)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(answer)) return null;
            foreach (var raw in answer.ReplaceLineEndings("\n").Split('\n'))
            {
                if (Sentence(word, raw) is { } line) return line;
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>One answer line as the meaning, or null when it is only a heading ("Ephemeral (adjective)", "/əˈfem(ə)rəl/").</summary>
    static string? Sentence(string word, string raw)
    {
        var first = Regex.Replace(raw, @"[*_`#]+", "", RegexOptions.None, Budget);
        first = Regex.Replace(first, @"\s+", " ", RegexOptions.None, Budget).Trim();
        if (first.Length == 0) return null;

        // Citations before the meaning: "According to the Merriam-Webster definition of ephemeral, it means …".
        first = Regex.Replace(first, @"^(?:according\s+to|as\s+(?:defined|noted|described|explained)\s+(?:by|in))\s+[^,]{3,90},\s*", "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Budget);
        first = Regex.Replace(first, @"^(?:it|this(?:\s+word)?)\s+(?:means|refers\s+to|describes)\s+", "", RegexOptions.IgnoreCase, Budget);
        var w = Regex.Escape(word.Trim());
        // A heading: the word itself, with a part of speech or a pronunciation and nothing more.
        var bare = Regex.Replace(first, $@"(?i){w}|\([^)]*\)|/[^/]*/|\[[^\]]*\]|[^\p{{L}}]", "", RegexOptions.None, Budget);
        if (bare.Length < 3 || Regex.IsMatch(bare, @"^(?:adjective|noun|verb|adverb|definition|meaning)+$", RegexOptions.IgnoreCase, Budget)) return null;
        // "Ephemeral (adjective) means", "The word "ephemeral" refers to", "Ephemeral: ", "Ephemeral is an adjective that means".
        first = Regex.Replace(first,
            $@"^(?:the\s+(?:word|term)\s+)?[""“']?{w}[""”']?\s*(?:\([^)]{{1,40}}\))?\s*(?:[:—–-]\s*|(?:is\s+an?\s+(?:adjective|noun|verb|adverb)\s+(?:that|which)\s+)?(?:means|refers\s+to|describes|is\s+defined\s+as)\s+)",
            "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Budget);
        first = Regex.Replace(first, @"^(?:something\s+that\s+is\s+)?", "", RegexOptions.IgnoreCase, Budget);

        // The first sentence: up to a full stop followed by a space or the end, or a semicolon.
        var m = Regex.Match(first, @"^(.+?)(?:[.;!?](?:\s|$)|$)", RegexOptions.None, Budget);
        var sentence = (m.Success ? m.Groups[1].Value : first).Trim().TrimEnd(',', ':', '.');
        if (sentence.Length < 3) return null;
        // Lower case like a dictionary line ("lasting a very short time"), unless it starts with a name or "I".
        if (sentence.Length > 1 && char.IsUpper(sentence[0]) && char.IsLower(sentence[1]) && !sentence.StartsWith("I ", StringComparison.Ordinal))
            sentence = char.ToLowerInvariant(sentence[0]) + sentence[1..];
        return Shorten(sentence, MeaningLength);
    }

    /// <summary>Cut at a word to at most <paramref name="max"/> characters, with "…".</summary>
    public static string Shorten(string text, int max)
    {
        var t = text.Trim();
        if (t.Length <= max) return t;
        var cut = t[..(max - 1)];
        int space = cut.LastIndexOf(' ');
        if (space > max / 2) cut = cut[..space];
        return cut.TrimEnd(' ', ',', ';', ':', '-', '—') + "…";
    }
}
