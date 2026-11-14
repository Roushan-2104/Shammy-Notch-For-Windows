namespace Shammy.Core.Notch;

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
