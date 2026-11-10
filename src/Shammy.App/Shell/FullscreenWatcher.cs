using System.Diagnostics;
using System.Windows.Threading;
using Shammy.Interop;

namespace Shammy.Shell;

/// <summary>
/// Detects games, videos and presentations taking over the primary monitor so the notch can get out
/// of the way. Reacts instantly to foreground changes and re-checks on a slow timer for in-place
/// switches like F11 in a browser.
/// </summary>
internal sealed class FullscreenWatcher : IDisposable
{
    static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
        { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow" };

    readonly Native.WinEventProc _proc;
    readonly IntPtr _hook;
    readonly DispatcherTimer _timer;
    readonly uint _pid = (uint)Environment.ProcessId;
    bool _state;

    public FullscreenWatcher(Dispatcher ui)
    {
        _proc = (_, _, _, _, _, _, _) => ui.BeginInvoke(Check);
        _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(1500), DispatcherPriority.Background, (_, _) => Check(), ui);
        _timer.Start();
    }

    public event Action<bool>? Changed;

    /// <summary>The screen the notch is on; the main one when not set.</summary>
    public Func<IntPtr>? NotchMonitor { get; set; }

    /// <summary>The apps the notch stays away from (Settings → Hide while using).</summary>
    public Func<IReadOnlyList<string>>? HiddenApps { get; set; }

    readonly Dictionary<uint, string> _names = new();

    /// <summary>The app in front is one the user asked the notch to hide for.</summary>
    bool HiddenAppInFront(uint pid)
    {
        if (HiddenApps?.Invoke() is not { Count: > 0 } apps) return false;
        if (!_names.TryGetValue(pid, out var name))
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                name = p.ProcessName;
            }
            catch (Exception)
            {
                name = "";
            }
            if (_names.Count > 200) _names.Clear();
            _names[pid] = name;
        }
        return Shammy.Core.Settings.HideRules.Hides(name, apps);
    }

    /// <summary>Raised on every foreground change, so the notch can re-assert topmost.</summary>
    public event Action? ForegroundChanged;

    public void Check()
    {
        bool now = Evaluate();
        ForegroundChanged?.Invoke();
        if (now == _state) return;
        _state = now;
        Changed?.Invoke(now);
    }

    bool Evaluate()
    {
        if (Native.SHQueryUserNotificationState(out int quns) == 0 &&
            quns is Native.QUNS_RUNNING_D3D_FULL_SCREEN or Native.QUNS_PRESENTATION_MODE)
            return true;

        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == Native.GetShellWindow() || fg == Native.GetDesktopWindow()) return false;
        Native.GetWindowThreadProcessId(fg, out uint pid);
        // Our own windows are never a fullscreen app (the freeze overlay never takes focus, and when
        // Windows hands focus to one of Shammy's background windows the fullscreen app is gone).
        if (pid == _pid) return false;
        if (ShellClasses.Contains(Native.ClassName(fg))) return false;
        if (HiddenAppInFront(pid)) return true;

        var primary = NotchMonitor?.Invoke() is { } m0 && m0 != IntPtr.Zero ? m0 : Native.PrimaryMonitor();
        if (Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST) != primary) return false;
        // A normal maximized window (title bar and all) overhangs the monitor by its resize border, so
        // with an auto-hidden taskbar it "covers" the screen. That's not fullscreen; games, videos and
        // F11 browsers drop the caption.
        const long WS_CAPTION = 0x00C00000;
        if (Native.IsZoomed(fg) && (Native.GetWindowLongPtr(fg, -16).ToInt64() & WS_CAPTION) == WS_CAPTION) return false;
        if (!Native.GetWindowRect(fg, out var r)) return false;
        var m = Native.MonitorBounds(primary);
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    public void Dispose()
    {
        _timer.Stop();
        if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
    }
}
