namespace Shammy.Core.Wheel;

/// <summary>What the app in front suggests: a short label ("Zoom") and the wheel actions that fit it, best first.</summary>
public sealed record WheelSuggestion(string Label, IReadOnlyList<string> ActionIds);

/// <summary>
/// The dock's extra keys for the app you are in: a meeting gets captions, recording and notes; a browser gets the
/// screenshot, the phone and the clipboard (or captions and the mixer on a video site); code gets a timer and
/// notes; a music app gets the player and the mixer; a slideshow gets the teleprompter. Your own eight keys never
/// move: these sit beside them, and only the ones not already there.
/// </summary>
public static class WheelContext
{
    public const int MaxExtra = 3;

    static readonly string[] Meetings = { "zoom", "teams", "ms-teams", "webex", "ciscocollabhost", "skype", "slack" };
    static readonly string[] Browsers = { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc", "zen" };
    static readonly string[] Coding = { "code", "code - insiders", "devenv", "idea64", "pycharm64", "rider64", "webstorm64", "cursor", "windsurf", "sublime_text", "notepad++", "windowsterminal", "wt" };
    static readonly string[] Music = { "spotify", "applemusic", "itunes", "musicbee", "foobar2000", "aimp" };
    static readonly string[] Slides = { "powerpnt" };
    static readonly string[] VideoSites = { "youtube", "netflix", "prime video", "hotstar", "jiocinema", "twitch", "disney+" };
    static readonly string[] MeetingSites = { "google meet", "meet.google", "zoom meeting", "microsoft teams" };

    /// <summary>The suggestion for a foreground process (its name without ".exe") and window title; null when none fits.</summary>
    public static WheelSuggestion? For(string? process, string? title)
    {
        var p = (process ?? "").Trim().ToLowerInvariant();
        if (p.EndsWith(".exe", StringComparison.Ordinal)) p = p[..^4];
        var t = (title ?? "").ToLowerInvariant();
        if (p.Length == 0) return null;

        if (Meetings.Contains(p) || (Browsers.Contains(p) && MeetingSites.Any(t.Contains)))
            return new WheelSuggestion(Name(p, t, "Your meeting"), new[] { "captions", "record", "notes" });
        if (Browsers.Contains(p))
            return VideoSites.Any(t.Contains)
                ? new WheelSuggestion(VideoSites.First(t.Contains) is var site ? Title(site) : "Video", new[] { "captions", "mixer" })
                : new WheelSuggestion("This page", new[] { "screenshot", "phone", "clipboard" });
        if (Coding.Contains(p)) return new WheelSuggestion("Coding", new[] { "timer", "notes", "clipboard" });
        if (Music.Contains(p)) return new WheelSuggestion(Title(p), new[] { "music", "mixer" });
        if (Slides.Contains(p)) return new WheelSuggestion("Slides", new[] { "prompter", "record", "timer" });
        return null;
    }

    /// <summary>The suggested actions not already on the dock, at most <see cref="MaxExtra"/>.</summary>
    public static IReadOnlyList<string> Extra(WheelSuggestion? suggestion, IEnumerable<string> onDock)
    {
        if (suggestion is null) return Array.Empty<string>();
        var have = onDock.ToHashSet(StringComparer.Ordinal);
        return suggestion.ActionIds.Where(id => !have.Contains(id) && WheelSlots.ById(id) is not null).Take(MaxExtra).ToList();
    }

    static string Name(string process, string title, string fallback) => process switch
    {
        "zoom" => "Zoom",
        "teams" or "ms-teams" => "Teams",
        "webex" or "ciscocollabhost" => "Webex",
        "skype" => "Skype",
        "slack" => "Slack",
        _ => title.Contains("google meet") || title.Contains("meet.google") ? "Google Meet" : fallback,
    };

    static string Title(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
