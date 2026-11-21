namespace Shammy.Core.Music;

/// <summary>
/// Chat and call apps that tell Windows they're "playing" something. WhatsApp's web app reports a track titled just
/// "WhatsApp", with no artist and no length, even when nothing plays; a lyrics search for that finds a real song
/// called "WhatsApp" and showed its words. Such a session is not music: no card, no lyrics, no song history.
/// </summary>
public static class NotMusic
{
    static readonly string[] Apps =
    {
        "WhatsApp", "Telegram", "Signal", "Discord", "Slack", "Microsoft Teams", "Teams", "Zoom", "Skype", "Messenger",
        "Google Meet", "Phone Link",
    };

    // How these apps show up in Windows' app id ("5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App", "Discord").
    // ("zoom" alone would catch Zoom Player, a real media player; Zoom's own id has "zoom.us".)
    static readonly string[] AppIds = { "whatsapp", "telegram", "signal", "discord", "slack", "teams", "zoom.us", "skype" };

    /// <param name="appId">Windows' id for the app behind the media session; null when unknown.</param>
    /// <param name="duration">The track's length; a real song called "Signal" (TWICE) has one, a chat app's page doesn't.</param>
    public static bool Is(string? appId, string? title, string? artist, TimeSpan duration = default)
    {
        if (!string.IsNullOrEmpty(appId) && AppIds.Any(a => appId.Contains(a, StringComparison.OrdinalIgnoreCase))) return true;
        return string.IsNullOrWhiteSpace(artist) && duration <= TimeSpan.Zero && IsAppName(title);
    }

    /// <summary>The title is nothing but a chat app's name ("WhatsApp", " whatsapp ", "(3) WhatsApp" with unread messages).</summary>
    public static bool IsAppName(string? title)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(title?.Trim() ?? "", @"^\(\d+\)\s*", "");
        return t.Length > 0 && Apps.Any(a => t.Equals(a, StringComparison.OrdinalIgnoreCase));
    }
}
