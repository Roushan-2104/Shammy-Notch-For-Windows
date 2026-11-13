using Shammy.Core.Music;

namespace Shammy.Core.Notch;

/// <summary>Where the floating island sits and how tall it is when closed.</summary>
public static class IslandGeometry
{
    /// <summary>The gap between the top of the screen and the island, in DIPs (4 px at 100 %).</summary>
    public const double Gap = 4;

    /// <summary>The closed island (resting, a pill) is a fully rounded pill at least this tall.</summary>
    public const double ClosedHeight = 38;

    /// <summary>The gap snapped to whole device pixels, so the island's top edge stays crisp at any scale.</summary>
    public static double SnappedGap(double dpiScale)
    {
        if (dpiScale <= 0) dpiScale = 1;
        return Math.Max(1, Math.Round(Gap * dpiScale)) / dpiScale;
    }

    /// <summary>A closed state's height grown to the pill height (its content is centred inside); open states are untouched.</summary>
    public static double ClosedPill(double height) => height is > 0 and < ClosedHeight ? ClosedHeight : height;

    /// <summary>How far content shorter than the island sits from its top, to be centred in it.</summary>
    public static double ContentTop(double islandHeight, double contentHeight) =>
        contentHeight > 0 && contentHeight < islandHeight ? Math.Round((islandHeight - contentHeight) / 2) : 0;
}

/// <summary>What Windows and the user allow, and whether anything is going on.</summary>
/// <param name="TransparencyOn">Windows' "Transparency effects" (Personalize › EnableTransparency).</param>
/// <param name="Busy">Music playing, a call, focus, a live activity: the waves may move.</param>
public readonly record struct IslandConditions(bool TransparencyOn, bool BatterySaver, bool HighContrast, bool MotionLively, bool Busy);

/// <param name="Glass">Draw the blurred wallpaper under the tint (else a plain tinted island).</param>
/// <param name="Waves">Draw the soft colour waves at all.</param>
/// <param name="AnimateWaves">Move them (~20 fps); otherwise they hold still and cost nothing.</param>
/// <param name="Breathe">The rim light breathes; otherwise it holds steady.</param>
public readonly record struct IslandLookPlan(bool Glass, bool Waves, bool AnimateWaves, bool Breathe);

public static class IslandLook
{
    public static IslandLookPlan Decide(IslandConditions c)
    {
        bool glass = c.TransparencyOn && !c.BatterySaver && !c.HighContrast;
        bool waves = !c.HighContrast;
        bool still = !c.MotionLively || c.BatterySaver;
        return new(glass, waves, waves && c.Busy && !still, !still && !c.HighContrast);
    }

    /// <summary>Frames per second while the waves or the rim move: slow, soft motion needs no more.</summary>
    public const int AmbientFps = 20;
}

/// <summary>What the rim light along the island's lower edge shows.</summary>
public enum RimState { None, Music, Focus, MicLive, MeetingSoon }

public static class RimLight
{
    public static readonly Rgb Focus = new(0x4C, 0x9A, 0xFF), MicLive = new(0xFF, 0x45, 0x3A), MeetingSoon = new(0x30, 0xD1, 0x58);

    /// <summary>The most urgent state wins: a live microphone, then a meeting about to start, then focus, then music.</summary>
    public static RimState Pick(bool music, bool focus, bool micLive, bool meetingSoon) =>
        micLive ? RimState.MicLive : meetingSoon ? RimState.MeetingSoon : focus ? RimState.Focus : music ? RimState.Music : RimState.None;

    public static Rgb? Colour(RimState state, Rgb album) => state switch
    {
        RimState.Music => album,
        RimState.Focus => Focus,
        RimState.MicLive => MicLive,
        RimState.MeetingSoon => MeetingSoon,
        _ => null,
    };

    /// <summary>Seconds per breath.</summary>
    public const double Period = 4.2;

    /// <summary>The rim's brightness at <paramref name="seconds"/>: a slow, soft swell between 0.45 and 0.85 (quiet even at its peak).</summary>
    public static double Breath(double seconds)
    {
        double phase = (1 - Math.Cos(2 * Math.PI * seconds / Period)) / 2;
        return 0.45 + 0.4 * phase;
    }

    /// <summary>Held still (Calm, animations off, Battery Saver): the middle of a breath.</summary>
    public const double Steady = 0.65;
}

/// <summary>
/// The soft aurora waves inside the island, drawn into a tiny premultiplied BGRA bitmap that is then scaled up with
/// linear filtering (the scaling is the blur). A few thousand pixels per frame, no effects.
/// </summary>
public static class IslandWaves
{
    /// <summary>The bitmap's size: tiny on purpose.</summary>
    public const int Width = 72, Height = 24;

    /// <summary>The strongest any band gets (the owner found brighter glows "too much").</summary>
    public const double MaxAlpha = 0.26;

    static readonly (double Centre, double Amp, double Freq, double Speed, double Sigma, double Phase, double Gain)[] Bands =
    {
        (0.62, 0.13, 0.85, 0.21, 0.17, 0.0, 1.0),
        (0.78, 0.10, 1.25, -0.16, 0.14, 2.1, 0.8),
        (0.46, 0.09, 0.6, 0.12, 0.13, 4.2, 0.6),
    };

    /// <summary>0 at both ends of the island, 1 across its middle, with a long soft shoulder (no visible edge).</summary>
    static double SmoothEnds(double fx)
    {
        double e = Math.Clamp(Math.Min(fx, 1 - fx) / 0.28, 0, 1);
        return e * e * (3 - 2 * e);
    }

    /// <summary>
    /// Renders the bands at time <paramref name="t"/> (seconds) in <paramref name="colours"/> (three; fewer repeat)
    /// into <paramref name="pbgra"/> (w×h×4, premultiplied). <paramref name="strength"/> 0..1 scales them all; 0 clears.
    /// </summary>
    public static void Render(Span<byte> pbgra, int w, int h, IReadOnlyList<Rgb> colours, double t, double strength)
    {
        if (pbgra.Length < w * h * 4) throw new ArgumentException("Buffer too small", nameof(pbgra));
        pbgra[..(w * h * 4)].Clear();
        strength = Math.Clamp(strength, 0, 1);
        if (strength <= 0 || colours.Count == 0 || w <= 0 || h <= 0) return;

        for (int y = 0; y < h; y++)
        {
            double fy = (y + 0.5) / h;
            // Water, not stripes: nothing touches an edge. The light dissolves well before the island's ends and
            // softens towards its top and bottom, so no band is ever cut off by the outline.
            double rows = Math.Pow(Math.Sin(Math.PI * Math.Clamp(fy * 0.92 + 0.04, 0, 1)), 0.7);
            for (int x = 0; x < w; x++)
            {
                double fx = (x + 0.5) / w;
                double ends = SmoothEnds(fx) * rows;
                double r = 0, g = 0, b = 0, a = 0;
                for (int i = 0; i < Bands.Length; i++)
                {
                    var band = Bands[i];
                    double phase = t * band.Speed * 2 * Math.PI;
                    // A swell plus a slower second ripple, so the crest rolls instead of tracing one sine.
                    double centre = band.Centre
                                    + band.Amp * 1.35 * Math.Sin(2 * Math.PI * band.Freq * fx + band.Phase + phase)
                                    + band.Amp * 0.55 * Math.Sin(2 * Math.PI * band.Freq * 2.1 * fx - band.Phase * 0.7 - phase * 0.6 + i)
                                    + 0.035 * Math.Sin(2 * Math.PI * 2.3 * fx - t * 0.5 + i);
                    // The ribbon swells and thins along its length, like light on moving water.
                    double sigma = band.Sigma * (1.15 + 0.4 * Math.Sin(2 * Math.PI * 0.8 * fx - phase * 0.5 + i * 1.7));
                    double d = (fy - centre) / sigma;
                    double alpha = Math.Exp(-d * d) * band.Gain * MaxAlpha * strength * ends;
                    if (alpha < 1e-4) continue;
                    var c = colours[i % colours.Count];
                    // "Over" in premultiplied space, so overlapping bands mix instead of adding up past the cap.
                    r = c.R / 255.0 * alpha + r * (1 - alpha);
                    g = c.G / 255.0 * alpha + g * (1 - alpha);
                    b = c.B / 255.0 * alpha + b * (1 - alpha);
                    a = alpha + a * (1 - alpha);
                }
                // Where ribbons cross they never add up past a soft ceiling: the light stays quiet.
                double cap = MaxAlpha * 1.5 * strength;
                if (a > cap)
                {
                    double f = cap / a;
                    r *= f; g *= f; b *= f; a = cap;
                }
                int o = (y * w + x) * 4;
                pbgra[o] = (byte)Math.Round(b * 255);
                pbgra[o + 1] = (byte)Math.Round(g * 255);
                pbgra[o + 2] = (byte)Math.Round(r * 255);
                pbgra[o + 3] = (byte)Math.Round(a * 255);
            }
        }
    }
}

/// <summary>How Windows lays the wallpaper on a screen (HKCU\Control Panel\Desktop WallpaperStyle / TileWallpaper).</summary>
public enum WallpaperStyle { Fill, Fit, Stretch, Center, Tile, Span }

public readonly record struct PixelRect(double X, double Y, double W, double H);

public static class WallpaperFit
{
    /// <summary>WallpaperStyle "10" fill, "6" fit, "2" stretch, "0" centre (or tile with TileWallpaper "1"), "22" span.</summary>
    public static WallpaperStyle StyleFrom(string? wallpaperStyle, string? tile) => (wallpaperStyle ?? "").Trim() switch
    {
        "0" when (tile ?? "").Trim() == "1" => WallpaperStyle.Tile,
        "0" => WallpaperStyle.Center,
        "2" => WallpaperStyle.Stretch,
        "6" => WallpaperStyle.Fit,
        "22" => WallpaperStyle.Span,
        _ => WallpaperStyle.Fill,
    };

    /// <summary>
    /// Where an image of <paramref name="imageW"/>×<paramref name="imageH"/> lands on a screen of
    /// <paramref name="screenW"/>×<paramref name="screenH"/>, in the screen's pixels. Tile and span are treated as fill
    /// (close enough under a heavy blur and a dark tint).
    /// </summary>
    public static PixelRect Place(double imageW, double imageH, double screenW, double screenH, WallpaperStyle style)
    {
        if (imageW <= 0 || imageH <= 0) return new(0, 0, screenW, screenH);
        switch (style)
        {
            case WallpaperStyle.Stretch:
                return new(0, 0, screenW, screenH);
            case WallpaperStyle.Center:
                return new((screenW - imageW) / 2, (screenH - imageH) / 2, imageW, imageH);
            case WallpaperStyle.Fit:
            {
                double s = Math.Min(screenW / imageW, screenH / imageH);
                return new((screenW - imageW * s) / 2, (screenH - imageH * s) / 2, imageW * s, imageH * s);
            }
            default:
            {
                double s = Math.Max(screenW / imageW, screenH / imageH);
                return new((screenW - imageW * s) / 2, (screenH - imageH * s) / 2, imageW * s, imageH * s);
            }
        }
    }
}

/// <summary>
/// The island's fake glass: a low-resolution crop of the wallpaper under it, blurred once. Built only when the
/// wallpaper, the display or its scale changes; the island then just draws the frozen result scaled up.
/// </summary>
public static class GlassBackdrop
{
    /// <summary>The crop is made at this fraction of the screen's pixels.</summary>
    public const int Downscale = 8;

    /// <summary>
    /// Samples <paramref name="crop"/> (screen pixels) of a wallpaper placed at <paramref name="placed"/> (screen pixels),
    /// from a decoded <paramref name="src"/> (BGRA, srcW×srcH), into an outW×outH opaque BGRA bitmap. Screen outside the
    /// picture takes <paramref name="background"/> (B, G, R).
    /// </summary>
    public static byte[] Crop(ReadOnlySpan<byte> src, int srcW, int srcH, PixelRect placed, PixelRect crop, int outW, int outH, (byte B, byte G, byte R) background)
    {
        var dst = new byte[outW * outH * 4];
        for (int y = 0; y < outH; y++)
        {
            double sy = crop.Y + (y + 0.5) * crop.H / outH;
            double v = (sy - placed.Y) / placed.H;
            for (int x = 0; x < outW; x++)
            {
                double sx = crop.X + (x + 0.5) * crop.W / outW;
                double u = (sx - placed.X) / placed.W;
                int o = (y * outW + x) * 4;
                if (u < 0 || u >= 1 || v < 0 || v >= 1 || srcW <= 0 || srcH <= 0)
                {
                    dst[o] = background.B;
                    dst[o + 1] = background.G;
                    dst[o + 2] = background.R;
                }
                else
                {
                    int px = Math.Min(srcW - 1, (int)(u * srcW)), py = Math.Min(srcH - 1, (int)(v * srcH));
                    int i = (py * srcW + px) * 4;
                    dst[o] = src[i];
                    dst[o + 1] = src[i + 1];
                    dst[o + 2] = src[i + 2];
                }
                dst[o + 3] = 255;
            }
        }
        return dst;
    }

    /// <summary>Three passes of a box blur (close to a gaussian) on an opaque BGRA bitmap, in place. Edges clamp.</summary>
    public static void Blur(byte[] bgra, int w, int h, int radius, int passes = 3)
    {
        if (radius <= 0 || w <= 0 || h <= 0) return;
        var tmp = new byte[bgra.Length];
        for (int p = 0; p < passes; p++)
        {
            BoxPass(bgra, tmp, w, h, radius, horizontal: true);
            BoxPass(tmp, bgra, w, h, radius, horizontal: false);
        }
    }

    static void BoxPass(byte[] from, byte[] to, int w, int h, int r, bool horizontal)
    {
        int lines = horizontal ? h : w, len = horizontal ? w : h;
        int span = 2 * r + 1;
        for (int line = 0; line < lines; line++)
        {
            for (int ch = 0; ch < 3; ch++)
            {
                int Index(int k)
                {
                    k = Math.Clamp(k, 0, len - 1);
                    return (horizontal ? line * w + k : k * w + line) * 4 + ch;
                }
                int sum = 0;
                for (int k = -r; k <= r; k++) sum += from[Index(k)];
                for (int k = 0; k < len; k++)
                {
                    to[Index(k)] = (byte)(sum / span);
                    sum += from[Index(k + r + 1)] - from[Index(k - r)];
                }
            }
            for (int k = 0; k < len; k++) to[(horizontal ? line * w + k : k * w + line) * 4 + 3] = 255;
        }
    }
}
