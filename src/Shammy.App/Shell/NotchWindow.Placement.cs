using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Core.Activities;
using Shammy.Core.Music;
using Shammy.Core.Notch;
using Shammy.Infrastructure;
using Shammy.Interop;
using Shammy.Modules;
using Shammy.Motion;
using Shammy.Notifications;
using Shammy.Views;

namespace Shammy.Shell;

public partial class NotchWindow
{
    // ---- Size and screen (Settings → Notch & pet) --------------------------------------------------

    double _scale = 1;
    IntPtr _monitor;
    DispatcherTimer? _pointerFollow;

    /// <summary>The screen the notch is on (the fullscreen check looks at this one).</summary>
    public IntPtr Monitor => _monitor != IntPtr.Zero ? _monitor : Native.PrimaryMonitor();

    /// <summary>Motion changed (Lively or Calm): the pet is redrawn so its record and extras start or stop at once.</summary>
    public void MotionChanged()
    {
        _activityView.Pet.Reset();
        RefreshPet();
    }

    /// <summary>Scales everything in the notch (Settings → Notch size) and re-centres it.</summary>
    public void ApplyScale()
    {
        double scale = Shammy.Core.Settings.NotchLook.Scale(_services.Settings.NotchSize);
        if (Math.Abs(scale - _scale) < 0.001 && Root.LayoutTransform is ScaleTransform) return;
        _scale = scale;
        Root.LayoutTransform = new ScaleTransform(scale, scale);
        Width = WindowWidth * scale;
        Height = Math.Max(Height, (_spec.H + ShadowPad) * scale);
        Reposition();
        ApplyVisuals(force: true);
    }

    /// <summary>The screen chosen in Settings: the main one, the one under the pointer, or a named one.</summary>
    IntPtr TargetMonitor()
    {
        var primary = Native.PrimaryMonitor();
        var setting = Shammy.Core.Settings.NotchDisplay.Normalize(_services.Settings.NotchDisplay);
        if (setting == Shammy.Core.Settings.NotchDisplay.Main) return primary;
        try
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            var names = screens.Select(s => s.DeviceName).ToList();
            var main = screens.FirstOrDefault(s => s.Primary)?.DeviceName ?? names.FirstOrDefault() ?? "";
            string? under = Native.GetCursorPos(out var p) ? System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y)).DeviceName : null;
            var pick = Shammy.Core.Settings.NotchDisplay.Pick(setting, names, main, under);
            var screen = screens.FirstOrDefault(s => s.DeviceName == pick);
            if (screen is null || screen.Primary) return primary;
            var b = screen.Bounds;
            return Native.MonitorFromPoint(new Native.POINT(b.Left + b.Width / 2, b.Top + b.Height / 2), Native.MONITOR_DEFAULTTOPRIMARY);
        }
        catch (Exception ex)
        {
            Log.Warn("Finding the notch's screen failed", ex);
            return primary;
        }
    }

    /// <summary>"Follow pointer": once a second, move to the pointer's screen while the notch is at rest.</summary>
    void UpdatePointerFollow()
    {
        bool follow = Shammy.Core.Settings.NotchDisplay.Normalize(_services.Settings.NotchDisplay) == Shammy.Core.Settings.NotchDisplay.Pointer;
        if (!follow)
        {
            _pointerFollow?.Stop();
            return;
        }
        _pointerFollow ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (_mode is not (NotchMode.Idle or NotchMode.Compact or NotchMode.Hidden) || _hwnd == IntPtr.Zero) return;
            if (TargetMonitor() != _monitor) PlaceInPixels();
        }, Dispatcher);
        _pointerFollow.Start();
    }

    /// <summary>
    /// Centres the notch on the primary monitor in physical pixels at that monitor's own scale.
    /// SystemParameters (used before the window exists) are in the system DPI captured at sign-in, which is
    /// wrong once the primary monitor's scale differs from it (docking a 150% laptop to a 100% screen,
    /// changing the scale in Settings): the notch ended up hundreds of pixels off centre.
    /// </summary>
    bool PlaceInPixels()
    {
        var monitor = TargetMonitor();
        if (Native.GetDpiForMonitor(monitor, 0, out uint dpi, out _) != 0 || dpi == 0) return false;
        _monitor = monitor;
        var info = Native.MonitorInfo(monitor);
        var r = _services.Settings.PinToScreenEdge ? info.rcMonitor : info.rcWork;
        int width = (int)Math.Round(WindowWidth * _scale * dpi / 96.0);
        int x = r.Left + (r.Right - r.Left - width) / 2;
        return Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, r.Top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    public void BringToFront()
    {
        if (_hwnd != IntPtr.Zero && !MenusAboveNotch.AnyOpen) Native.BringToTopmost(_hwnd);
    }

    /// <summary>Keep the notch out of Shammy's own screen freeze (it's drawn live on top anyway).</summary>
    public void ExcludeFromCapture(bool exclude)
    {
        if (_hwnd != IntPtr.Zero) Native.SetWindowDisplayAffinity(_hwnd, exclude ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE);
    }
}
