using System.Windows;
using Microsoft.Win32;
using Shammy.Core.Notch;
using Shammy.Shell.Glass;

namespace Shammy.Shell;

/// <summary>
/// Frosted glass for the island (opt-in, Settings › Notch): when the rules allow (see <see cref="FrostedGlassRules"/>)
/// the island's own glass steps aside and <see cref="FrostedGlass"/> puts Windows' blur behind it, its action circle
/// and its wings. Anything that isn't fine (Energy saver, low battery, a full-screen app…) brings the usual glass back.
/// </summary>
public partial class NotchWindow
{
    FrostedGlass? _frost;
    bool _frostUnavailable;

    /// <summary>Where frosted glass stands, for the line under its switch in Settings.</summary>
    internal static GlassDecision GlassStatus { get; private set; }

    internal static event Action? GlassStatusChanged;

    void InitFrost()
    {
        SystemEvents.PowerModeChanged += OnPowerForGlass;
        GlassSurfaces.ShapesChanged += OnGlassShapes;
        Closed += (_, _) =>
        {
            SystemEvents.PowerModeChanged -= OnPowerForGlass;
            GlassSurfaces.ShapesChanged -= OnGlassShapes;
            _frost?.Dispose();
            GlassSurfaces.SetRunning(false);
        };
    }

    void OnPowerForGlass(object? sender, PowerModeChangedEventArgs e) => Dispatcher.BeginInvoke(EvaluateFrost);

    void OnGlassShapes() => _frost?.Update();

    /// <summary>Runs or stops frosted glass for what's true now, and updates the line Settings shows.</summary>
    internal void EvaluateFrost()
    {
        if (_hwnd == IntPtr.Zero || _services is null) return;
        bool wasRunning = GlassSurfaces.Running;
        var power = System.Windows.Forms.SystemInformation.PowerStatus;
        bool onAc = power.PowerLineStatus != System.Windows.Forms.PowerLineStatus.Offline;
        bool noBattery = power.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) || power.BatteryLifePercent > 1;
        int battery = noBattery ? -1 : (int)Math.Round(power.BatteryLifePercent * 100);
        var decision = FrostedGlassRules.Decide(new GlassConditions(
            _services.Settings.FrostedGlass, FrostedGlassRules.WindowsBuildOk(Environment.OSVersion.Version.Build) && !_frostUnavailable,
            onAc, battery, _batterySaver, _transparency, _fullscreen, SystemParameters.HighContrast));

        bool run = decision.Runs && _mode != NotchMode.Hidden;
        if (run && _frost?.Running != true)
        {
            _frost ??= new FrostedGlass(() => GlassSurfaces.Family);
            // Running first, so the pieces publish where they are before the first shape.
            GlassSurfaces.SetRunning(true);
            PublishIsland();
            if (!_frost.Start(ExcludedFromCapture))
            {
                _frostUnavailable = true;
                GlassSurfaces.SetRunning(false);
                EvaluateFrost();
                return;
            }
        }
        else if (!run && _frost?.Running == true)
        {
            _frost.Stop();
        }
        GlassSurfaces.SetRunning(_frost?.Running == true);
        if (!GlassSurfaces.Running) GlassSurfaces.Remove("island");
        _chrome.SetFrosted(GlassSurfaces.Running);
        ShadowShape.Visibility = GlassSurfaces.Running ? Visibility.Hidden : Visibility.Visible;
        ApplyLook();
        PublishIsland();

        if (decision != GlassStatus)
        {
            GlassStatus = decision;
            GlassStatusChanged?.Invoke();
        }
        // The waves take the moment's colour only on frost: started or stopped, they follow at once.
        if (wasRunning != GlassSurfaces.Running) UpdateAmbience();
    }

    /// <summary>The island and its action circle, where they are on screen this frame (only while frosted glass runs).</summary>
    void PublishIsland()
    {
        if (!GlassSurfaces.Running || PresentationSource.FromVisual(Root) is null) return;
        double w = _w.Value, h = _h.Value, r = _r.Value;
        if (h < 1 || w < 1 || _mode == NotchMode.Hidden)
        {
            GlassSurfaces.Remove("island");
        }
        else
        {
            try
            {
                var a = Root.PointToScreen(new Point(Cx - w / 2, 0));
                var b = Root.PointToScreen(new Point(Cx + w / 2, h));
                double px = (b.X - a.X) / w, room = Math.Min(h, w) / 2;
                r = Math.Clamp(r, 0, room);
                // An open slab's top corners are tighter than its bottom ones (see NotchShape.Build).
                double top = r >= room - 0.5 ? r : Math.Min(r, NotchShape.TopRadius);
                GlassSurfaces.Set("island", new GlassShape(new Rect(a, b), top * px, r * px, 1));
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }
        foreach (var (key, element) in _chrome.GlassPieces())
        {
            if (element.Opacity < 0.01 || !element.IsVisible)
            {
                GlassSurfaces.Remove(key);
                continue;
            }
            try
            {
                var a = element.PointToScreen(new Point(0, 0));
                var b = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
                var rect = new Rect(a, b);
                GlassSurfaces.Set(key, new GlassShape(rect, rect.Height / 2, rect.Height / 2, GlassPiece.Opacity(element)));
            }
            catch (InvalidOperationException)
            {
                GlassSurfaces.Remove(key);
            }
        }
    }

    internal string DescribeFrost() =>
        $"{GlassStatus.State} \"{GlassStatus.Status}\" unavailable={_frostUnavailable} {_frost?.Describe() ?? "frosted=never"} {GlassSurfaces.Describe()}";

    /// <summary>Test only: a real screen capture of the frosted glass and the island family over what's behind them.</summary>
    internal string GlassScreenShot(string path)
    {
        if (_frost is not { Running: true } frost || frost.WindowRect.Width == 0) return "error: frosted glass isn't showing";
        var r = frost.WindowRect;
        const int pad = 40;
        using var bmp = new System.Drawing.Bitmap(r.Width + 2 * pad, r.Height + 2 * pad);
        using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(r.X - pad, r.Y - pad, 0, 0, bmp.Size);
        bmp.Save(path);
        return $"ok {path} {r.X},{r.Y} {r.Width}x{r.Height}";
    }
}
