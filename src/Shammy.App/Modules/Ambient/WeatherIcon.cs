using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Shammy.Core.Ambient;

namespace Shammy.Modules.Ambient;

/// <summary>
/// A weather icon composed from Segoe Fluent glyphs (sun, moon, cloud, bolt, fog, snow) plus drawn
/// drops, since the icon font has no rain or snow cloud glyphs. The cloud gets a black backing so a sun
/// behind it is hidden, which only works because the notch is pure black.
/// </summary>
internal sealed class WeatherIcon : FrameworkElement
{
    static readonly Dictionary<string, (Geometry Shape, Geometry Backing)> Cache = new();

    int _code;
    bool _isDay = true;
    bool _set;

    /// <summary>What hides the sun behind a cloud: the surface the icon sits on (black notch, #121214 card).</summary>
    public Brush Backing { get; init; } = Brushes.Black;

    /// <summary>The sun's colour; the v2 cards use the weather accent instead of the theme's amber.</summary>
    public Brush? Sun { get; init; }

    public void Set(int code, bool isDay)
    {
        if (_set && code == _code && isDay == _isDay) return;
        _code = code;
        _isDay = isDay;
        _set = true;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!_set) return;
        double s = Math.Min(ActualWidth, ActualHeight);
        if (s <= 0) return;
        var fg = Ui.Tokens.Res("Fg");
        var amber = Sun ?? Ui.Tokens.Res("Amber");
        var accent = Ui.Tokens.Res("AccentBrush");
        var sky = _isDay ? WeatherCodes.SunGlyph : WeatherCodes.MoonGlyph;
        var skyBrush = _isDay ? amber : fg;

        switch (WeatherCodes.Kind(_code))
        {
            case WeatherKind.Clear when _code == 0:
                Glyph(dc, sky, new Rect(0, 0, s, s), skyBrush);
                break;
            case WeatherKind.Clear or WeatherKind.PartlyCloudy:
                // Mostly clear shows more sky than partly cloudy.
                double sun = _code == 1 ? 0.7 : 0.62;
                Glyph(dc, sky, new Rect(s * (1 - sun), 0, s * sun, s * sun), skyBrush);
                Glyph(dc, WeatherCodes.CloudGlyph, new Rect(0, s * 0.3, s * 0.82, s * 0.7), fg, backing: true);
                break;
            case WeatherKind.Fog:
                Glyph(dc, WeatherCodes.FogGlyph, new Rect(0, s * 0.1, s, s * 0.8), fg);
                break;
            case WeatherKind.Drizzle or WeatherKind.Rain:
                Glyph(dc, WeatherCodes.CloudGlyph, new Rect(0, 0, s, s * 0.66), fg);
                Drops(dc, s, accent, WeatherCodes.Kind(_code) == WeatherKind.Drizzle ? 1 : Math.Max(2, WeatherCodes.Intensity(_code)));
                break;
            case WeatherKind.Snow:
                Glyph(dc, WeatherCodes.CloudGlyph, new Rect(0, 0, s, s * 0.66), fg);
                Flakes(dc, s, fg);
                break;
            case WeatherKind.Thunderstorm:
                Glyph(dc, WeatherCodes.CloudGlyph, new Rect(0, 0, s, s * 0.66), fg);
                Glyph(dc, WeatherCodes.BoltGlyph, new Rect(s * 0.34, s * 0.52, s * 0.34, s * 0.48), amber, backing: true);
                break;
            default:
                Glyph(dc, WeatherCodes.CloudGlyph, new Rect(0, s * 0.1, s, s * 0.8), fg);
                break;
        }
    }

    static void Drops(DrawingContext dc, double s, Brush brush, int count)
    {
        var pen = new Pen(brush, Math.Max(1.2, s * 0.07)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        double len = count == 1 ? s * 0.12 : s * 0.2;
        int n = Math.Min(3, count + 1);
        for (int i = 0; i < n; i++)
        {
            double x = s * (0.32 + i * 0.2 - (n - 2) * 0.05);
            double y = s * 0.72;
            dc.DrawLine(pen, new Point(x + len * 0.35, y), new Point(x, y + len));
        }
    }

    static void Flakes(DrawingContext dc, double s, Brush brush)
    {
        double r = Math.Max(1, s * 0.055);
        foreach (var (x, y) in new[] { (0.3, 0.8), (0.5, 0.9), (0.7, 0.8) })
            dc.DrawEllipse(brush, null, new Point(s * x, s * y), r, r);
    }

    /// <summary>Draw a glyph scaled to fit <paramref name="box"/>, optionally over a black silhouette.</summary>
    void Glyph(DrawingContext dc, string glyph, Rect box, Brush brush, bool backing = false)
    {
        var (shape, back) = Shapes(glyph);
        var b = shape.Bounds;
        if (b.IsEmpty || b.Width <= 0 || b.Height <= 0) return;
        double k = Math.Min(box.Width / b.Width, box.Height / b.Height);
        var m = new Matrix();
        m.Translate(-b.X, -b.Y);
        m.Scale(k, k);
        m.Translate(box.X + (box.Width - b.Width * k) / 2, box.Y + (box.Height - b.Height * k) / 2);
        dc.PushTransform(new MatrixTransform(m));
        if (backing)
        {
            // Slightly fattened so whatever sits behind doesn't touch the outline.
            dc.DrawGeometry(Backing, new Pen(Backing, 2.2 / k), back);
        }
        dc.DrawGeometry(brush, null, shape);
        dc.Pop();
    }

    (Geometry Shape, Geometry Backing) Shapes(string glyph)
    {
        if (Cache.TryGetValue(glyph, out var cached)) return cached;
        var typeface = new Typeface(Ui.Tokens.FontRes("IconFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 100, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var shape = text.BuildGeometry(new Point(0, 0));
        shape.Freeze();

        // The outer contour alone, filled, is the glyph's silhouette.
        var flat = shape.GetFlattenedPathGeometry();
        var outer = flat.Figures
            .Select(f => new PathGeometry(new[] { f.Clone() }))
            .OrderByDescending(g => g.Bounds.IsEmpty ? 0 : g.Bounds.Width * g.Bounds.Height)
            .FirstOrDefault();
        Geometry backing = outer is null ? Geometry.Empty : MakeSolid(outer);
        backing.Freeze();
        return Cache[glyph] = (shape, backing);
    }

    static Geometry MakeSolid(PathGeometry outer)
    {
        foreach (var f in outer.Figures) f.IsFilled = true;
        outer.FillRule = FillRule.Nonzero;
        return outer;
    }
}
