using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Core.Notch;
using Shammy.Infrastructure;
using Shammy.Interop;
using Rgb = Shammy.Core.Music.Rgb;

namespace Shammy.Shell;

/// <summary>
/// The island blends with the screen: every few seconds, while it's closed, it looks at a thin strip of screen just
/// under itself (below its shadow), takes the strip's average colour and fades its tint, rim and halo towards it. The
/// strip is a few thousand pixels, read and dropped at once; nothing is kept. It doesn't look while the screen is shared
/// or recorded, while there's no glass (Battery Saver, no transparency, high contrast) or with the setting off.
/// </summary>
public partial class NotchWindow
{
    const double TintEveryS = 2.5, TintFadeMs = 650, StripHeight = 18;

    DispatcherTimer? _tintTimer;
    ScreenTint _tintFrom = ScreenTint.None, _tintTo = ScreenTint.None, _tintNow = ScreenTint.None;
    readonly System.Diagnostics.Stopwatch _tintClock = new();
    IDisposable? _tintFade;
    Rgb? _lastBehind;
    double _tintDrawnAt;

    void InitTint()
    {
        _tintTimer = new DispatcherTimer(TimeSpan.FromSeconds(TintEveryS), DispatcherPriority.Background, (_, _) => SampleTint(), Dispatcher);
        _services.Settings.Changed += name =>
        {
            if (name == nameof(AppSettings.BlendWithScreen)) Dispatcher.BeginInvoke(UpdateTintWatch);
        };
        Closed += (_, _) =>
        {
            _tintTimer.Stop();
            _tintFade?.Dispose();
        };
        UpdateTintWatch();
    }

    bool TintAllowed => _services.Settings.BlendWithScreen && _plan.Glass && !_tucked && _mode != NotchMode.Hidden;

    /// <summary>Starts or stops looking (called whenever the island's look is decided); off fades back to plain glass.</summary>
    void UpdateTintWatch()
    {
        if (_tintTimer is null) return;
        if (TintAllowed)
        {
            if (!_tintTimer.IsEnabled)
            {
                _tintTimer.Start();
                SampleTint();
            }
            return;
        }
        _tintTimer.Stop();
        _lastBehind = null;
        // Off for good (the setting, no glass): back to the plain look. Hidden or tucked: keep the tint for when it's back.
        if (!_services.Settings.BlendWithScreen || !_plan.Glass) FadeTint(ScreenTint.None);
    }

    void SampleTint()
    {
        if (!TintAllowed) return;
        // Only while closed: an open island, a card or a message covers the strip it would read.
        if (_mode is not (NotchMode.Idle or NotchMode.Compact or NotchMode.Peek)) return;
        // Someone else may be watching the screen: the island doesn't look at it then.
        if (PrivacyState.Presenting) return;
        var island = IslandBounds.Screen;
        if (island.IsEmpty || Native.GetDpiForMonitor(Monitor, 0, out uint dpi, out _) != 0 || dpi == 0) return;
        double px = dpi / 96.0;
        var screen = Native.MonitorBounds(Monitor);
        int top = (int)Math.Round(island.Bottom + (ShadowPad + 6) * _scale * px);
        int height = (int)Math.Round(StripHeight * px);
        int left = Math.Max(screen.Left, (int)Math.Round(island.Left));
        int right = Math.Min(screen.Right, (int)Math.Round(island.Right));
        if (right - left < 8 || top + height > screen.Bottom) return;
        var pixels = ReadStrip(left, top, right - left, height);
        if (pixels is null) return;
        var behind = ScreenTint.Average(pixels);
        // The same colour as last time (within a hair): nothing to repaint.
        if (_lastBehind is { } last && Math.Abs(last.R - behind.R) + Math.Abs(last.G - behind.G) + Math.Abs(last.B - behind.B) < 18) return;
        _lastBehind = behind;
        var tint = ScreenTint.From(behind);
        // A different screen that comes out as the same tint (grey to grey): nothing to repaint.
        if (tint != _tintTo) FadeTint(tint);
    }

    /// <summary>Fades from the tint on show now to <paramref name="to"/>, on the frame clock, then stops.</summary>
    void FadeTint(ScreenTint to)
    {
        if (to == _tintTo && _tintFade is null) return;
        _tintFrom = _tintNow;
        _tintTo = to;
        if (!MotionPrefs.Lively)
        {
            SetTint(to);
            return;
        }
        _tintClock.Restart();
        _tintDrawnAt = -1;
        _tintFade ??= Motion.Animator.Subscribe(_ =>
        {
            double ms = _tintClock.Elapsed.TotalMilliseconds, t = Math.Min(1, ms / TintFadeMs);
            // A slow colour fade needs no 60 fps: every step repaints the whole island and the pieces beside it.
            if (t < 1 && ms - _tintDrawnAt < 1000.0 / IslandLook.AmbientFps) return;
            _tintDrawnAt = ms;
            SetTint(_tintFrom.Toward(_tintTo, 1 - Math.Pow(1 - t, 2)));
            if (t < 1) return;
            _tintFade?.Dispose();
            _tintFade = null;
        });
    }

    void SetTint(ScreenTint tint)
    {
        _tintNow = tint;
        Shape.Tint = tint;
        IslandSurface.Rim.Color = tint.RimAlpha > 0
            ? Color.FromArgb((byte)Math.Round(tint.RimAlpha * 255), tint.Rim.R, tint.Rim.G, tint.Rim.B)
            : Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF);
        ApplyLook(tintChanged: true);
    }

    /// <summary>The island's colour for the bits beside it: the glass's colour (or the plain one) under the tint.</summary>
    Color SurfaceColour(bool glass)
    {
        var under = glass ? _glass.Average : PlainIsland;
        var c = _tintNow.Over(new Rgb(under.R, under.G, under.B));
        return Color.FromRgb(c.R, c.G, c.B);
    }

    /// <summary>"island-tint [state|r,g,b|off]": the tint as it is, or as if the screen behind were this colour (tests).</summary>
    internal string TintCommand(string arg)
    {
        arg = arg.Trim();
        if (arg == "off")
        {
            _tintTimer?.Stop();
            FadeTint(ScreenTint.None);
        }
        else if (arg.Split(',') is { Length: 3 } p && byte.TryParse(p[0], out var r) && byte.TryParse(p[1], out var g) && byte.TryParse(p[2], out var b))
        {
            // Held: the timer would read the real screen again.
            _tintTimer?.Stop();
            FadeTint(ScreenTint.From(new Rgb(r, g, b)));
        }
        else if (arg is not ("" or "state")) return "error: island-tint state|<r,g,b>|off";
        var t = _tintTo;
        return $"watching={_tintTimer?.IsEnabled == true} glass={t.Glass.R},{t.Glass.G},{t.Glass.B}@{t.GlassAlpha:0.##} rim={t.Rim.R},{t.Rim.G},{t.Rim.B}@{t.RimAlpha:0.##} " +
               $"halo={t.HaloAlpha:0.##} fill={IslandSurface.Fill.Color}";
    }

    /// <summary>The screen's pixels in a small rectangle (BGRA), or null. Shammy's own windows aren't in it: it's below them.</summary>
    static byte[]? ReadStrip(int left, int top, int w, int h)
    {
        var screenDc = Native.GetDC(IntPtr.Zero);
        var memDc = Native.CreateCompatibleDC(screenDc);
        var bmp = Native.CreateCompatibleBitmap(screenDc, w, h);
        var old = bmp == IntPtr.Zero ? IntPtr.Zero : Native.SelectObject(memDc, bmp);
        try
        {
            if (bmp == IntPtr.Zero || !Native.BitBlt(memDc, 0, 0, w, h, screenDc, left, top, Native.SRCCOPY)) return null;
            Native.SelectObject(memDc, old);
            old = IntPtr.Zero;
            var pixels = new byte[w * h * 4];
            var bmi = new Native.BITMAPINFOHEADER { biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            return Native.GetDIBits(memDc, bmp, 0, (uint)h, pixels, ref bmi, 0) == 0 ? null : pixels;
        }
        finally
        {
            if (old != IntPtr.Zero) Native.SelectObject(memDc, old);
            if (bmp != IntPtr.Zero) Native.DeleteObject(bmp);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
