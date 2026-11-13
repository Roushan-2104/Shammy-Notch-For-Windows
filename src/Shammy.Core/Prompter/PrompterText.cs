using System.Text.RegularExpressions;

namespace Shammy.Core.Prompter;

public enum PrompterPace { Slow, Normal, Fast }

public enum PrompterCommandKind { Open, FromClipboard, FromFile, Close }

public sealed record PrompterCommand(PrompterCommandKind Kind, string? Path = null);

/// <summary>
/// The teleprompter's rules (Stage reads the script): how fast a script moves, how a script is tidied, and which
/// typed or spoken phrases open it ("stage", "teleprompter", "stage from clipboard"…). Speeds are in words per
/// minute, the way people talk about reading aloud.
/// </summary>
public static class PrompterText
{
    /// <summary>A script longer than this is cut (about 3,000 words, well over 15 minutes of speaking).</summary>
    public const int MaxLength = 20_000;

    /// <summary>What shows before the user has given a script of their own.</summary>
    public const string Example =
        "This is Stage. Your words sit right under the camera, so your eyes stay near the lens.\n" +
        "It moves on while you speak and waits when you stop.\n\n" +
        "Copy your script, then type “stage from clipboard”.\nOr type “stage” and the path to a text file.\n\n" +
        "With the pointer here, Space pauses and the arrows step a line.\nPlus and minus change the size, and Esc closes Stage.";

    const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    static readonly Regex Open = new(@"^(?:(?:open|show|start)\s+)?(?:the\s+|my\s+)?(?:teleprompter|prompter|stage)$|^stage\s+on$", Opts);
    static readonly Regex Close = new(@"^(?:close|stop|hide|end|leave)\s+(?:the\s+)?(?:teleprompter|prompter|stage)$|^(?:teleprompter|prompter|stage)\s+(?:off|close)$", Opts);
    static readonly Regex Clipboard = new(@"^(?:teleprompter|prompter|stage)\s+(?:from\s+(?:the\s+|my\s+)?clipboard|paste)$", Opts);
    static readonly Regex WithRest = new(@"^(?:teleprompter|prompter|stage)\s+(?<rest>.+)$", Opts);
    static readonly Regex DrivePath = new(@"^[a-z]:[\\/]", Opts);

    public static int WordsPerMinute(PrompterPace pace) => pace switch
    {
        PrompterPace.Slow => 100,
        PrompterPace.Fast => 180,
        _ => 140,
    };

    /// <summary>Line endings made the same, long runs of blank lines shortened, and the length capped.</summary>
    public static (string Text, bool Cut) Clean(string? raw)
    {
        var text = (raw ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ');
        text = Regex.Replace(text, @"[ ]+\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        if (text.Length <= MaxLength) return (text, false);
        // Cut at a word, never in the middle of one.
        int end = text.LastIndexOfAny(new[] { ' ', '\n' }, MaxLength);
        return (text[..(end > MaxLength / 2 ? end : MaxLength)].TrimEnd(), true);
    }

    public static int Words(string? text) =>
        (text ?? "").Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>How long reading this many words takes at that pace; never less than two seconds.</summary>
    public static double Seconds(int words, PrompterPace pace) => Math.Max(2, words * 60.0 / WordsPerMinute(pace));

    /// <summary>How fast the script moves so that <paramref name="distance"/> pixels pass in the reading time.</summary>
    public static double PixelsPerSecond(double distance, int words, PrompterPace pace) =>
        distance <= 0 ? 0 : distance / Seconds(Math.Max(1, words), pace);

    /// <summary>"12 min at this pace" for the bar.</summary>
    public static string Length(int words, PrompterPace pace)
    {
        double seconds = Seconds(words, pace);
        return seconds < 90 ? $"{Math.Round(seconds / 5) * 5:0} s" : $"{Math.Round(seconds / 60):0} min";
    }

    /// <summary>
    /// What a typed or spoken line asks of the teleprompter; null when it isn't for it (so "teleprompter app for
    /// mac" stays a web search). A file is only taken when the rest looks like a path.
    /// </summary>
    public static PrompterCommand? Parse(string? text)
    {
        var t = (text ?? "").Trim().TrimEnd('.', '!', '?').Trim();
        if (t.Length == 0) return null;
        if (Open.IsMatch(t)) return new PrompterCommand(PrompterCommandKind.Open);
        if (Close.IsMatch(t)) return new PrompterCommand(PrompterCommandKind.Close);
        if (Clipboard.IsMatch(t)) return new PrompterCommand(PrompterCommandKind.FromClipboard);
        if (WithRest.Match(t) is not { Success: true } m) return null;
        var rest = m.Groups["rest"].Value.Trim();
        bool quoted = rest.Length > 2 && rest[0] == '"' && rest[^1] == '"';
        var path = quoted ? rest[1..^1].Trim() : rest;
        bool looksLikePath = quoted || DrivePath.IsMatch(path) || path.StartsWith(@"\\", StringComparison.Ordinal);
        if (!looksLikePath || !IsScriptFile(path)) return null;
        return new PrompterCommand(PrompterCommandKind.FromFile, path);
    }

    /// <summary>Plain text files only.</summary>
    public static bool IsScriptFile(string path) =>
        path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
}
