using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Shammy.Infrastructure;
using Shammy.Interop;
using Shammy.Motion;

namespace Shammy.Modules.Wheel;

/// <summary>
/// The island's tiny pulse as the wheel blooms: a faint outline spreads a few pixels out from the pill and fades.
/// Drawn in a little window of the wheel's own laid over the island (the island itself is left alone), click-through
/// and hidden from captures. Lively motion only; frames run just for the pulse.
/// </summary>
internal sealed class IslandPulse : Window
{
    const double Pad = 14, Grow = 9, Duration = 0.42;
    const long WS_EX_TRANSPARENT = 0x20;

    readonly Layer _layer = new();
    readonly SolidColorBrush _brush = new(Colors.White);
    readonly Pen _pen;
    IDisposable? _frames;
    IntPtr _hwnd;
    double _t, _scale = 1;
    Size _pill;

    public IslandPulse()
    {
        Title = "Shammy wheel pulse";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        _pen = new Pen(_brush, 2);
        _layer.Draw = Draw;
        Content = _layer;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.SetExStyle(_hwnd, WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
            Native.SetWindowDisplayAffinity(_hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
        };
        new WindowInteropHelper(this).EnsureHandle();
    }

    /// <summary>Pulses around the island's rectangle (screen pixels) on <paramref name="monitor"/>.</summary>
    public void Play(Rect island, IntPtr monitor)
    {
        if (!MotionPrefs.Lively || island.IsEmpty || island.Width < 4 || _hwnd == IntPtr.Zero) return;
        double s = Native.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
        int pad = (int)Math.Ceiling(Pad * s);
        int left = (int)Math.Floor(island.Left) - pad, top = (int)Math.Floor(island.Top) - pad;
        int width = (int)Math.Ceiling(island.Width) + 2 * pad, height = (int)Math.Ceiling(island.Height) + 2 * pad;
        if (!IsVisible) Show();
        for (int pass = 0; pass < 3; pass++)
        {
            if (Native.GetWindowRect(_hwnd, out var now) && now.Left == left && now.Top == top && now.Width == width && now.Height == height) break;
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, left, top, width, height, Native.SWP_NOACTIVATE);
        }
        _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (_scale <= 0) _scale = s;
        Width = _layer.Width = width / _scale;
        Height = _layer.Height = height / _scale;
        _pill = new Size(island.Width / _scale, island.Height / _scale);
        _brush.Color = Ui.Tokens.Accent.Color;
        _t = 0;
        _frames ??= Animator.Subscribe(OnFrame);
    }

    void OnFrame(double dt)
    {
        _t += dt;
        if (_t >= Duration)
        {
            _frames?.Dispose();
            _frames = null;
            Hide();
            return;
        }
        _layer.InvalidateVisual();
    }

    void Draw(DrawingContext dc)
    {
        if (_frames is null) return;
        double t = Math.Clamp(_t / Duration, 0, 1), eased = 1 - (1 - t) * (1 - t);
        double grow = Grow * eased;
        var r = new Rect(Pad - grow, Pad - grow, _pill.Width + 2 * grow, _pill.Height + 2 * grow);
        double corner = Math.Min(r.Height / 2, 22 + grow);
        dc.PushOpacity(0.7 * (1 - t));
        dc.DrawRoundedRectangle(null, _pen, r, corner, corner);
        dc.Pop();
    }

    sealed class Layer : FrameworkElement
    {
        public Action<DrawingContext>? Draw;

        public Layer() => IsHitTestVisible = false;

        protected override void OnRender(DrawingContext dc) => Draw?.Invoke(dc);
    }
}
