using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Shammy.Shell;

/// <summary>
/// A thin coloured edge around the notch that appears only when something happens: a short flash for
/// events (new song, notification, timer, charging), or a steady edge while a state lasts (listening).
/// At rest it draws nothing and its timer is stopped, so an idle notch costs no extra repaints.
/// </summary>
internal sealed class EdgeLight : FrameworkElement
{
    static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);
    const double FadeIn = 0.15, FadeOut = 0.6;

    // A crisp line with a little soft light outside it.
    static readonly (double Thickness, double Opacity)[] Strokes = { (7, 0.14), (3.5, 0.3), (1.4, 0.95) };

    readonly DispatcherTimer _tick;
    // Timer ticks arrive late when the desktop is busy; step by real time so a flash always lasts as long.
    readonly System.Diagnostics.Stopwatch _clock = new();
    readonly SolidColorBrush _brush = new(Colors.White);
    // Built once: they follow the brush's colour, so a render allocates nothing.
    readonly Pen[] _pens;
    Geometry _outline = Geometry.Empty;
    Color? _hold;
    double _flashLeft, _level;
    bool _enabled = true, _shown = true;

    public EdgeLight()
    {
        IsHitTestVisible = false;
        _pens = Strokes.Select(s => new Pen(_brush, s.Thickness) { LineJoin = PenLineJoin.Round }).ToArray();
        _tick = new DispatcherTimer(Frame, DispatcherPriority.Render, (_, _) =>
        {
            double dt = Math.Min(0.1, _clock.Elapsed.TotalSeconds);
            _clock.Restart();
            Advance(dt);
        }, Dispatcher);
    }

    public double CenterX { get; set; }

    /// <summary>Light the edge briefly in <paramref name="color"/>, then fade back to plain black.</summary>
    public void Flash(Color color, double seconds = 1.5)
    {
        if (!_enabled) return;
        // A steady edge (listening) keeps its own colour; the flash just extends how long it shows.
        if (_hold is null) _brush.Color = color;
        _flashLeft = Math.Max(_flashLeft, seconds);
        Run();
    }

    /// <summary>Keep the edge lit in <paramref name="color"/> until called with null.</summary>
    public void Hold(Color? color)
    {
        if (_hold == color) return;
        _hold = color;
        if (color is { } c) _brush.Color = c;
        Run();
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value)
            {
                _flashLeft = 0;
                _level = 0;
                _tick.Stop();
            }
            InvalidateVisual();
        }
    }

    /// <summary>Off while the notch itself is hidden (fullscreen apps).</summary>
    public bool Shown
    {
        get => _shown;
        set
        {
            // Set on every state change; only a real change repaints the (window-sized) edge.
            if (_shown == value) return;
            _shown = value;
            InvalidateVisual();
        }
    }

    public void UpdateShape(double w, double h, double r)
    {
        _outline = NotchShape.BuildOutline(CenterX, w, h, r);
        if (_level > 0) InvalidateVisual();
    }

    void Run()
    {
        if (!_enabled || _tick.IsEnabled) return;
        _clock.Restart();
        _tick.Start();
    }

    void Advance(double dt)
    {
        _flashLeft = Math.Max(0, _flashLeft - dt);
        bool on = _enabled && (_hold is not null || _flashLeft > 0);
        double was = _level;
        _level = on ? Math.Min(1, _level + dt / FadeIn) : Math.Max(0, _level - dt / FadeOut);
        // Only a change of brightness needs a repaint; a steady edge costs nothing.
        if (_level != was) InvalidateVisual();
        if (!on && _level <= 0) _tick.Stop();
        // Held fully lit with no flash counting down: nothing will change until Hold(null) or a flash restarts it.
        else if (on && _level >= 1 && _flashLeft <= 0) _tick.Stop();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!_enabled || !_shown || _level <= 0 || _outline.IsEmpty()) return;
        // Ease so the light blooms in and dies away softly instead of ramping linearly.
        double eased = _level * _level * (3 - 2 * _level);
        for (int i = 0; i < Strokes.Length; i++)
        {
            dc.PushOpacity(Strokes[i].Opacity * eased);
            dc.DrawGeometry(null, _pens[i], _outline);
            dc.Pop();
        }
    }
}
