using System.Runtime.InteropServices;
using System.Text;

namespace Shammy.Interop;

/// <summary>
/// The app in front, worked out in one place: its window, process name and title, and the front app skipping
/// Shammy's own windows (while the island or a card has the keyboard, the last other app that was in front). The
/// process name is kept for the window in front only (a process id is reused once its app exits), so asking on every
/// key stays cheap. The foreground hook (<see cref="Shell.FullscreenWatcher"/>) calls <see cref="Note"/>; everything
/// else reads on demand. Safe from any thread.
/// </summary>
internal static class FrontWindow
{
    /// <summary>An app in front: its process name ("chrome") and its window's title.</summary>
    public sealed record App(string? Process, string? Title);

    sealed record Named(IntPtr Window, uint Pid, string? Process);

    const uint GW_HWNDNEXT = 2;
    const int DWMWA_CLOAKED = 14;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    static readonly uint OwnPid = (uint)Environment.ProcessId;
    static volatile Named _named = new(IntPtr.Zero, 0, null);
    static volatile App _last = new(null, null);

    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    /// <summary>The window in front (it may be one of Shammy's own).</summary>
    public static IntPtr Handle => Native.GetForegroundWindow();

    /// <summary>The process name of the app in front, or null when it can't be read.</summary>
    public static string? Process => ProcessOf(Handle, out _);

    /// <summary>The title of the window in front ("" when it has none).</summary>
    public static string Title => TitleOf(Handle);

    /// <summary>The app in front skipping Shammy: while one of Shammy's windows is in front, the last other app that was.</summary>
    public static App Ordinary
    {
        get
        {
            Note();
            return _last;
        }
    }

    /// <summary>Remembers the app in front unless it is Shammy (on every foreground change, and on every read).</summary>
    public static void Note()
    {
        var window = Handle;
        var process = ProcessOf(window, out uint pid);
        if (pid == 0 || pid == OwnPid) return;
        _last = new App(process, TitleOf(window));
    }

    /// <summary>A window's process id and name; the name is read once per window in front.</summary>
    public static string? ProcessOf(IntPtr window, out uint pid)
    {
        pid = 0;
        if (window == IntPtr.Zero) return null;
        Native.GetWindowThreadProcessId(window, out pid);
        var named = _named;
        if (named.Window == window && named.Pid == pid) return named.Process;
        var process = pid == 0 ? null : ProcessName(pid);
        _named = new Named(window, pid, process);
        return process;
    }

    /// <summary>A process's exe name without ".exe" ("chrome"), or null when it can't be opened (a protected process, or it closed).</summary>
    public static string? ProcessName(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? Path.GetFileNameWithoutExtension(sb.ToString(0, size)) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public static string TitleOf(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0) return "";
        var sb = new StringBuilder(length + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// Down the z-order from the front, the visible ordinary windows of other apps (past Shammy's own windows, tool
    /// windows and hidden or cloaked ones), up to <paramref name="max"/> windows looked at.
    /// </summary>
    public static IEnumerable<(IntPtr Window, uint Pid)> OrdinaryWindows(int max)
    {
        var hwnd = Handle;
        for (int i = 0; hwnd != IntPtr.Zero && i < max; i++, hwnd = GetWindow(hwnd, GW_HWNDNEXT))
        {
            if (!IsOrdinary(hwnd)) continue;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == OwnPid) continue;
            yield return (hwnd, pid);
        }
    }

    static bool IsOrdinary(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        if ((Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64() & Native.WS_EX_TOOLWINDOW) != 0) return false;
        return Native.DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0 || cloaked == 0;
    }
}
