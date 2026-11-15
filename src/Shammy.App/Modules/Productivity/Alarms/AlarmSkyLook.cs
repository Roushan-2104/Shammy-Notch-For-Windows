using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using Shammy.Core.Productivity;

namespace Shammy.Modules.Productivity.Alarms;

/// <summary>
/// The colours of each <see cref="Sky"/> (night at the top, the horizon at the bottom), its sun (or moon), and the small
/// sun or moon mark the snoozed and missed pills carry. The ringing card, the pre-glow and the pills all read from here.
/// </summary>
internal sealed record AlarmSkyLook(Sky Sky, Color Top, Color Mid, Color Horizon, Color Core, Color Edge, Color Glow, Color Stop, Color Label, double SunX, double SunBottom)
{
    // In the order of Sky: dawn, day, sunset, night.
    static readonly AlarmSkyLook[] Looks =
    {
        new(Sky.Dawn, C("#1E1626"), C("#5A2E3A"), C("#FFB070"), C("#FFF6E6"), C("#FFA860"), C("#FFBA78"), C("#FFF1E4"), C("#E2D4C8"), 400, -8),
        new(Sky.Day, C("#16304F"), C("#2F5C88"), C("#E9C48C"), C("#FFFFFF"), C("#FFE2A8"), C("#FFECBE"), C("#FFFFFF"), C("#D6E2EE"), 470, 40),
        new(Sky.Sunset, C("#170F2E"), C("#5E1E4C"), C("#F2504A"), C("#FFE0C8"), C("#F04A32"), C("#FF6E5A"), C("#FFE9DE"), C("#E2CCD2"), 400, -45),
        new(Sky.Night, C("#04050B"), C("#0A1226"), C("#1C2C50"), C("#F4F7FF"), C("#B8C8F0"), C("#AAC3FF"), C("#E9EEFF"), C("#B9C2D6"), 470, 0),
    };

    public static AlarmSkyLook For(Sky sky) => Looks[(int)sky];

    public static AlarmSkyLook For(Alarm a) => For(AlarmSky.At(a.Hour));

    public bool Moon => Sky == Sky.Night;

    /// <summary>The digits' light: white at the top, the sky's warmth (or the moon's blue) at the foot.</summary>
    public Brush Lit => Frozen(new LinearGradientBrush(Colors.White, Moon ? C("#D6E0FF") : C("#FFD3B2"), 90) { GradientStops = { new(Colors.White, 0.35) } });

    public Brush Background => Frozen(new LinearGradientBrush
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(0, 1),
        GradientStops = { new(Top, 0), new(Top, 0.28), new(Mid, 0.64), new(Horizon, 1) },
    });

    static T Frozen<T>(T brush) where T : Freezable
    {
        brush.Freeze();
        return brush;
    }

    public static Color Alpha(Color c, double a) => Color.FromArgb((byte)Math.Round(a * 255), c.R, c.G, c.B);

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>A soft round halo of <paramref name="color"/>, fading out to its edge.</summary>
    public static Ellipse Halo(Color color, double size, double strength) => new()
    {
        Width = size,
        Height = size,
        IsHitTestVisible = false,
        Fill = Frozen(new RadialGradientBrush
        {
            GradientStops = { new(Alpha(color, strength), 0), new(Alpha(color, strength * 0.36), 0.45), new(Alpha(color, 0), 1) },
        }),
    };

    /// <summary>The sun (a lit disc) or, at night, a crescent moon, <paramref name="size"/> across.</summary>
    public FrameworkElement Body(double size)
    {
        var disc = Frozen(new RadialGradientBrush(Core, Edge) { GradientOrigin = new Point(Moon ? 0.4 : 0.5, 0.4), Center = new Point(Moon ? 0.4 : 0.5, 0.4) });
        if (!Moon) return new Ellipse { Width = size, Height = size, Fill = disc, IsHitTestVisible = false };
        // The moon: the disc less a second one nudged up and right.
        var shape = new CombinedGeometry(GeometryCombineMode.Exclude,
            new EllipseGeometry(new Rect(0, 0, size, size)),
            new EllipseGeometry(new Rect(size * 0.27, -size * 0.1, size, size)));
        return new Path { Data = shape, Fill = disc, Width = size, Height = size, IsHitTestVisible = false };
    }

    /// <summary>The pills' mark: a small sun or moon, <paramref name="size"/> across, faded by <paramref name="opacity"/>.</summary>
    public FrameworkElement Mark(double size, double opacity = 1)
    {
        var body = Body(size);
        body.Opacity = opacity;
        body.VerticalAlignment = VerticalAlignment.Center;
        return body;
    }

    /// <summary>The night's few stars, scattered over the top two thirds of a <paramref name="width"/>-wide sky.</summary>
    public static void Stars(Canvas into, double width)
    {
        foreach (var (x, y, r, o) in new[] { (0.44, 40.0, 2.0, 0.7), (0.53, 90.0, 1.5, 0.5), (0.82, 30.0, 2.0, 0.8), (0.9, 120.0, 1.5, 0.5), (0.37, 150.0, 1.5, 0.4), (0.62, 60.0, 1.0, 0.6), (0.76, 170.0, 1.5, 0.5), (0.94, 70.0, 1.0, 0.6) })
        {
            var star = new Ellipse { Width = r, Height = r, Fill = Ui.Tokens.Frozen(C("#DDE6FF")), Opacity = o, IsHitTestVisible = false };
            Canvas.SetLeft(star, x * width);
            Canvas.SetTop(star, y);
            into.Children.Add(star);
        }
    }
}
