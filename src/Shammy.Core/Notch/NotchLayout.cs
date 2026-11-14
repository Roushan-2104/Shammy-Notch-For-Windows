namespace Shammy.Core.Notch;

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
