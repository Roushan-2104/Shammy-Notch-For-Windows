using System.Windows;
using System.Windows.Media;

namespace Shammy.Shell;

/// <summary>
/// Draws the island: a detached, fully rounded shape floating just below the top of the screen. Closed it is a pill;
/// open it is a slab with all four corners rounded by smooth, continuous curves. Inside it, from the bottom up: the
/// fake glass (a pre-blurred, pre-tinted crop of the wallpaper, or a plain tint), the soft colour waves (a tiny bitmap
/// scaled up) and a faint hairline. No effects: everything here is a plain image or fill, cheap to repaint.
/// </summary>
internal sealed class NotchShape : FrameworkElement
{
    Geometry _geometry = Geometry.Empty;
    Rect _bounds = Rect.Empty;
    static readonly Pen Hairline = MakeHairline();

    public NotchShape()
    {
        // The glass and the waves are tiny bitmaps scaled up: linear filtering is what makes them soft.
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
    }

    public double CenterX { get; set; }

    /// <summary>The island's plain colour: used when there is no glass, and by the bits drawn beside it (the action circle).</summary>
    public Brush Fill { get; set; } = Brushes.Black;

    public Geometry Geometry => _geometry;

    /// <summary>The blurred, tinted wallpaper under the island (null: the plain <see cref="Fill"/>).</summary>
    public ImageSource? Glass { get; set; }

    /// <summary>Where <see cref="Glass"/> lands, in this element's coordinates (it covers the whole window).</summary>
    public Rect GlassRect { get; set; }

    /// <summary>The soft waves, stretched over the island's bounds (null: none).</summary>
    public ImageSource? Waves { get; set; }

    public double WavesOpacity { get; set; } = 1;

    /// <summary>Draw the faint light hairline round the island (off for the shadow's copy).</summary>
    public bool Edge { get; set; } = true;

    public void Update(double width, double height, double radius)
    {
        _geometry = Build(CenterX, width, height, radius);
        _bounds = _geometry.IsEmpty() ? Rect.Empty : new Rect(CenterX - width / 2, 0, width, height);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_geometry.IsEmpty()) return;
        if (Glass is { } glass)
        {
            // An opaque base under the glass: the glass is a touch see-through, and the island must look the same
            // over its own shadow as the liquid circle beside it does over the desktop.
            dc.DrawGeometry(Fill, null, _geometry);
            dc.PushClip(_geometry);
            dc.DrawImage(glass, GlassRect);
            dc.Pop();
        }
        else
        {
            dc.DrawGeometry(Fill, null, _geometry);
        }
        if (Waves is { } waves && WavesOpacity > 0.003 && !_bounds.IsEmpty)
        {
            dc.PushClip(_geometry);
            dc.PushOpacity(WavesOpacity);
            dc.DrawImage(waves, _bounds);
            dc.Pop();
            dc.Pop();
        }
        if (Edge) dc.DrawGeometry(null, Hairline, _geometry);
    }

    static Pen MakeHairline()
    {
        // Lit from above: a whisper of light along the top, nothing along the bottom (the rim light lives there).
        var brush = new LinearGradientBrush(Color.FromArgb(26, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90);
        brush.Freeze();
        var pen = new Pen(brush, 1);
        pen.Freeze();
        return pen;
    }

    /// <summary>
    /// The island's outline: <paramref name="w"/>×<paramref name="h"/>, centred on <paramref name="cx"/>, top at 0. All four
    /// corners are one smooth curve each (<see cref="Shammy.Core.Notch.SmoothCorner"/>); a radius of half the height
    /// or more gives a pill with round ends.
    /// </summary>
    public static Geometry Build(double cx, double w, double h, double r)
    {
        if (w < 1 || h < 0.5) return Geometry.Empty;
        double room = Math.Min(h / 2, w / 2);
        r = Math.Clamp(r, 0, room);
        // An open slab's top corners stay a little tighter than its bottom ones: the content was laid out for a notch
        // with square top corners, and its chips and buttons sit close to them. A pill keeps round ends.
        double top = r >= room - 0.5 ? r : Math.Min(r, TopRadius);
        var (e, k) = Shammy.Core.Notch.SmoothCorner.For(r, room);
        var (et, kt) = Shammy.Core.Notch.SmoothCorner.For(top, room);
        double x0 = cx - w / 2, x1 = cx + w / 2;

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(x0 + et, 0), true, true);
            ctx.LineTo(new Point(x1 - et, 0), true, true);
            if (et > 0) ctx.BezierTo(new Point(x1 - kt, 0), new Point(x1, kt), new Point(x1, et), true, true);
            ctx.LineTo(new Point(x1, h - e), true, true);
            if (e > 0) ctx.BezierTo(new Point(x1, h - k), new Point(x1 - k, h), new Point(x1 - e, h), true, true);
            ctx.LineTo(new Point(x0 + e, h), true, true);
            if (e > 0) ctx.BezierTo(new Point(x0 + k, h), new Point(x0, h - k), new Point(x0, h - e), true, true);
            ctx.LineTo(new Point(x0, et), true, true);
            if (et > 0) ctx.BezierTo(new Point(x0, kt), new Point(x0 + kt, 0), new Point(x0 + et, 0), true, true);
        }
        g.Freeze();
        return g;
    }

    /// <summary>The largest top-corner radius of an open slab.</summary>
    public const double TopRadius = 26;

    /// <summary>
    /// The island's lower edge only: up each side to where the bottom corners start, round both corners and along the
    /// bottom, <paramref name="inset"/> inside the outline (the rim light sits just inside the island).
    /// </summary>
    public static Geometry BuildLowerEdge(double cx, double w, double h, double r, double inset)
    {
        if (w < 2 * inset + 1 || h < 2 * inset + 0.5) return Geometry.Empty;
        double iw = w - 2 * inset, ih = h - 2 * inset;
        double room = Math.Min(ih / 2, iw / 2);
        r = Math.Clamp(r - inset, 0, room);
        var (e, k) = Shammy.Core.Notch.SmoothCorner.For(r, room);
        double x0 = cx - iw / 2, x1 = cx + iw / 2, bottom = h - inset;
        // The light climbs a little way up the sides, never past the middle.
        double rise = Math.Min(ih / 2, e + 2);

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(x0, bottom - rise), false, false);
            ctx.LineTo(new Point(x0, bottom - e), true, true);
            if (e > 0) ctx.BezierTo(new Point(x0, bottom - k), new Point(x0 + k, bottom), new Point(x0 + e, bottom), true, true);
            ctx.LineTo(new Point(x1 - e, bottom), true, true);
            if (e > 0) ctx.BezierTo(new Point(x1 - k, bottom), new Point(x1, bottom - k), new Point(x1, bottom - e), true, true);
            ctx.LineTo(new Point(x1, bottom - rise), true, true);
        }
        g.Freeze();
        return g;
    }
}
