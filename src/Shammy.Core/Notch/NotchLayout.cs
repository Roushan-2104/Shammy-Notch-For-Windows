namespace Shammy.Core.Notch;

/// <summary>A home-row candidate: only what the fitting needs to know about a card.</summary>
public readonly record struct RowCard(string Id, double Width, int Order, bool Relevant);

/// <summary>
/// Which cards the open notch's home row shows. The row is one line with no scrolling, so when the relevant
/// cards don't all fit, the lowest-priority ones (highest <see cref="RowCard.Order"/>) are left out; a smaller,
/// later card still takes a gap that a bigger one couldn't use. Left-out cards stay reachable from the chips.
/// </summary>
public static class HomeRowLayout
{
    /// <summary>The open notch's inner width (1012 minus 18 px padding each side) and the gap between cards.</summary>
    public const double RowWidth = 976, Gap = 8;

    /// <returns>The ids to show, left to right (by Order; equal Orders keep registration order).</returns>
    public static IReadOnlyList<string> Fit(IEnumerable<RowCard> cards, double rowWidth = RowWidth, double gap = Gap)
    {
        var shown = new List<string>();
        double used = 0;
        // OrderBy is stable, so ties keep the order the modules registered them in.
        foreach (var c in cards.Where(c => c.Relevant && c.Width > 0).OrderBy(c => c.Order))
        {
            double need = shown.Count == 0 ? c.Width : used + gap + c.Width;
            // Half a pixel of slack: 340 + 260 + 176 + 176 + 3 gaps is exactly 976 and must fit.
            if (need > rowWidth + 0.5) continue;
            shown.Add(c.Id);
            used = need;
        }
        return shown;
    }
}

/// <summary>A view reachable from the open notch's top row (a built-in view or an older module's tab).</summary>
public sealed record LegacyView(string Label, string ViewId);

/// <summary>A registered v2 card, as far as the chips care.</summary>
public sealed record ChipCard(string Id, string Title, bool Chip);

/// <summary>One chip in the open notch's top row: its label and the view it opens.</summary>
public sealed record ChipSpec(string Label, string ViewId);

/// <summary>
/// The open notch's chips: one per card that asks for a chip, plus one per built-in view and older module
/// tab. A card titled like an older tab replaces that tab's chip (the module moved to a card), so a module
/// switching over never shows two chips for the same thing.
/// </summary>
public static class NotchChips
{
    /// <summary>The chip order: Clipboard, then the Guide, Laptop, Health and Claude Code (ahead of Shelf, Mirror and Snippets, so
    /// they stay out of "More"), then the Focus Wall order (Wall.dc.html: … Calendar, Snippets, Settings), then the cards that
    /// gained a chip so every card has a way in (Timers… Weather); anything else follows in the order it was registered. What
    /// doesn't fit goes under "More". "Coding agents" is Claude Code's older card title.</summary>
    static readonly string[] Preferred =
    {
        "Clipboard", "Guide", "Laptop", "Health", "Claude Code", "Coding agents", "Shelf", "Notes", "Alerts", "System", "Mirror", "Calendar", "Snippets",
        "Settings", "Timers", "Music", "Weather", "Alarms",
    };

    public static IReadOnlyList<ChipSpec> Build(IEnumerable<LegacyView> legacy, IEnumerable<ChipCard> cards)
    {
        var cardList = cards.ToList();
        var chips = new List<ChipSpec>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string label, string id)
        {
            if (seen.Add(id)) chips.Add(new ChipSpec(label, id));
        }

        foreach (var v in legacy)
        {
            var card = cardList.FirstOrDefault(c => string.Equals(c.Title, v.Label, StringComparison.OrdinalIgnoreCase));
            if (card is not null) Add(card.Title, card.Id);
            else Add(v.Label, v.ViewId);
        }
        foreach (var c in cardList.Where(c => c.Chip)) Add(c.Title, c.Id);

        int Rank(ChipSpec c)
        {
            int i = Array.FindIndex(Preferred, p => string.Equals(p, c.Label, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? Preferred.Length : i;
        }
        return chips.OrderBy(Rank).ToList();
    }
}

/// <summary>
/// How many chips the top row shows. A chip cut in half looks broken, so chips show whole or not at all, in
/// order; when they don't all fit, a "More" chip takes the end of the row and lists the rest, so every card
/// stays one click (and a menu) away.
/// </summary>
public static class ChipFit
{
    /// <returns>How many of the chips (in order) show, and whether the "More" chip is needed for the rest.</returns>
    public static (int Shown, bool More) Fit(IReadOnlyList<double> widths, double available, double gap, double moreWidth)
    {
        static int Prefix(IReadOnlyList<double> widths, double room, double gap)
        {
            double x = 0;
            int n = 0;
            foreach (var w in widths)
            {
                double next = n == 0 ? w : x + gap + w;
                // Half a pixel of slack for layout rounding.
                if (next > room + 0.5) break;
                x = next;
                n++;
            }
            return n;
        }

        if (widths.Count == 0) return (0, false);
        int all = Prefix(widths, available, gap);
        if (all == widths.Count) return (all, false);
        int shown = Prefix(widths, available - moreWidth - gap, gap);
        // Not even "More" fits after nothing: show what fits without it rather than a lone "More".
        return available >= moreWidth ? (shown, true) : (all, false);
    }
}

/// <summary>Plain-words file types for shelf tiles ("Word", "PNG image", "Folder").</summary>
public static class FileKinds
{
    static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        [".doc"] = "Word", [".docx"] = "Word", [".rtf"] = "Rich text",
        [".xls"] = "Excel", [".xlsx"] = "Excel", [".csv"] = "CSV",
        [".ppt"] = "PowerPoint", [".pptx"] = "PowerPoint",
        [".pdf"] = "PDF", [".txt"] = "Text", [".md"] = "Markdown",
        [".png"] = "PNG image", [".jpg"] = "JPEG image", [".jpeg"] = "JPEG image", [".gif"] = "GIF image",
        [".webp"] = "WebP image", [".heic"] = "HEIC image", [".bmp"] = "Bitmap image", [".svg"] = "SVG image",
        [".mp4"] = "MP4 video", [".mov"] = "MOV video", [".mkv"] = "MKV video", [".avi"] = "AVI video",
        [".mp3"] = "MP3 audio", [".wav"] = "WAV audio", [".m4a"] = "M4A audio", [".flac"] = "FLAC audio",
        [".zip"] = "ZIP archive", [".7z"] = "7-Zip archive", [".rar"] = "RAR archive",
        [".exe"] = "App", [".msi"] = "Installer", [".lnk"] = "Shortcut",
    };

    /// <summary>"Word" for report.docx, "Folder" for a folder, "XYZ file" for anything unknown.</summary>
    public static string Describe(string path, bool isFolder = false)
    {
        if (isFolder) return "Folder";
        string ext = Path.GetExtension(path ?? "");
        if (ext.Length == 0) return "File";
        return Names.TryGetValue(ext, out var name) ? name : ext.TrimStart('.').ToUpperInvariant() + " file";
    }
}
