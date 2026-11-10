using System.Windows;
using System.Windows.Media;

namespace Shammy.Shell;

/// <summary>
/// Draws the notch: flush with the top edge of the screen, with concave "shoulders" where it meets the
/// edge and smooth, continuous corners at the bottom, like the MacBook cut-out it imitates.
/// </summary>
internal sealed class NotchShape : FrameworkElement
{
    const double ShoulderRadius = 7;

    Geometry _geometry = Geometry.Empty;

    public double CenterX { get; set; }
    public Brush Fill { get; set; } = Brushes.Black;
    public Pen? Stroke { get; set; }
    public Geometry Geometry => _geometry;

    public void Update(double width, double height, double radius)
    {
        _geometry = Build(CenterX, width, height, radius);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc) => dc.DrawGeometry(Fill, Stroke, _geometry);

    /// <summary>The same outline without the top edge (it sits against the screen or taskbar), for the shammy.</summary>
    public static Geometry BuildOutline(double cx, double w, double h, double r)
    {
        if (w < 1 || h < 0.5) return Geometry.Empty;

        double rs = Math.Min(ShoulderRadius, h / 2);
        r = Math.Clamp(r, 0, Math.Min(h - rs, w / 2));
        double x0 = cx - w / 2, x1 = cx + w / 2;

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(x0 - rs, 0), false, false);
            ctx.QuadraticBezierTo(new Point(x0, 0), new Point(x0, rs), true, true);
            Bottom(ctx, x0, x1, h, r, Math.Min(h - rs, w / 2));
            ctx.LineTo(new Point(x1, rs), true, true);
            ctx.QuadraticBezierTo(new Point(x1, 0), new Point(x1 + rs, 0), true, true);
        }
        g.Freeze();
        return g;
    }

    public static Geometry Build(double cx, double w, double h, double r)
    {
        if (w < 1 || h < 0.5) return Geometry.Empty;

        double rs = Math.Min(ShoulderRadius, h / 2);
        r = Math.Clamp(r, 0, Math.Min(h - rs, w / 2));
        double x0 = cx - w / 2, x1 = cx + w / 2;

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(x0 - rs, 0), true, true);
            ctx.QuadraticBezierTo(new Point(x0, 0), new Point(x0, rs), true, true);
            Bottom(ctx, x0, x1, h, r, Math.Min(h - rs, w / 2));
            ctx.LineTo(new Point(x1, rs), true, true);
            ctx.QuadraticBezierTo(new Point(x1, 0), new Point(x1 + rs, 0), true, true);
        }
        g.Freeze();
        return g;
    }

    /// <summary>
    /// Down the left side, round both bottom corners and stop at the top of the right one. Each corner is one
    /// smooth curve (<see cref="Shammy.Core.Notch.SmoothCorner"/>) rather than a quarter circle.
    /// </summary>
    static void Bottom(StreamGeometryContext ctx, double x0, double x1, double h, double r, double room)
    {
        var (extent, handle) = Shammy.Core.Notch.SmoothCorner.For(r, room);
        ctx.LineTo(new Point(x0, h - extent), true, true);
        if (extent > 0) ctx.BezierTo(new Point(x0, h - handle), new Point(x0 + handle, h), new Point(x0 + extent, h), true, true);
        ctx.LineTo(new Point(x1 - extent, h), true, true);
        if (extent > 0) ctx.BezierTo(new Point(x1 - handle, h), new Point(x1, h - handle), new Point(x1, h - extent), true, true);
    }
}
