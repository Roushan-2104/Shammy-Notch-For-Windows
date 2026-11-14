using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Shammy.Core.CircleMemory;

namespace Shammy.Modules.CircleMemory;

/// <summary>
/// The app a circle was drawn over: the window in front, or, while Shammy's own overlay or island is in front, the
/// first ordinary window below it. Only the app's friendly name is kept ("WhatsApp"), never the window's title.
/// </summary>
internal static class FrontApp
{
    const uint GW_HWNDNEXT = 2;
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x80;
    const int DWMWA_CLOAKED = 14;

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);

    /// <summary>The friendly name of the app in front (skipping Shammy), or null when it can't tell.</summary>
    public static string? Name()
    {
        try
        {
            uint own = (uint)Environment.ProcessId;
            var hwnd = GetForegroundWindow();
            // Walk down the z-order from the front past Shammy's own windows, tool windows and hidden ones.
            for (int i = 0; hwnd != IntPtr.Zero && i < 64; i++, hwnd = GetWindow(hwnd, GW_HWNDNEXT))
            {
                if (!Ordinary(hwnd)) continue;
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0 || pid == own) continue;
                return Friendly(StoreAppPid(hwnd, pid));
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or ExternalException)
        {
            Infrastructure.Log.Warn("Reading the app in front for circle memory failed", ex);
        }
        return null;
    }

    static bool Ordinary(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return false;
        return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0 || cloaked == 0;
    }

    /// <summary>A Store app's frame belongs to ApplicationFrameHost; the app itself owns a child window inside it.</summary>
    static uint StoreAppPid(IntPtr hwnd, uint pid)
    {
        using (var p = Process.GetProcessById((int)pid))
            if (!string.Equals(p.ProcessName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) return pid;
        uint inner = pid;
        EnumChildWindows(hwnd, (child, _) =>
        {
            GetWindowThreadProcessId(child, out uint c);
            if (c == 0 || c == pid) return true;
            inner = c;
            return false;
        }, IntPtr.Zero);
        return inner;
    }

    static string? Friendly(uint pid)
    {
        using var p = Process.GetProcessById((int)pid);
        string? description = null;
        try { description = p.MainModule?.FileVersionInfo.FileDescription; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // An elevated or protected process: the process name has to do.
        }
        return AppNames.Friendly(p.ProcessName, description);
    }
}
