using System.Buffers.Binary;
using System.Text;
using Shammy.Core.PhoneLink;
using Shammy.Core.PhoneSend;

namespace Shammy.Core.DropActions;

/// <summary>What a drag onto the notch carries, best first: real files, files a browser hands over, a link, or text.</summary>
public enum DragKind { None, Files, VirtualFiles, Link, Text }

/// <summary>One file in a FileGroupDescriptorW: its name, and its size when the source said.</summary>
public sealed record VirtualFile(string Name, long? Size);

/// <summary>
/// Drags from a browser or any app that has no file on disk (an image from Chrome, a link, selected words): which of
/// them a drag is, the names they get, and reading the shell's file descriptor. The app reads the bytes.
/// </summary>
public static class DragPlan
{
    /// <summary>All the files one browser drag may hand over, together.</summary>
    public const long MaxVirtualBytes = 100L * 1024 * 1024;

    const int DescriptorSize = 592, NameOffset = 72, NameChars = 260;
    const uint HasSize = 0x40, HasAttributes = 0x04, DirectoryAttribute = 0x10;

    /// <summary>
    /// The files in a FILEGROUPDESCRIPTORW (a count, then 592-byte FILEDESCRIPTORWs), folders left out; empty when the
    /// bytes are short or odd. Each item's index in the drag is its position here before folders were dropped.
    /// </summary>
    public static IReadOnlyList<(int Index, VirtualFile File)> ParseDescriptor(ReadOnlySpan<byte> data)
    {
        var files = new List<(int, VirtualFile)>();
        if (data.Length < 4) return files;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (count > 1000 || data.Length < 4 + (long)count * DescriptorSize) return files;
        for (int i = 0; i < count; i++)
        {
            var d = data.Slice(4 + i * DescriptorSize, DescriptorSize);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(d);
            uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(d[36..]);
            if ((flags & HasAttributes) != 0 && (attributes & DirectoryAttribute) != 0) continue;
            long? size = (flags & HasSize) != 0
                ? ((long)BinaryPrimitives.ReadUInt32LittleEndian(d[64..]) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(d[68..])
                : null;
            var name = Encoding.Unicode.GetString(d.Slice(NameOffset, NameChars * 2));
            int end = name.IndexOf('\0');
            files.Add((i, new VirtualFile(end >= 0 ? name[..end] : name, size)));
        }
        return files;
    }

    /// <summary>
    /// The link a drag carries: the first line of a UniformResourceLocator(W) format, or text that is one http(s) address,
    /// checked like a link from the phone; else null.
    /// </summary>
    public static string? LinkFrom(string? urlFormat, string? text)
    {
        var first = (urlFormat ?? "").Split('\n', 2)[0].Trim().TrimEnd('\0');
        return LinkShare.Url(first) ?? (text is { } t && !t.Trim().Any(char.IsWhiteSpace) ? LinkShare.Url(t) : null);
    }

    /// <summary>
    /// What to do with a drag. A single ".url" a browser hands over for a dragged link is the link itself (a shortcut we
    /// write from the checked address, never the browser's file); any other handed-over file is a file.
    /// </summary>
    public static DragKind Choose(bool fileDrop, IReadOnlyList<VirtualFile> virtualFiles, string? link, string? text)
    {
        if (fileDrop) return DragKind.Files;
        if (link is not null && virtualFiles.Count == 1 && IsShortcut(virtualFiles[0].Name)) return DragKind.Link;
        if (virtualFiles.Count > 0) return DragKind.VirtualFiles;
        if (link is not null) return DragKind.Link;
        return string.IsNullOrWhiteSpace(text) ? DragKind.None : DragKind.Text;
    }

    static bool IsShortcut(string name) => name.EndsWith(".url", StringComparison.OrdinalIgnoreCase);

    /// <summary>A link's shortcut name: the page title the browser gave its ".url", else the address's host.</summary>
    public static string LinkName(string url, IReadOnlyList<VirtualFile> virtualFiles)
    {
        var title = virtualFiles.Count == 1 && IsShortcut(virtualFiles[0].Name) ? virtualFiles[0].Name[..^4] : "";
        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "link";
        return SafeFileName.Sanitize(title.Trim().Length > 0 ? title : host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host, "link") + ".url";
    }

    /// <summary>The Internet Shortcut file the Shelf keeps for a link (opens in the default browser).</summary>
    public static string Shortcut(string url) => $"[InternetShortcut]\r\nURL={url}\r\n";

    /// <summary>A note's name from its first few words ("Meeting moved to Friday.txt"), at most 40 characters.</summary>
    public static string NoteName(string text)
    {
        var words = string.Join(' ', text.Split((char[]?)null, 6, StringSplitOptions.RemoveEmptyEntries).Take(5));
        if (words.Length > 40) words = words[..40];
        return SafeFileName.Sanitize(words, "Note").TrimEnd('.', ' ') is { Length: > 0 } name ? name + ".txt" : "Note.txt";
    }
}
