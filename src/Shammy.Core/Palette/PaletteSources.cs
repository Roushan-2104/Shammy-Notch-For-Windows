using System.Text.RegularExpressions;

namespace Shammy.Core.Palette;

/// <summary>One of Shammy's own views the palette can open ("calendar" opens the Calendar card).</summary>
/// <param name="Id">The view id the notch opens (a card id, "shelf", "alerts").</param>
public sealed record ShammyView(string Id, string Title, string Glyph, IReadOnlyList<string> Words);

/// <summary>A remembered text the palette can find: a copied text, a note, a reminder or a snippet.</summary>
/// <param name="Key">Stable id within its source (clip index, note id, snippet trigger).</param>
/// <param name="Trigger">A snippet's trigger (";addr"); empty otherwise. Matched before the text.</param>
public sealed record PaletteText(string Key, string Text, string Trigger = "");

/// <summary>
/// The palette's extra sources beyond commands, apps and files: Shammy's own views, clipboard history, notes
/// and reminders, snippets; and what it suggests before anything is typed. Matching is plain and cheap
/// (prefix, word start, contains) since it runs on every keystroke over a few hundred short texts.
/// </summary>
public static class PaletteSources
{
    /// <summary>How much of a remembered text is searched.</summary>
    public const int MaxScan = 2000;

    /// <summary>What the empty palette offers to try.</summary>
    public static readonly IReadOnlyList<string> Examples = new[] { "play kesariya", "remind me at 6 pm", "100 usd to inr" };

    /// <summary>"Try: play kesariya · remind me at 6 pm · 100 usd to inr".</summary>
    public static string TryRow => "Try: " + string.Join(" · ", Examples);

    public static readonly IReadOnlyList<ShammyView> Views = new ShammyView[]
    {
        new("calendar", "Calendar", "", new[] { "calendar", "agenda", "schedule", "meetings" }),
        new("mirror", "Mirror", "", new[] { "mirror", "camera", "selfie" }),
        new("shelf", "Shelf", "", new[] { "shelf", "files shelf" }),
        new("alerts", "Notifications", "", new[] { "notifications", "alerts", "notification" }),
        new("settings", "Shammy settings", "", new[] { "settings", "shammy settings", "preferences" }),
        new("alarms", "Alarms", "", new[] { "alarms", "alarm clock" }),
        new("timer", "Timers","", new[] { "timers", "timer", "stopwatch" }),
        new("weather", "Weather", "", new[] { "weather", "forecast" }),
        new("music", "Music", "", new[] { "music", "now playing", "lyrics" }),
        new("notes", "Notes", "", new[] { "notes", "reminders" }),
        new("clipboard", "Clipboard", "", new[] { "clipboard", "clipboard history" }),
        new("battery", "Battery", "", new[] { "battery", "system" }),
        // Cards opened by their id (see each module's AddCard).
        new("health", "Health", "", new[] { "health", "pc health", "cleanup", "clean up", "storage", "disk space", "free up space" }),
        new("quick", "Laptop", "", new[] { "laptop", "laptop controls", "quick actions", "power mode", "do not disturb", "quiet" }),
        new("guide", "Guide", "", new[] { "guide", "help", "what can you do", "tips" }),
        new("mixer", "Volume mixer", "", new[] { "volume mixer", "mixer", "app volume", "app volumes" }),
        new("agents", "Claude Code", "", new[] { "claude code", "coding agents", "agents" }),
    };

    /// <summary>Clipboard and notes rows are personal; they never show while the screen is shared.</summary>
    public static bool Allowed(PaletteKind kind, bool presenting) =>
        !(presenting && kind is PaletteKind.Clip or PaletteKind.Note);

    /// <summary>Views a word names: 100 for a whole word ("calendar"), 90 for a prefix of 2+ letters ("cal").</summary>
    public static List<(ShammyView View, int Match)> FindViews(string query, Func<string, bool>? canOpen = null)
    {
        var q = Normalize(query);
        var found = new List<(ShammyView, int)>();
        if (q.Length < 2) return found;
        foreach (var v in Views)
        {
            if (canOpen is not null && !canOpen(v.Id)) continue;
            int best = 0;
            foreach (var w in v.Words)
            {
                if (w == q) best = Math.Max(best, 100);
                else if (w.StartsWith(q, StringComparison.Ordinal)) best = Math.Max(best, 90);
            }
            if (best > 0) found.Add((v, best));
        }
        return found.OrderByDescending(f => f.Item2).ToList();
    }

    /// <summary>
    /// How well a remembered text matches (0 = not at all): starts with the query 80, a word in it starts with
    /// the query 70, contains it 55. Queries under <paramref name="minLength"/> letters match nothing, so one
    /// typed letter doesn't flood the list with every note.
    /// </summary>
    public static int TextMatch(string query, string text, int minLength = 3)
    {
        var q = Normalize(query);
        if (q.Length < minLength || string.IsNullOrEmpty(text)) return 0;
        // Runs on every keystroke over the whole history: a copied log only has its start looked at.
        var t = Normalize(text.Length > MaxScan ? text[..MaxScan] : text);
        if (t.StartsWith(q, StringComparison.Ordinal)) return 80;
        int at = t.IndexOf(q, StringComparison.Ordinal);
        if (at < 0) return 0;
        // A later word that starts with the query.
        for (; at >= 0; at = t.IndexOf(q, at + 1, StringComparison.Ordinal))
            if (at > 0 && t[at - 1] == ' ') return 70;
        return 55;
    }

    /// <summary>The best <paramref name="max"/> matches, strongest first (ties keep the source's order: newest first).</summary>
    public static List<(PaletteText Item, int Match)> FindTexts(string query, IEnumerable<PaletteText> items, int max, int minLength = 3) =>
        items.Select((item, order) => (item, order, match: Score(query, item, minLength)))
            .Where(x => x.match > 0)
            .OrderByDescending(x => x.match)
            .ThenBy(x => x.order)
            .Take(Math.Max(0, max))
            .Select(x => (x.item, x.match))
            .ToList();

    /// <summary>A snippet's trigger matched exactly is 95, a trigger prefix 85; otherwise its text is matched.</summary>
    static int Score(string query, PaletteText item, int minLength)
    {
        if (item.Trigger.Length > 0)
        {
            var q = query.Trim();
            if (q.Length > 0)
            {
                if (string.Equals(item.Trigger, q, StringComparison.OrdinalIgnoreCase)) return 95;
                if (q.Length >= 2 && item.Trigger.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 85;
                // Without the snippet's prefix character: "addr" finds ";addr".
                var bare = item.Trigger.TrimStart(';', ':', '/', '!', '\\', '.', ',');
                if (bare.Length > 0 && q.Length >= 2 && bare.StartsWith(q.TrimStart(';', ':', '/', '!', '\\', '.', ','), StringComparison.OrdinalIgnoreCase))
                    return 85;
            }
        }
        return TextMatch(query, item.Text, minLength);
    }

    /// <summary>One line for a row's title: the first non-empty line, at most <paramref name="max"/> characters.</summary>
    public static string Line(string text, int max = 70)
    {
        var head = text ?? "";
        if (head.Length > MaxScan) head = head[..MaxScan];
        var line = head.Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        line = Regex.Replace(line, @"\s+", " ");
        return line.Length <= max ? line : line[..(max - 1)].TrimEnd() + "…";
    }

    static string Normalize(string s) => Regex.Replace((s ?? "").ToLowerInvariant(), @"\s+", " ").Trim();
}
