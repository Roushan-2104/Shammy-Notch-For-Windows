using System.Globalization;
using Shammy.Core.Conversions;
using Shammy.Core.Hover;
using Shammy.Core.Search;

namespace Shammy.Core.Tests;

/// <summary>Hover to understand: picking the words under the pointer from OCR, what they are, and the bubble's words.</summary>
public class HoverTests
{
    // A Friday afternoon in India.
    static readonly DateTime Now = new(2026, 11, 13, 15, 0, 0);
    static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");
    static readonly TimeZoneInfo Ist = TimeZoneInfo.CreateCustomTimeZone("Test India", TimeSpan.FromMinutes(330), "India", "India");

    // A tiny stand-in for the PC's English spelling check.
    static readonly HashSet<string> English = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "launch", "moved", "to", "so", "call", "is", "now", "at", "on", "keep", "welcome", "note", "short", "first",
        "impressions", "are", "ephemeral", "photosynthesis", "booth", "wide", "early", "tickets", "cost", "hello", "world",
        "cafe", "café", "settings",
    };

    static bool? IsEnglish(string word) => English.Contains(word);

    static HoverTarget? Classify(string text, Func<string, bool?>? check = null) => HoverSense.Classify(text, Now, check ?? IsEnglish, India, Ist);

    // ---- What a hovered thing is ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("$49.99", "USD", 49.99)]
    [InlineData("₹1,299", "INR", 1299)]
    [InlineData("€15", "EUR", 15)]
    [InlineData("(Rs. 999)", "INR", 999)]
    public void Prices(string text, string code, double amount)
    {
        var t = Classify(text);
        Assert.Equal(HoverKind.Price, t?.Kind);
        Assert.Equal(code, t!.Money!.Code);
        Assert.Equal(amount, t.Money.Amount, 2);
    }

    [Theory]
    [InlineData("5 ft 10 in", "cm")]
    [InlineData("350°F", "°C")]
    [InlineData("2.5 kg", "lb")]
    [InlineData("60 mph", "km/h")]
    public void Measurements(string text, string to)
    {
        var t = Classify(text);
        Assert.Equal(HoverKind.Measure, t?.Kind);
        Assert.Equal(to, t!.Conversion!.To.Symbol);
    }

    [Fact]
    public void TimeWithAZone()
    {
        var t = Classify("3 PM PST");
        Assert.Equal(HoverKind.Time, t?.Kind);
        // 3 PM Pacific on Friday is 4:30 AM Saturday in India.
        Assert.Equal(new DateTime(2026, 11, 14, 4, 30, 0), t!.Time!.At);
    }

    [Theory]
    [InlineData("नमस्ते", "Hindi")]
    [InlineData("Привет, как дела?", "Russian")]
    [InlineData("東京タワー", "Japanese")]
    [InlineData("¿Dónde está la estación de tren?", "Spanish")]
    public void ForeignTextTranslates(string text, string language)
    {
        var t = Classify(text);
        Assert.Equal(HoverKind.Translation, t?.Kind);
        Assert.Equal(language, t!.Language);
    }

    [Fact]
    public void AWordTheEnglishCheckTurnsDownTranslates()
    {
        var t = Classify("Wochenende");
        Assert.Equal(HoverKind.Translation, t?.Kind);
        Assert.Equal("German", t!.Language);
        Assert.Equal("Wochenende", t.Text);
    }

    [Fact]
    public void WithoutAnEnglishCheckOnlyMarkedWordsTranslate()
    {
        Assert.Equal(HoverKind.Translation, Classify("Straße", _ => null)?.Kind);
        Assert.Equal(HoverKind.Meaning, Classify("ephemeral", _ => null)?.Kind);
    }

    [Theory]
    [InlineData("ephemeral")]
    [InlineData("ephemeral.")]
    [InlineData("\"photosynthesis\",")]
    public void AnEnglishWordGetsItsMeaning(string text)
    {
        var t = Classify(text);
        Assert.Equal(HoverKind.Meaning, t?.Kind);
        Assert.DoesNotContain('.', t!.Text);
    }

    [Fact]
    public void ASentenceStartIsLoweredForTheMeaning()
    {
        var t = Classify("Settings");
        Assert.Equal(HoverKind.Meaning, t?.Kind);
        Assert.Equal("settings", t!.Text);
    }

    [Theory]
    [InlineData("hello world foo")]
    [InlineData("the")]
    [InlineData("is")]
    [InlineData("42")]
    [InlineData("SALE")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Keep the welcome note short")]
    [InlineData("Fri 21 Nov")]
    public void EverythingElseStaysQuiet(string text) => Assert.Null(Classify(text));

    [Fact]
    public void AnEnglishWordWithAnAccentIsStillEnglish() => Assert.NotEqual(HoverKind.Translation, Classify("café")?.Kind);

    // ---- Which words are under the pointer -----------------------------------------------------------------------

    /// <summary>OCR words laid out left to right on lines 22 px apart, each letter 8 px wide, one space between.</summary>
    static List<OcrWord> Layout(params string[] lines)
    {
        var words = new List<OcrWord>();
        for (int l = 0; l < lines.Length; l++)
        {
            double x = 4;
            foreach (var w in lines[l].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                words.Add(new OcrWord(w, new Box(x, 4 + l * 22, w.Length * 8, 14), l));
                x += w.Length * 8 + 8;
            }
        }
        return words;
    }

    /// <summary>The middle of a word's box.</summary>
    static (double X, double Y) On(List<OcrWord> words, string word, int nth = 0)
    {
        var w = words.Where(x => x.Text == word).ElementAt(nth);
        return (w.Bounds.X + w.Bounds.W / 2, w.Bounds.Y + w.Bounds.H / 2);
    }

    static HoverTarget? PickAt(List<OcrWord> words, (double X, double Y) p) => HoverSense.Pick(words, p.X, p.Y, Now, IsEnglish, India, Ist);

    [Fact]
    public void PicksAMeasurementAcrossItsWords()
    {
        var words = Layout("the booth is 5 ft 10 in wide.");
        var t = PickAt(words, On(words, "ft"));
        Assert.Equal(HoverKind.Measure, t?.Kind);
        Assert.Equal("5 ft 10 in", t!.Text);
        // The box covers the whole run, so moving within it reads nothing new.
        var five = words.First(w => w.Text == "5").Bounds;
        Assert.Equal(five.X, t.Bounds.X);
    }

    [Fact]
    public void PicksATimeWithItsZone()
    {
        var words = Layout("the call is now at 3 PM PST on Saturday.");
        var t = PickAt(words, On(words, "PM"));
        Assert.Equal(HoverKind.Time, t?.Kind);
    }

    [Fact]
    public void PicksAPrice()
    {
        var words = Layout("Early tickets cost $49.99 and the booth");
        var t = PickAt(words, On(words, "$49.99"));
        Assert.Equal(HoverKind.Price, t?.Kind);
        Assert.Equal(49.99, t!.Money!.Amount, 2);
    }

    [Fact]
    public void PicksTheWordForItsMeaningOrTranslation()
    {
        var words = Layout("The launch moved to the Wochenende, so the call", "first impressions are ephemeral.");
        Assert.Equal(HoverKind.Translation, PickAt(words, On(words, "Wochenende,"))?.Kind);
        var meaning = PickAt(words, On(words, "ephemeral."));
        Assert.Equal(HoverKind.Meaning, meaning?.Kind);
        Assert.Equal("ephemeral", meaning!.Text);
        // A little word under the pointer: nothing.
        Assert.Null(PickAt(words, On(words, "the")));
    }

    [Fact]
    public void PicksTheWholeRunOfAnotherScript()
    {
        var words = Layout("He said नमस्ते आप कैसे हैं today");
        var t = PickAt(words, On(words, "आप"));
        Assert.Equal(HoverKind.Translation, t?.Kind);
        Assert.Equal("नमस्ते आप कैसे हैं", t!.Text);
    }

    [Fact]
    public void PicksAForeignLatinLine()
    {
        var words = Layout("¿Dónde está la estación de tren?");
        var t = PickAt(words, On(words, "estación"));
        Assert.Equal(HoverKind.Translation, t?.Kind);
        Assert.Equal("Spanish", t!.Language);
    }

    [Fact]
    public void BetweenWordsOrOnNothingStaysQuiet()
    {
        var words = Layout("first impressions are ephemeral.");
        Assert.Null(HoverSense.Pick(words, 400, 4 + 7, Now, IsEnglish, India, Ist));
        Assert.Null(HoverSense.Pick(words, 20, 80, Now, IsEnglish, India, Ist));
        Assert.Null(HoverSense.Pick(Array.Empty<OcrWord>(), 1, 1, Now));
    }

    // ---- The bubble's words --------------------------------------------------------------------------------------

    [Fact]
    public void PriceBubble()
    {
        var b = HoverText.Price(4741.5, "INR");
        Assert.Equal("In rupees", b.Label);
        Assert.StartsWith("₹4,741", b.Value);
        Assert.True(b.IsNumber);
        Assert.StartsWith("rupees: ₹", b.TestLine);
        Assert.Equal("In dollars", HoverText.Price(12, "USD").Label);
    }

    [Fact]
    public void MeasureBubbleIsRoundedForAGlance()
    {
        Assert.True(Units.TryReadMeasurement("5 ft 10 in", out var c));
        var b = HoverText.Measure(c);
        Assert.Equal("In centimetres", b.Label);
        Assert.Equal("178 cm", b.Value);
        Assert.Equal("cm: 178 cm", b.TestLine);
        Assert.Equal(2.5, HoverText.Glance(2.54));
        Assert.Equal(0.25, HoverText.Glance(0.2541));
    }

    [Fact]
    public void TimeBubbleKeepsTheDayShort()
    {
        var b = HoverText.Time(new DateTime(2026, 11, 14, 4, 30, 0), "tomorrow, Sat 14 Nov");
        Assert.Equal("Your time", b.Label);
        Assert.Equal("4:30 AM", b.Value);
        Assert.Equal("tomorrow", b.Detail);
        Assert.Equal("time: 4:30 AM tomorrow", b.TestLine);
    }

    [Fact]
    public void TranslationBubble()
    {
        var b = HoverText.Translation("Wochenende", "weekend", "German");
        Assert.Equal("German · weekend", b!.Label);
        Assert.Equal("weekend", b.Value);
        Assert.Equal("translation: weekend", b.TestLine);
        Assert.Equal("In English", HoverText.Translation("Wochenende", "weekend", null)!.Label);
        Assert.Equal("Hindi", HoverText.Translation("आप कैसे हैं", "How are you", "Hindi")!.Label);
        // Came back the same (a name): no bubble.
        Assert.Null(HoverText.Translation("Rahul", "Rahul", null));
        Assert.Null(HoverText.Translation("Rahul", "  ", null));
    }

    [Theory]
    [InlineData("Ephemeral means lasting for a very short time. It comes from the Greek.", "lasting for a very short time")]
    [InlineData("**Ephemeral** (adjective): lasting a very short time; transitory.", "lasting a very short time")]
    [InlineData("The word \"ephemeral\" refers to something fleeting.", "something fleeting")]
    [InlineData("Ephemeral is an adjective that means short-lived.", "short-lived")]
    [InlineData("Lasting a very short time.", "lasting a very short time")]
    [InlineData("Ephemeral (adjective)\nLasting for a very short time; fleeting.", "lasting for a very short time")]
    [InlineData("ephemeral /əˈfem(ə)rəl/\nAdjective\nEphemeral describes something that lasts briefly.", "something that lasts briefly")]
    public void MeaningIsOneShortLine(string answer, string expected) => Assert.Equal(expected, HoverText.MeaningLine("ephemeral", answer));

    [Fact]
    public void ALongMeaningIsCutAtAWord()
    {
        var line = HoverText.MeaningLine("word", "word means " + string.Join(' ', Enumerable.Repeat("something rather long", 10)) + ".");
        Assert.NotNull(line);
        Assert.True(line!.Length <= HoverText.MeaningLength);
        Assert.EndsWith("…", line);
        Assert.Null(HoverText.MeaningLine("word", null));
        Assert.Null(HoverText.Meaning("word", ""));
    }
}
