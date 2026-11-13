namespace Shammy.Core.Wheel;

/// <summary>What the user is doing when the wheel opens; it decides which six droplets show.</summary>
public enum WheelMoment { Usual, Call, Music, Writing }

/// <summary>
/// What the wheel can see when it opens: a call app holding the microphone, the app in front (its process name,
/// without ".exe", and its window title) and whether music is playing.
/// </summary>
public sealed record MomentSignals(bool InCall, string? ForegroundProcess, string? ForegroundTitle, bool MusicPlaying);

/// <summary>
/// "The wheel knows the moment": in a call it offers call tools, with a music player in front (or music playing) the
/// music controls, in an editor the writing tools, and otherwise the user's own beads. A call wins over everything;
/// the app in front wins over music playing in the background, so coding with music on still gets the writing
/// tools while a music player in front gets the music ones.
/// </summary>
public static class WheelMoments
{
    /// <summary>The droplets each moment shows, in droplet order (top, then clockwise). Usual has none: the beads show.</summary>
    public static IReadOnlyList<string> Droplets(WheelMoment moment) => moment switch
    {
        WheelMoment.Call => new[] { "stage", "captions", "mute", "record", "notes", "share" },
        WheelMoment.Music => new[] { "playpause", "next", "lyrics", "mixer", "musiccard", "previous" },
        WheelMoment.Writing => new[] { "prompter", "focus", "timer", "clipboard", "snippets", "translate" },
        _ => Array.Empty<string>(),
    };

    public static string Name(WheelMoment moment) => moment.ToString().ToLowerInvariant();

    /// <summary>"call", "music", "writing", "usual" (any case); null for anything else ("auto" included).</summary>
    public static WheelMoment? Parse(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "usual" => WheelMoment.Usual,
        "call" => WheelMoment.Call,
        "music" => WheelMoment.Music,
        "writing" => WheelMoment.Writing,
        _ => null,
    };

    public static WheelMoment Decide(MomentSignals s)
    {
        string process = Clean(s.ForegroundProcess), title = s.ForegroundTitle ?? "";
        if (s.InCall || IsMeetingInFront(process, title)) return WheelMoment.Call;
        if (IsMusicApp(process, title)) return WheelMoment.Music;
        if (IsWritingApp(process, title)) return WheelMoment.Writing;
        return s.MusicPlaying ? WheelMoment.Music : WheelMoment.Usual;
    }

    static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "arc", "zen", "browser", "chromium", "iexplore", "thorium",
    };

    // Apps whose window in front is always a call (Webex's meeting window, Zoom's meeting host).
    static readonly HashSet<string> CallProcesses = new(StringComparer.OrdinalIgnoreCase) { "cpthost", "ciscocollabhost", "webexmta", "atmgr" };

    static readonly HashSet<string> TeamsProcesses = new(StringComparer.OrdinalIgnoreCase) { "ms-teams", "teams", "msteams" };

    static readonly HashSet<string> MusicProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "spotify", "applemusic", "itunes", "tidal", "amazon music", "amazonmusic", "deezer", "foobar2000", "musicbee", "aimp", "winamp",
        "youtube music", "youtubemusic", "jiosaavn", "wynk", "microsoft.media.player", "music.ui",
    };

    static readonly string[] MusicTitles = { "YouTube Music", "Spotify", "SoundCloud", "JioSaavn", "Gaana", "Apple Music", "Amazon Music", "Wynk Music", "Deezer", "TIDAL" };

    static readonly HashSet<string> WritingProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "code - insiders", "cursor", "windsurf", "zed", "devenv", "rider", "rider64", "idea", "idea64", "pycharm", "pycharm64",
        "webstorm", "webstorm64", "clion", "clion64", "goland", "goland64", "phpstorm", "phpstorm64", "rubymine64", "datagrip64",
        "studio64", "fleet", "eclipse", "sublime_text", "notepad++", "notepad", "wordpad", "winword", "obsidian", "notion", "typora",
        "logseq", "joplin", "marktext", "scrivener", "onenote", "onenoteim", "gvim", "nvim-qt", "emacs", "windowsterminal",
        "powershell_ise", "atom",
    };

    static readonly string[] WritingTitles =
    {
        "Google Docs", "Notion", "Overleaf", "Visual Studio Code", "Replit", "Microsoft Word", "Word Online", "CodeSandbox",
        "StackBlitz", "HackMD", "Dropbox Paper", "Coda",
    };

    /// <summary>A meeting window in front: a call app's meeting, or a browser tab on Meet, Zoom, Teams, Jitsi or Whereby.</summary>
    public static bool IsMeetingInFront(string? process, string? title)
    {
        string p = Clean(process), t = title ?? "";
        if (CallProcesses.Contains(p)) return true;
        bool meetingWord = Has(t, "Meeting") || Has(t, "Call with") || Has(t, "| Call");
        if (p.Equals("zoom", StringComparison.OrdinalIgnoreCase)) return Has(t, "Zoom Meeting") || Has(t, "Zoom Webinar");
        if (TeamsProcesses.Contains(p)) return meetingWord;
        if (!Browsers.Contains(p)) return false;
        return t.StartsWith("Meet - ", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Meet – ", StringComparison.OrdinalIgnoreCase) ||
               Has(t, "Google Meet") || Has(t, "Zoom Meeting") || Has(t, "Jitsi Meet") || Has(t, "Whereby") ||
               (Has(t, "Microsoft Teams") && meetingWord);
    }

    /// <summary>A music player in front, or a browser on a music site.</summary>
    public static bool IsMusicApp(string? process, string? title)
    {
        string p = Clean(process);
        if (MusicProcesses.Contains(p)) return true;
        return Browsers.Contains(p) && MusicTitles.Any(m => Has(title ?? "", m));
    }

    /// <summary>An editor, a word processor, a notes app or a terminal in front, or a browser on Docs, Notion and the like.</summary>
    public static bool IsWritingApp(string? process, string? title)
    {
        string p = Clean(process);
        if (WritingProcesses.Contains(p)) return true;
        return Browsers.Contains(p) && WritingTitles.Any(w => Has(title ?? "", w));
    }

    static bool Has(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);

    static string Clean(string? process)
    {
        var p = (process ?? "").Trim();
        return p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p;
    }
}
