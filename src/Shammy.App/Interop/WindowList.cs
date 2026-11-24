using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Shammy.Interop;

/// <summary>
/// Reach P2: the laptop's open windows as Alt+Tab shows them (top of the stack first: visible, not a tool window, not
/// owned by another window, not cloaked, with a title, not Shammy's), each app's icon read from its own program file,
/// and bringing one forward or asking it to close (WM_CLOSE, the same as its ✕: it can still ask to save).
/// </summary>
internal static class WindowList
{
    public sealed record Item(IntPtr Hwnd, uint Pid, string Title);

    const uint WM_CLOSE = 0x0010;
    const int SW_RESTORE = 9, DWMWA_CLOAKED = 14;

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    static readonly uint OwnPid = (uint)Environment.ProcessId;

    /// <summary>Up to [max] windows, top of the stack first (Native.TopWindows, less tool, cloaked, Shammy's and the desktop).</summary>
    public static List<Item> Top(int max)
    {
        var shell = Native.GetShellWindow();
        return Native.TopWindows()
            .Where(w => w.Handle != shell && w.Pid != OwnPid && w.Pid != 0
                && (Native.GetWindowLongPtr(w.Handle, Native.GWL_EXSTYLE).ToInt64() & Native.WS_EX_TOOLWINDOW) == 0
                && !(Native.DwmGetWindowAttribute(w.Handle, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0))
            .Take(max)
            .Select(w => new Item(w.Handle, w.Pid, w.Title))
            .ToList();
    }

    /// <summary>The window is still one of the listed kind (the phone may only act on windows it was shown, still open).</summary>
    public static bool StillOpen(IntPtr h) => IsWindow(h) && IsWindowVisible(h);

    /// <summary>Brings a window forward, restoring it if it was minimized.</summary>
    public static void Front(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        Native.ForceForeground(h);
    }

    /// <summary>Asks a window to close, as its ✕ does (an app with unsaved work asks first, on the laptop). False when
    /// Windows refused (an administrator window: Shammy may not post to it).</summary>
    public static bool Close(IntPtr h) => PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    /// <summary>The full path of a process's program, or null (it ended, or it's protected).</summary>
    public static string? ExePath(uint pid)
    {
        var p = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (p == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(p, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(p); }
    }

    /// <summary>A program's own icon at [size] pixels, as a PNG (a few KB), or null when it has none. UI thread (WPF imaging).</summary>
    public static byte[]? IconPng(string exe, int size)
    {
        var icons = new IntPtr[1];
        var ids = new uint[1];
        if (PrivateExtractIcons(exe, 0, size, size, icons, ids, 1, 0) == 0 || icons[0] == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(icons[0], System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(size, size));
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            png.Save(ms);
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException) { return null; }
        finally { DestroyIcon(icons[0]); }
    }
}
