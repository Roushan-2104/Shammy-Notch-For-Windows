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
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);

    /// <summary>The friendly name of the app in front (skipping Shammy), or null when it can't tell.</summary>
    public static string? Name()
    {
        try
        {
            // The first ordinary window from the front, past Shammy's own windows, tool windows and hidden ones.
            var (hwnd, pid) = Interop.FrontWindow.OrdinaryWindows(64).FirstOrDefault();
            if (hwnd != IntPtr.Zero) return Friendly(StoreAppPid(hwnd, pid));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or ExternalException)
        {
            Infrastructure.Log.Warn("Reading the app in front for circle memory failed", ex);
        }
        return null;
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
