using System.Text.RegularExpressions;

namespace Shammy.Core.Captions;

/// <summary>
/// The typed or spoken phrases that turn live captions on and off. The whole text must be one of them, so
/// "captions for instagram" or "how to turn on captions on youtube" stay web searches.
/// </summary>
public static class CaptionPhrases
{
    const string Captions = "(?:the )?(?:live )?(?:captions?|subtitles)";

    static readonly Regex Off = new(
        $"^(?:(?:turn off|switch off|stop|hide|end|close|disable) {Captions}|(?:turn |switch )?{Captions} off|no (?:more )?captions)$",
        RegexOptions.CultureInvariant);

    static readonly Regex On = new(
        $"^(?:(?:turn on|switch on|start|show|enable) {Captions}|(?:turn |switch )?{Captions} on|live captions|captions|caption this)$",
        RegexOptions.CultureInvariant);

    /// <summary>True to turn captions on, false to turn them off, null when the text is about something else.</summary>
    public static bool? Parse(string? text)
    {
        var s = Normalize(text ?? "");
        if (s.Length == 0) return null;
        if (Off.IsMatch(s)) return false;
        if (On.IsMatch(s)) return true;
        return null;
    }

    /// <summary>Lower case, single spaces, no closing punctuation, no "please" or "hey shammy".</summary>
    static string Normalize(string text)
    {
        return Text.PhraseText.Normalize(text);
    }
}
