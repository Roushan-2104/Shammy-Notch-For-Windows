namespace Shammy.Core.Captions;

/// <summary>
/// Windows Live Captions (Windows 11 22H2 and later) as a source of caption text. Its window shows a running
/// transcript, a few lines long, that grows at the end and rewords its last few words as it goes. Shammy reads
/// that text every half second; these helpers turn it into something the <see cref="CaptionMerger"/> can line up
/// against what is already on screen.
/// </summary>
public static class LiveCaptionsText
{
    /// <summary>Windows 11 22H2: the first build with Live Captions.</summary>
    public const int FirstBuild = 22621;

    /// <summary>The window class and the caption text element's AutomationId in Live Captions.</summary>
    public const string WindowClass = "LiveCaptionsDesktopWindow", TextId = "CaptionsTextBlock";

    /// <summary>Whether this Windows build has Live Captions at all.</summary>
    public static bool Supported(int windowsBuild) => windowsBuild >= FirstBuild;

    /// <summary>
    /// The last <paramref name="maxWords"/> words of the transcript, on one line. Enough to overlap what was
    /// shown last time (so the merger lines them up) without re-reading the whole buffer every time.
    /// </summary>
    public static string Tail(string? raw, int maxWords = 40)
    {
        if (string.IsNullOrWhiteSpace(raw) || maxWords <= 0) return "";
        // Walk back from the end counting words, so a long buffer costs no split of the whole thing.
        int end = raw.Length, words = 0, i = end - 1;
        bool inWord = false;
        for (; i >= 0; i--)
        {
            bool space = char.IsWhiteSpace(raw[i]);
            if (!space && !inWord)
            {
                inWord = true;
                if (++words > maxWords) break;
            }
            else if (space)
            {
                inWord = false;
            }
        }
        return Collapse(raw.AsSpan(i + 1));
    }

    /// <summary>The last <paramref name="lines"/> lines of the transcript as Live Captions wraps them, joined by spaces.</summary>
    public static string LastLines(string? raw, int lines = 2)
    {
        if (string.IsNullOrWhiteSpace(raw) || lines <= 0) return "";
        var all = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Collapse(string.Join(' ', all.Skip(Math.Max(0, all.Length - lines))));
    }

    /// <summary>Runs of white space (line breaks included) as one space, trimmed.</summary>
    static string Collapse(ReadOnlySpan<char> text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        bool space = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
