using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Shammy.Shell;

/// <summary>
/// The island's soft shadow, without a live effect: one small blurred rounded shape is rendered once into a frozen
/// bitmap and cut into nine pieces; each frame only stretches those pieces round the island's current bounds. A live
/// DropShadowEffect was re-rasterised on every repaint of the layered window.
/// </summary>
internal sealed class IslandShadow : FrameworkElement
{
    /// <summary>The prerendered shape's corner radius (the closed pill's) and how far its blur reaches.</summary>
    const double Radius = 19, Blur = 16;

    /// <summary>How far the shadow sits below the island.</summary>
    const double Fall = 5;

    /// <summary>Border round the shape inside the bitmap: room for the blur.</summary>
    const double Pad = Blur + 2;

    /// <summary>Each corner piece's size: the pad plus the corner itself.</summary>
    const double Corner = Pad + Radius;

    /// <summary>The prerendered shape: two corners and a 2 px straight middle each way.</summary>
    const double Middle = 2, Size = 2 * Corner + Middle;

    static readonly Lazy<ImageSource[]> Pieces = new(Render);

    Rect _island = Rect.Empty;

    public IslandShadow() => IsHitTestVisible = false;

    public double CenterX { get; set; }

    public void Update(double width, double height)
    {
        _island = width < 1 || height < 0.5 ? Rect.Empty : new Rect(CenterX - width / 2, 0, width, height);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_island.IsEmpty) return;
        var p = Pieces.Value;
        var outer = new Rect(_island.X - Pad, _island.Y - Pad + Fall, _island.Width + 2 * Pad, _island.Height + 2 * Pad);
        // A small island: shrink the corners to fit rather than overlapping them.
        double c = Math.Min(Corner, Math.Min(outer.Width / 2, outer.Height / 2));
        // The pieces meet exactly on whole device pixels: overlapping them doubled the shadow's alpha into dark
        // lines, and a gap would show a light one.
        var dpi = VisualTreeHelper.GetDpi(this);
        double Snap(double v, double scale) => Math.Round(v * scale) / scale;
        double[] xb = { Snap(outer.X, dpi.DpiScaleX), Snap(outer.X + c, dpi.DpiScaleX), Snap(outer.Right - c, dpi.DpiScaleX), Snap(outer.Right, dpi.DpiScaleX) };
        double[] yb = { Snap(outer.Y, dpi.DpiScaleY), Snap(outer.Y + c, dpi.DpiScaleY), Snap(outer.Bottom - c, dpi.DpiScaleY), Snap(outer.Bottom, dpi.DpiScaleY) };
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
            {
                double w = xb[col + 1] - xb[col], h = yb[row + 1] - yb[row];
                if (w <= 0 || h <= 0) continue;
                dc.DrawImage(p[row * 3 + col], new Rect(xb[col], yb[row], w, h));
            }
    }

    static ImageSource[] Render()
    {
        const double Scale = 2;
        var shape = new Border
        {
            Width = Size - 2 * Pad,
            Height = Size - 2 * Pad,
            CornerRadius = new CornerRadius(Radius),
            Background = Brushes.Black,
            Opacity = 0.55,
            Effect = new BlurEffect { Radius = Blur, KernelType = KernelType.Gaussian },
        };
        var host = new Grid { Width = Size, Height = Size, Children = { shape } };
        host.Measure(new Size(Size, Size));
        host.Arrange(new Rect(0, 0, Size, Size));
        var bitmap = new RenderTargetBitmap((int)(Size * Scale), (int)(Size * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(host);
        bitmap.Freeze();

        int c = (int)Math.Round(Corner * Scale), m = (int)Math.Round(Middle * Scale), full = bitmap.PixelWidth;
        int[] xs = { 0, c, full - c }, ws = { c, m, c };
        var pieces = new ImageSource[9];
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
            {
                var piece = new CroppedBitmap(bitmap, new Int32Rect(xs[col], xs[row], ws[col], ws[row]));
                piece.Freeze();
                pieces[row * 3 + col] = piece;
            }
        return pieces;
    }
}
