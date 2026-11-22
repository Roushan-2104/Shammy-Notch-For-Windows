using Shammy.Core.Music;

namespace Shammy.Core.Notch;

/// <summary>
/// How the island takes on the colour of what's behind it. Its glass gets a little of that colour, its rim a light
/// line of it, and a soft halo of it fades out round its edge. A grey or white screen adds no colour: on a white page
/// the island stays dark glass with a faint dark edge, never light glass on a light page.
/// </summary>
public readonly record struct ScreenTint(Rgb Glass, double GlassAlpha, Rgb Rim, double RimAlpha, Rgb Halo, double HaloAlpha)
{
    /// <summary>No tint: plain dark glass with its usual hairline.</summary>
    public static readonly ScreenTint None = new(default, 0, default, 0, default, 0);

    /// <summary>The average colour of a strip of screen pixels (BGRA). A strip that's nearly all one colour still counts fully.</summary>
    public static Rgb Average(ReadOnlySpan<byte> bgra)
    {
        long r = 0, g = 0, b = 0, n = 0;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            b += bgra[i];
            g += bgra[i + 1];
            r += bgra[i + 2];
            n++;
        }
        return n == 0 ? default : new Rgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    /// <summary>Below this much colour (0..1, max − min channel) a screen counts as grey.</summary>
    public const double MinChroma = 0.12;

    /// <summary>The tint for a screen whose colour around the island is <paramref name="behind"/>.</summary>
    public static ScreenTint From(Rgb behind)
    {
        var (h, s, l) = ToHsl(behind);
        // How much colour there really is (HSL saturation runs away near black and white, chroma doesn't).
        double chroma = (Math.Max(behind.R, Math.Max(behind.G, behind.B)) - Math.Min(behind.R, Math.Min(behind.G, behind.B))) / 255.0;
        if (chroma < MinChroma)
        {
            // Grey, near-black or near-white: no colour. A light screen gets a faint dark edge so the island still
            // stands out; a dark one keeps the island's own hairline.
            return l > 0.6 ? new ScreenTint(default, 0, new Rgb(0, 0, 0), 0.12, new Rgb(0, 0, 0), 0.10) : None;
        }
        double strength = Math.Clamp((chroma - MinChroma) / 0.4, 0, 1);
        return new ScreenTint(
            FromHsl(h, Math.Min(1, s * 1.1), 0.32), 0.10 + 0.14 * strength,
            FromHsl(h, Math.Min(1, s), 0.74), 0.18 + 0.16 * strength,
            FromHsl(h, Math.Min(1, s * 1.1), 0.52), 0.12 + 0.14 * strength);
    }

    /// <summary>Partway from this tint to <paramref name="to"/> (t from 0 to 1), so a new screen fades in rather than snapping.</summary>
    public ScreenTint Toward(ScreenTint to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        // An untinted side borrows the other side's colour, so the fade changes strength, not hue.
        var glass = GlassAlpha == 0 ? to.Glass : to.GlassAlpha == 0 ? Glass : Lerp(Glass, to.Glass, t);
        var rim = RimAlpha == 0 ? to.Rim : to.RimAlpha == 0 ? Rim : Lerp(Rim, to.Rim, t);
        var halo = HaloAlpha == 0 ? to.Halo : to.HaloAlpha == 0 ? Halo : Lerp(Halo, to.Halo, t);
        return new ScreenTint(glass, GlassAlpha + (to.GlassAlpha - GlassAlpha) * t, rim, RimAlpha + (to.RimAlpha - RimAlpha) * t,
            halo, HaloAlpha + (to.HaloAlpha - HaloAlpha) * t);
    }

    /// <summary><paramref name="under"/> with the tint's glass colour laid over it (what the island's surface ends up as).</summary>
    public Rgb Over(Rgb under) => GlassAlpha <= 0 ? under : Lerp(under, Glass, GlassAlpha);

    static Rgb Lerp(Rgb a, Rgb b, double t) => new(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    static (double H, double S, double L) ToHsl(Rgb c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min;
        if (d < 1e-6) return (0, 0, l);
        double s = d / (1 - Math.Abs(2 * l - 1));
        double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        if (h < 0) h += 360;
        return (h, Math.Min(1, s), l);
    }

    static Rgb FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        var (r, g, b) = h < 60 ? (c, x, 0d) : h < 120 ? (x, c, 0d) : h < 180 ? (0d, c, x) : h < 240 ? (0d, x, c) : h < 300 ? (x, 0d, c) : (c, 0d, x);
        return new Rgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
