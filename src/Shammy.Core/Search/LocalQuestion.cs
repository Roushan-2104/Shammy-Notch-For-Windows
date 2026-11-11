namespace Shammy.Core.Search;

/// <summary>Simple questions Shammy answers on the PC instead of searching the web.</summary>
public enum LocalQuestionKind { Battery, Time, Date, Notifications, LastMessage, ShammySettings, WhatsNew, NowPlaying }

public static class LocalQuestion
{
    static readonly Dictionary<string, LocalQuestionKind> Canonical = new(StringComparer.OrdinalIgnoreCase)
    {
        ["battery"] = LocalQuestionKind.Battery,
        ["what time is it"] = LocalQuestionKind.Time,
        ["what's the date"] = LocalQuestionKind.Date,
        ["any new notifications"] = LocalQuestionKind.Notifications,
        ["read my last message"] = LocalQuestionKind.LastMessage,
        ["shammy settings"] = LocalQuestionKind.ShammySettings,
        ["what's new"] = LocalQuestionKind.WhatsNew,
        ["what song is this"] = LocalQuestionKind.NowPlaying,
    };

    /// <summary>The question <paramref name="text"/> asks, in any wording <see cref="SpokenRewrite"/> knows; null otherwise.</summary>
    public static LocalQuestionKind? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var tidy = SpokenRewrite.Tidy(text);
        if (Canonical.TryGetValue(tidy, out var kind)) return kind;
        return SpokenRewrite.Rewrite(tidy) is { } english && Canonical.TryGetValue(english, out kind) ? kind : null;
    }

    /// <summary>"78% · charging", "45% · 2 h 10 min left", or "No battery on this PC".</summary>
    public static string Battery(int percent, bool pluggedIn, TimeSpan? left)
    {
        if (percent is < 0 or > 100) return "No battery on this PC";
        var where = pluggedIn ? (percent >= 100 ? "full, plugged in" : "charging")
            : left is { } l && l > TimeSpan.Zero ? (l.TotalHours >= 1 ? $"{(int)l.TotalHours} h {l.Minutes} min left" : $"{l.Minutes} min left")
            : "on battery";
        return $"{percent}% · {where}";
    }
}
