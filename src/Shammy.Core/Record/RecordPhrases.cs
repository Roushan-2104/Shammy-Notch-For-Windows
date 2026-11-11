using System.Globalization;
using System.Text.RegularExpressions;

namespace Shammy.Core.Record;

public enum RecordIntent
{
    /// <summary>"record screen", "start recording": open the start card.</summary>
    Open,
    /// <summary>"stop recording".</summary>
    Stop,
}

/// <summary>
/// The typed or spoken phrases screen recording answers. The whole text must be one of them: anything with another
/// topic word ("screen recording software", "how to record my screen", "record player") stays a web search, and
/// "take a screenshot" belongs to screenshots.
/// </summary>
public static class RecordPhrases
{
    const string Screen = "(?:(?:my|the|this) )?(?:screen|display|desktop)";

    // "record screen", "record my screen", "record the screen", "screen record", "screen recording",
    // "start recording", "start a screen recording", "start recording my screen", "record a video of my screen".
    static readonly Regex OpenRx = new(
        $"^(?:(?:start|begin) (?:a |the )?(?:screen )?recording(?: of {Screen})?" +
        $"|(?:start |begin )?record(?:ing)? {Screen}" +
        $"|(?:start |begin |new )?screen record(?:ing|er)?" +
        $"|record (?:a )?video of {Screen})(?: now)?$",
        RegexOptions.CultureInvariant);

    // "stop recording", "stop the recording", "stop screen recording", "stop recording my screen", "end recording".
    static readonly Regex StopRx = new(
        $"^(?:stop|end|finish) (?:(?:the |my )?(?:screen )?record(?:ing)?(?: {Screen})?|recording {Screen})(?: now)?$",
        RegexOptions.CultureInvariant);

    /// <summary>What the text asks for, or null when it is not about screen recording.</summary>
    public static RecordIntent? Parse(string text)
    {
        var s = Normalize(text);
        if (s.Length == 0) return null;
        if (StopRx.IsMatch(s)) return RecordIntent.Stop;
        if (OpenRx.IsMatch(s)) return RecordIntent.Open;
        return null;
    }

    /// <summary>Lower case, single spaces, no trailing punctuation, no "please" or "hey shammy".</summary>
    static string Normalize(string text)
    {
        return Text.PhraseText.Normalize(text);
    }
}

/// <summary>What the test pipe's "record-start" line asks for.</summary>
/// <param name="Seconds">Stop by itself after this long; null to run until stopped.</param>
/// <param name="Area">Only this part of the screen, in its pixels (left, top, width, height); null for all of it.</param>
public sealed record RecordPipeStart(bool Sound, bool Mic, int? Seconds, (int X, int Y, int Width, int Height)? Area = null);

/// <summary>Reads "record-start screen [sound=on|off] [mic=on|off] [seconds=N] [area=X,Y,W,H]".</summary>
public static class RecordPipe
{
    public const string Usage = "usage: record-start screen [sound=on|off] [mic=on|off] [seconds=N] [area=X,Y,W,H]";

    /// <summary>The options, or null with <paramref name="error"/> saying what was wrong.</summary>
    public static RecordPipeStart? ParseStart(string arg, out string? error)
    {
        error = null;
        var words = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || !words[0].Equals("screen", StringComparison.OrdinalIgnoreCase))
        {
            error = Usage;
            return null;
        }
        bool sound = true, mic = false;
        int? seconds = null;
        (int X, int Y, int Width, int Height)? area = null;
        foreach (var word in words.Skip(1))
        {
            int eq = word.IndexOf('=');
            string key = (eq < 0 ? word : word[..eq]).ToLowerInvariant(), value = eq < 0 ? "" : word[(eq + 1)..].ToLowerInvariant();
            switch (key)
            {
                case "sound" when Switch(value) is bool on:
                    sound = on;
                    break;
                case "mic" when Switch(value) is bool on:
                    mic = on;
                    break;
                case "seconds" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is >= 1 and <= 3600:
                    seconds = n;
                    break;
                case "area" when Numbers(value) is { Length: 4 } n && n[0] >= 0 && n[1] >= 0 && n[2] > 0 && n[3] > 0:
                    area = (n[0], n[1], n[2], n[3]);
                    break;
                default:
                    error = $"error: can't read \"{word}\"; {Usage}";
                    return null;
            }
        }
        return new RecordPipeStart(sound, mic, seconds, area);
    }

    static int[]? Numbers(string value)
    {
        var parts = value.Split(',');
        var numbers = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return null;
        return numbers;
    }

    static bool? Switch(string value) => value switch
    {
        "on" or "true" or "1" or "yes" => true,
        "off" or "false" or "0" or "no" => false,
        _ => null,
    };
}
