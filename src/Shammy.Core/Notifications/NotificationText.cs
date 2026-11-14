namespace Shammy.Core.Notifications;

/// <summary>
/// What a notification shows. While the whole screen is shared or recorded others can see the notch, so then who it's
/// from stays and what it says doesn't. A notification with no body may be carrying its message in the title (a
/// reminder, a status), so that one shows the app's name instead.
/// </summary>
public static class NotificationText
{
    /// <summary>The pop-up's second line while private.</summary>
    public const string PopupHidden = "Message hidden while you share your screen";

    /// <summary>The pop-up's (and a Notifications-tab row's) second line with "always hide message text" on.</summary>
    public const string PopupAlwaysHidden = "Message hidden";

    /// <summary>A Notifications-tab row's second line while private.</summary>
    public const string ListHidden = "Message hidden while sharing your screen";

    /// <summary>
    /// A Notifications-tab row: the bold first line and the "App · body" line under it. Multi-line bodies
    /// are joined into one line, since the row has room for one.
    /// </summary>
    /// <param name="alwaysHide">The user's setting to hide message text even when nobody can see the screen.</param>
    public static (string Title, string Detail) ForList(string appName, string title, string body, bool presenting, bool alwaysHide = false)
    {
        if (presenting || alwaysHide)
        {
            var hidden = presenting ? ListHidden : PopupAlwaysHidden;
            return string.IsNullOrWhiteSpace(body) ? (appName, hidden) : (title, $"{appName} · {hidden}");
        }
        var flat = string.Join(" ", (body ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return (title, flat.Length > 0 ? $"{appName} · {flat}" : appName);
    }

    /// <summary>The pop-up's title and body.</summary>
    /// <param name="alwaysHide">The user's setting to hide message text even when nobody can see the screen.</param>
    public static (string Title, string Body) ForPopup(string appName, string title, string body, bool presenting, bool alwaysHide = false)
    {
        if (!presenting && !alwaysHide) return (title, body);
        return (string.IsNullOrWhiteSpace(body) ? appName : title, presenting ? PopupHidden : PopupAlwaysHidden);
    }
}
