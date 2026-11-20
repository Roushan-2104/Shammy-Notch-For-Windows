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
            var kind = DragPlan.Choose(files.Select(f => f.File).ToList(), link, text);
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

    /// <summary>
    /// The drag as files in Shammy's folder: what the browser handed over, or (only for the Shelf or the phone) a link's
    /// shortcut or a note. What couldn't be saved is left out; empty when nothing could.
    /// </summary>
    public static string[] Save(Dragged d, bool shelfOrPhone)
    {
        var dir = Modules.SelectionTools.CircleSend.Folder;
        try
        {
            Directory.CreateDirectory(dir);
            switch (d.Kind)
            {
                case DragKind.Link when shelfOrPhone:
                    return [Write(dir, DragPlan.LinkName(d.Text!, d.Files.Select(f => f.File).ToList()), Encoding.UTF8.GetBytes(DragPlan.Shortcut(d.Text!)))];
                case DragKind.Text when shelfOrPhone:
                    return [Write(dir, DragPlan.NoteName(d.Text!), new UTF8Encoding(false).GetBytes(d.Text + Environment.NewLine))];
                case DragKind.VirtualFiles:
                    return SaveHandedOver(d, dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Saving a drag failed ({ex.GetType().Name})");
        }
        return [];
    }

    /// <summary>Each handed-over file streamed into its own temp file, then kept like a received one; one failing skips only itself.</summary>
    static string[] SaveHandedOver(Dragged d, string dir)
    {
        var saved = new List<string>();
        long budget = DragPlan.MaxVirtualBytes;
        foreach (var (index, file) in d.Files)
        {
            if (file.Size > budget) continue;
            var temp = Path.Combine(dir, $".shammy-{Guid.NewGuid():N}.tmp");
            try
            {
                long written;
                using (var to = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    written = CopyContents(d.Data, index, file.Size, budget, to);
                if (written < 0)
                {
                    File.Delete(temp);
                    Log.Info("A dragged file was over the size limit or unreadable; skipped");
                    continue;
                }
                budget -= written;
                saved.Add(SafeFileName.MoveIn(temp, dir, SafeFileName.Sanitize(file.Name)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or InvalidCastException or InvalidDataException)
            {
                try { File.Delete(temp); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                Log.Warn($"Saving a dragged file failed ({ex.GetType().Name})");
            }
        }
        return saved.ToArray();
    }

    /// <summary>Our own small file (a shortcut or a note), under a free name.</summary>
    static string Write(string dir, string name, byte[] bytes)
    {
        var path = SafeFileName.MakeUnique(dir, name, p => File.Exists(p) || Directory.Exists(p));
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    /// Item <paramref name="index"/>'s bytes through COM (WPF's data object can't ask for one item of FileContents),
    /// written to <paramref name="to"/> as they come; returns how many, or -1 when it's unreadable, empty while the
    /// descriptor promised bytes, or past <paramref name="budget"/>.
    /// </summary>
    static long CopyContents(IDataObject data, int index, long? size, long budget, Stream to)
    {
        if (data is not IComDataObject com) return -1;
        var format = new FORMATETC
        {
            cfFormat = unchecked((short)DataFormats.GetDataFormat(Contents).Id),
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = index,
            tymed = TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL,
        };
        com.GetData(ref format, out var medium);
        var buffer = new byte[64 * 1024];
        try
        {
            if (medium.tymed == TYMED.TYMED_HGLOBAL)
            {
                // the block can be bigger than the file (rounded up): the descriptor's size wins when it gives one
                long length = (long)GlobalSize(medium.unionmember);
                if (size is > 0 and var s) length = Math.Min(length, s);
                if (length <= 0 || length > budget) return -1;
                var at = GlobalLock(medium.unionmember);
                if (at == IntPtr.Zero) return -1;
                try
                {
                    for (long done = 0; done < length;)
                    {
                        int n = (int)Math.Min(buffer.Length, length - done);
                        Marshal.Copy(at + (nint)done, buffer, 0, n);
                        to.Write(buffer, 0, n);
                        done += n;
                    }
                }
                finally { GlobalUnlock(medium.unionmember); }
                return length;
            }
            if (medium.tymed != TYMED.TYMED_ISTREAM) return -1;
            var stream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
            var read = Marshal.AllocCoTaskMem(sizeof(int));
            try
            {
                // from the start, in case the source handed over a stream it had already read
                try { stream.Seek(0, 0, IntPtr.Zero); }
                catch (COMException) { }
                long written = 0;
                while (true)
                {
                    stream.Read(buffer, buffer.Length, read);
                    int n = Marshal.ReadInt32(read);
                    if (n <= 0) break;
                    written += n;
                    if (written > budget) return -1;
                    to.Write(buffer, 0, n);
                }
                return written == 0 && size > 0 ? -1 : written;
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
