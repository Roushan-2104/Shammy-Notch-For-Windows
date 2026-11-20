using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using Shammy.Core.DropActions;
using Shammy.Core.PhoneSend;
using Shammy.Infrastructure;
using IDataObject = System.Windows.IDataObject;
using IComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Shammy.Shell;

/// <summary>
/// Drags that carry no file on disk: an image (or any file) from a browser, a link, selected words. They become real
/// files in Shammy's own folder for the Shelf (CircleSend's, cleared after a week unless on the Shelf), so every drop
/// target works on them as on dropped files; a link or words dropped on Send to phone go as a link or copied text
/// instead. Only the bytes the source hands over are used: nothing is fetched from the internet.
/// </summary>
internal static class BrowserDrop
{
    const string Descriptor = "FileGroupDescriptorW", Contents = "FileContents", UrlW = "UniformResourceLocatorW", Url = "UniformResourceLocator";

    /// <summary>What a drag carries, read while the drop lasts. <see cref="Text"/> is the link or the words.</summary>
    internal sealed record Dragged(DragKind Kind, string? Text, IReadOnlyList<(int Index, VirtualFile File)> Files, IDataObject Data);

    /// <summary>Whether the notch takes this drag at all (asked as it moves, so it only looks at the formats).</summary>
    public static bool Accepts(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(Descriptor) || data.GetDataPresent(UrlW)
                || data.GetDataPresent(Url) || data.GetDataPresent(DataFormats.UnicodeText);
        }
        catch (COMException) { return false; }
    }

    /// <summary>What the drag is, or null for nothing the notch takes (and for real files, which go as they are).</summary>
    public static Dragged? Read(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop)) return null;
            var files = data.GetDataPresent(Descriptor) && data.GetData(Descriptor) is MemoryStream d ? DragPlan.ParseDescriptor(d.ToArray()) : [];
            var url = Words(data, UrlW, Encoding.Unicode) ?? Words(data, Url, Encoding.Latin1);
            var text = data.GetDataPresent(DataFormats.UnicodeText) ? data.GetData(DataFormats.UnicodeText) as string : null;
            var link = DragPlan.LinkFrom(url, text);
            var kind = DragPlan.Choose(false, files.Select(f => f.File).ToList(), link, text);
            return kind switch
            {
                DragKind.VirtualFiles => new Dragged(kind, null, files, data),
                DragKind.Link => new Dragged(kind, link, files, data),
                DragKind.Text => new Dragged(kind, text!.Trim(), files, data),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is COMException or OutOfMemoryException or ExternalException)
        {
            Log.Warn("Reading a drag failed", ex);
            return null;
        }
    }

    /// <summary>The chip's word for a drag that isn't files on disk ("Link", "Text", the file's name).</summary>
    public static string Label(IDataObject data) => Read(data) is { } d
        ? d.Kind switch
        {
            DragKind.Link => "Link",
            DragKind.Text => "Text",
            _ => d.Files.Count == 1 ? d.Files[0].File.Name : $"{d.Files.Count} items",
        }
        : "File";

    static string? Words(IDataObject data, string format, Encoding encoding) =>
        data.GetDataPresent(format) && data.GetData(format) is MemoryStream ms ? encoding.GetString(ms.ToArray()).TrimEnd('\0') : null;

    /// <summary>The drag as files in Shammy's folder: what the browser handed over, a link's shortcut or a note. Empty on failure.</summary>
    public static string[] Save(Dragged d)
    {
        var dir = Modules.SelectionTools.CircleSend.Folder;
        try
        {
            Directory.CreateDirectory(dir);
            switch (d.Kind)
            {
                case DragKind.Link:
                    return [Write(dir, DragPlan.LinkName(d.Text!, d.Files.Select(f => f.File).ToList()), Encoding.UTF8.GetBytes(DragPlan.Shortcut(d.Text!)))];
                case DragKind.Text:
                    return [Write(dir, DragPlan.NoteName(d.Text!), new UTF8Encoding(false).GetBytes(d.Text + Environment.NewLine))];
                case DragKind.VirtualFiles:
                    var saved = new List<string>();
                    long budget = DragPlan.MaxVirtualBytes;
                    foreach (var (index, file) in d.Files)
                    {
                        if (file.Size > budget || ReadContents(d.Data, index, budget) is not { } bytes)
                        {
                            Log.Info("A dragged file was over the size limit or unreadable; skipped");
                            continue;
                        }
                        budget -= bytes.Length;
                        var temp = Path.Combine(dir, $".shammy-{Guid.NewGuid():N}.tmp");
                        File.WriteAllBytes(temp, bytes);
                        try { saved.Add(SafeFileName.MoveIn(temp, dir, SafeFileName.Sanitize(file.Name))); }
                        catch
                        {
                            try { File.Delete(temp); }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                            throw;
                        }
                    }
                    return saved.ToArray();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or InvalidDataException)
        {
            Log.Warn("Saving a drag failed", ex);
        }
        return [];
    }

    /// <summary>Our own small file (a shortcut or a note), under a free name.</summary>
    static string Write(string dir, string name, byte[] bytes)
    {
        var path = SafeFileName.MakeUnique(dir, name, p => File.Exists(p) || Directory.Exists(p));
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    /// Item <paramref name="index"/>'s bytes through COM (WPF's data object can't ask for one item of FileContents), or
    /// null past <paramref name="budget"/> bytes.
    /// </summary>
    static byte[]? ReadContents(IDataObject data, int index, long budget)
    {
        if (data is not IComDataObject com) return null;
        var format = new FORMATETC
        {
            cfFormat = unchecked((short)DataFormats.GetDataFormat(Contents).Id),
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = index,
            tymed = TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL,
        };
        com.GetData(ref format, out var medium);
        try
        {
            if (medium.tymed == TYMED.TYMED_HGLOBAL)
            {
                long size = (long)GlobalSize(medium.unionmember);
                if (size > budget) return null;
                var bytes = new byte[size];
                var at = GlobalLock(medium.unionmember);
                try { Marshal.Copy(at, bytes, 0, bytes.Length); }
                finally { GlobalUnlock(medium.unionmember); }
                return bytes;
            }
            if (medium.tymed != TYMED.TYMED_ISTREAM) return null;
            var stream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
            var read = Marshal.AllocCoTaskMem(sizeof(int));
            try
            {
                using var into = new MemoryStream();
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    stream.Read(buffer, buffer.Length, read);
                    int n = Marshal.ReadInt32(read);
                    if (n <= 0) break;
                    if (into.Length + n > budget) return null;
                    into.Write(buffer, 0, n);
                }
                return into.ToArray();
            }
            finally
            {
                Marshal.FreeCoTaskMem(read);
                Marshal.ReleaseComObject(stream);
            }
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    [DllImport("ole32.dll")] static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr hMem);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr hMem);
}
