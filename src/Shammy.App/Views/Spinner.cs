using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Shammy.Views;

/// <summary>
/// An indeterminate ring: a fixed arc that turns. A functional indicator (something is working), so it keeps
/// moving with Calm on, but cheaply: the arc is drawn once (pen and geometry cached) and only its rotation changes,
/// about 30 times a second, while it is on screen.
/// </summary>
internal sealed class Spinner : FrameworkElement
{
    const double DegreesPerSecond = 420, Sweep = 270;
    static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(33);

    readonly RotateTransform _turn = new();
    readonly DispatcherTimer _timer;
    readonly System.Diagnostics.Stopwatch _clock = new();
    Brush _stroke = Brushes.White;
    Pen? _pen;
    Geometry? _arc;
    Size _builtFor;

    public Spinner()
    {
        Width = Height = 18;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _turn;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = Frame };
        _timer.Tick += (_, _) => _turn.Angle = _clock.Elapsed.TotalSeconds * DegreesPerSecond % 360;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _clock.Start();
                _timer.Start();
            }
            else
            {
                _timer.Stop();
                _clock.Stop();
            }
        };
    }

    public Brush Stroke
    {
        get => _stroke;
        set
        {
            _stroke = value;
            _pen = null;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = new Size(double.IsNaN(Width) ? ActualWidth : Width, double.IsNaN(Height) ? ActualHeight : Height);
        if (size.Width <= 0 || size.Height <= 0) return;
        double d = Math.Min(size.Width, size.Height), thickness = Math.Max(2, d / 8);
        if (_pen is null || _arc is null || _builtFor != size)
        {
            _builtFor = size;
            _pen = new Pen(_stroke, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (_stroke.IsFrozen) _pen.Freeze();
            double r = (d - thickness) / 2;
            var c = new Point(size.Width / 2, size.Height / 2);
            double a1 = Sweep * Math.PI / 180;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(c.X + r, c.Y), false, false);
                ctx.ArcTo(new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1)), new Size(r, r), 0, Sweep > 180, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            _arc = g;
        }
        dc.DrawGeometry(null, _pen, _arc);
    }
}
