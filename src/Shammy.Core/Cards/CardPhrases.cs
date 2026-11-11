using System.Text.RegularExpressions;

namespace Shammy.Core.Cards;

/// <summary>
/// Typed or spoken phrases that open a card ("my meetings", "how do I look"). Each is a fixed list, never a
/// pattern, because these commands run before every other module's: "open camera" must reach the mirror
/// rather than the app launcher, but "open camera settings" must not.
/// </summary>
public static class CardPhrases
{
    static readonly HashSet<string> Calendar = new(StringComparer.Ordinal)
    {
        "calendar", "my calendar", "open calendar", "open my calendar", "show calendar", "show my calendar",
        "meetings", "my meetings", "show my meetings", "what are my meetings", "what meetings do i have",
        "what's next", "what is next", "what's next on my calendar", "what's on my calendar",
        "next meeting", "my next meeting", "what's my next meeting", "when is my next meeting",
        "agenda", "my agenda", "today's agenda", "what's on today",
    };

    static readonly HashSet<string> Mirror = new(StringComparer.Ordinal)
    {
        "mirror", "open mirror", "show mirror", "open the mirror", "show the mirror",
        "how do i look", "how am i looking", "open camera", "open the camera", "show camera", "show the camera",
    };

    /// <summary>"calendar", "my meetings", "what's next"…: open the calendar card.</summary>
    public static bool IsCalendar(string text) => Calendar.Contains(Normalize(text));

    /// <summary>"mirror", "how do I look", "open camera": open the mirror card.</summary>
    public static bool IsMirror(string text) => Mirror.Contains(Normalize(text));

    /// <summary>Lower case, single spaces, straight apostrophes, no trailing punctuation, "whats" as "what's", no "please".</summary>
    static string Normalize(string text)
    {
        var s = Text.PhraseText.Normalize(text).Replace('’', '\'');
        return Regex.Replace(s, @"\bwhats\b", "what's");
    }
}
