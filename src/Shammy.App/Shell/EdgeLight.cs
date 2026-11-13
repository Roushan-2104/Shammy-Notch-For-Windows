using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Shammy.Shell;

/// <summary>
/// The quiet rim light: a thin (~1.5 px) line of light just inside the island's lower edge, fading out toward the sides.
/// It shows in two ways. A state colour while something lasts (music in the album's colour, focus blue, a live
/// microphone red, a meeting about to start green), breathing softly — the island's ambience ticker sets the breath.
/// And a short flash (a notification, charging) or a steady hold (listening, a finished timer) on top of that.
/// At rest it draws nothing and its timer is stopped, so an idle island costs no extra repaints.
/// </summary>
internal sealed class EdgeLight : FrameworkElement
{
    static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(33);
    const double FadeIn = 0.15, FadeOut = 0.6;

    /// <summary>The line itself, and a faint wider light under it.</summary>
    const double Line = 1.5, Soft = 4;
    const double SoftOpacity = 0.18;

    /// <summary>How bright the state colour gets at the top of a breath (flashes and holds go to full).</summary>
    const double AmbientPeak = 0.75;

    readonly DispatcherTimer _tick;
    readonly System.Diagnostics.Stopwatch _clock = new();
    readonly RimBrush _flash = new(), _ambient = new();
    Geometry _edge = Geometry.Empty, _island = Geometry.Empty;
    Color? _hold, _state;
    double _flashLeft, _level, _breath = Shammy.Core.Notch.RimLight.Steady;
    bool _enabled = true, _shown = true;

    public EdgeLight()
    {
        IsHitTestVisible = false;
        _tick = new DispatcherTimer(Frame, DispatcherPriority.Render, (_, _) =>
        {
            double dt = Math.Min(0.1, _clock.Elapsed.TotalSeconds);
            _clock.Restart();
            Advance(dt);
        }, Dispatcher);
    }

    public double CenterX { get; set; }

    /// <summary>Light the rim briefly in <paramref name="color"/>, then fade back.</summary>
    public void Flash(Color color, double seconds = 1.5)
    {
        if (!_enabled) return;
        // A steady rim (listening) keeps its own colour; the flash just extends how long it shows.
        if (_hold is null) _flash.Color = color;
        _flashLeft = Math.Max(_flashLeft, seconds);
        Run();
    }

    /// <summary>Keep the rim lit in <paramref name="color"/> until called with null.</summary>
    public void Hold(Color? color)
    {
        if (_hold == color) return;
        _hold = color;
        if (color is { } c) _flash.Color = c;
        Run();
    }

    /// <summary>The state colour (music, focus, a live microphone, a meeting soon), or null for none.</summary>
    public void SetState(Color? color)
    {
        if (_state == color) return;
        _state = color;
        if (color is { } c) _ambient.Color = c;
        InvalidateVisual();
    }

    /// <summary>The state colour's breath, 0..1 (set by the ambience ticker; held steady when motion is off).</summary>
    public double Breath
    {
        get => _breath;
        set
        {
            if (Math.Abs(_breath - value) < 0.004) return;
            _breath = value;
            if (_state is not null && _enabled && _shown) InvalidateVisual();
        }
    }

    public string Describe() =>
        $"rim={(_state is { } s ? s.ToString() : "-")} breath={_breath:0.00} flash={_level:0.00}{(_hold is { } h ? $" hold={h}" : "")} enabled={_enabled}";

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

    /// <summary>Off while the island itself is hidden (fullscreen apps).</summary>
    public bool Shown
    {
        get => _shown;
        set
        {
            if (_shown == value) return;
            _shown = value;
            InvalidateVisual();
        }
    }

    /// <param name="island">The island's outline: the soft light under the line stays inside it.</param>
    public void UpdateShape(double w, double h, double r, Geometry island)
    {
        _edge = NotchShape.BuildLowerEdge(CenterX, w, h, r, Line / 2 + 0.25);
        _island = island;
        if (_level > 0 || _state is not null) InvalidateVisual();
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
        if (_level != was) InvalidateVisual();
        if (!on && _level <= 0) _tick.Stop();
        // Held fully lit with no flash counting down: nothing will change until Hold(null) or a flash restarts it.
        else if (on && _level >= 1 && _flashLeft <= 0) _tick.Stop();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!_enabled || !_shown || _edge.IsEmpty()) return;
        double eased = _level * _level * (3 - 2 * _level);
        // The flash covers the state colour while it shows, then hands back to it.
        double ambient = _state is null ? 0 : AmbientPeak * _breath * (1 - eased);
        if (ambient <= 0.003 && eased <= 0.003) return;
        dc.PushClip(_island);
        if (ambient > 0.003) _ambient.Draw(dc, _edge, ambient);
        if (eased > 0.003) _flash.Draw(dc, _edge, eased);
        dc.Pop();
    }

    /// <summary>One colour's two pens, sharing a gradient that fades the line out toward the island's sides.</summary>
    sealed class RimBrush
    {
        readonly GradientStop _a = new(Colors.Transparent, 0), _b = new(Colors.White, 0.22), _c = new(Colors.White, 0.78), _d = new(Colors.Transparent, 1);
        readonly Pen _line, _soft;

        public RimBrush()
        {
            var brush = new LinearGradientBrush(new GradientStopCollection { _a, _b, _c, _d }, 0);
            _line = new Pen(brush, Line) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            _soft = new Pen(brush, Soft) { LineJoin = PenLineJoin.Round };
        }

        public Color Color
        {
            set
            {
                _a.Color = _d.Color = Color.FromArgb(0, value.R, value.G, value.B);
                _b.Color = _c.Color = value;
            }
        }

        public void Draw(DrawingContext dc, Geometry edge, double opacity)
        {
            dc.PushOpacity(opacity * SoftOpacity);
            dc.DrawGeometry(null, _soft, edge);
            dc.Pop();
            dc.PushOpacity(opacity);
            dc.DrawGeometry(null, _line, edge);
            dc.Pop();
        }
    }
}
