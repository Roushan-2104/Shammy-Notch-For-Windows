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

    // ---- Installed web apps (Reach v2): YouTube Music, WhatsApp Web… are chrome.exe or msedge.exe underneath ----

    /// <summary>
    /// The window's own app ID when it's an installed web app (Chrome, Edge, Brave give each its own,
    /// "Chrome._crx_&lt;id&gt;"), else null: then it's the browser itself, or any other app, named by its program.
    /// </summary>
    public static string? WebAppId(IntPtr hwnd)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store is null) return null;
        try
        {
            var key = AppUserModelId;
            if (store.GetValue(ref key, out var v) != 0) return null;
            try { return v.Vt == 31 /* VT_LPWSTR */ && Marshal.PtrToStringUni(v.Ptr) is { } id && id.Contains("_crx_", StringComparison.Ordinal) ? id : null; }
            finally { PropVariantClear(ref v); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    /// <summary>A web app's name as the Start menu shows it ("YouTube Music"), from its app ID, or null. UI thread (shell COM).</summary>
    public static string? AppName(string appId)
    {
        var folder = AppsFolder;
        var iid = typeof(IShellItem).GUID;
        if (SHCreateItemInKnownFolder(ref folder, 0, appId, ref iid, out var item) != 0 || item is null) return null;
        try
        {
            if (item.GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out var name) != 0 || name == IntPtr.Zero) return null;
            // an ID with no Start menu entry can come back as itself: that's no name
            try { return Marshal.PtrToStringUni(name) is { Length: > 0 } n && n != appId && !n.Contains("_crx_", StringComparison.Ordinal) ? n : null; }
            finally { Marshal.FreeCoTaskMem(name); }
        }
        finally { Marshal.ReleaseComObject(item); }
    }

    /// <summary>The icon the window shows in its title bar and taskbar (a web app's own icon), as a PNG, or null. UI thread.</summary>
    public static byte[]? WindowIconPng(IntPtr hwnd, int size)
    {
        // the window's icons belong to it: read, never destroyed here
        IntPtr icon = IntPtr.Zero;
        foreach (var which in new[] { 1 /* ICON_BIG */, 2 /* ICON_SMALL2 */ })
            if (SendMessageTimeout(hwnd, 0x007F /* WM_GETICON */, (IntPtr)which, IntPtr.Zero, 0x0002 /* SMTO_ABORTIFHUNG */, 200, out icon) != IntPtr.Zero && icon != IntPtr.Zero) break;
        if (icon == IntPtr.Zero) icon = GetClassLongPtr(hwnd, -14 /* GCLP_HICON */);
        if (icon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(icon, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(size, size));
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            png.Save(ms);
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException) { return null; }
    }

    static readonly PropertyKey AppUserModelId = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
    static readonly Guid AppsFolder = new("1e87508d-89c2-42f0-8a7e-645a0f50ca58");

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey(Guid fmtid, uint pid) { public Guid Fmtid = fmtid; public uint Pid = pid; }

    [StructLayout(LayoutKind.Sequential)]
    struct PropVariant { public ushort Vt; ushort r1, r2, r3; public IntPtr Ptr; public IntPtr Ptr2; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
        [PreserveSig] int GetParent(out IntPtr parent);
        [PreserveSig] int GetDisplayName(uint form, out IntPtr name);
    }

    [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemInKnownFolder(ref Guid folder, uint flags, string item, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem? shellItem);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant v);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeoutMs, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);
}
