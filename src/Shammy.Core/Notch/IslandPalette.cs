using Shammy.Core.Music;
using Shammy.Core.Settings;

namespace Shammy.Core.Notch;

/// <summary>Colour arithmetic for the island: parsing, mixing, WCAG luminance and contrast, hue turns.</summary>
public static class Colour
{
    public static Rgb Parse(string hex)
    {
        var h = hex.Trim().TrimStart('#');
        if (h.Length == 8) h = h[2..];
        if (h.Length != 6) throw new FormatException($"Not a colour: {hex}");
        return new(Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..], 16));
    }

    public static Rgb Mix(Rgb a, Rgb b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new((byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    /// <summary>WCAG relative luminance, 0 (black) to 1 (white).</summary>
    public static double Luminance(Rgb c) => 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);

    static double Lin(byte v)
    {
        double s = v / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    /// <summary>WCAG contrast ratio, 1 to 21.</summary>
    public static double Contrast(Rgb a, Rgb b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// <paramref name="c"/> lightened toward white just enough to reach <paramref name="min"/> contrast against a dark
    /// <paramref name="background"/> (or darkened toward black against a light one). Already enough: unchanged.
    /// </summary>
    public static Rgb AtLeast(Rgb c, Rgb background, double min)
    {
        if (Contrast(c, background) >= min) return c;
        var toward = Luminance(background) < 0.4 ? new Rgb(255, 255, 255) : new Rgb(0, 0, 0);
        double lo = 0, hi = 1;
        for (int i = 0; i < 16; i++)
        {
            double mid = (lo + hi) / 2;
            if (Contrast(Mix(c, toward, mid), background) >= min) hi = mid;
            else lo = mid;
        }
        return Mix(c, toward, hi);
    }

    public static (double H, double S, double L) ToHsl(Rgb c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min;
        if (d < 1e-9) return (0, 0, l);
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60, s, l);
    }

    public static Rgb FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360 / 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        if (s < 1e-9)
        {
            byte v = (byte)Math.Round(l * 255);
            return new(v, v, v);
        }
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
        }
        return new((byte)Math.Round(Hue(p, q, h + 1 / 3.0) * 255), (byte)Math.Round(Hue(p, q, h) * 255), (byte)Math.Round(Hue(p, q, h - 1 / 3.0) * 255));
    }

    /// <summary>Squared distance in RGB, weighted toward green like the eye.</summary>
    public static double Distance(Rgb a, Rgb b)
    {
        double dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return 2 * dr * dr + 4 * dg * dg + 3 * db * db;
    }
}

/// <summary>A palette: three colours for the island's waves, the first one also the app's accent.</summary>
public sealed record IslandPalette(string Id, string Name, Rgb A, Rgb B, Rgb C)
{
    public IReadOnlyList<Rgb> Colours => new[] { A, B, C };
}

/// <summary>The built-in palettes (Settings › Notch › Palette). Album, the default, takes its colours from the song playing.</summary>
public static class IslandPalettes
{
    public const string Album = "album", Monsoon = "monsoon", NeonDusk = "neon-dusk", Northern = "northern", Holo = "holo", Frost = "frost";

    /// <summary>The island itself: a near-black tint (drawn at ~88 % over the blurred wallpaper).</summary>
    public static readonly Rgb Island = new(0x05, 0x05, 0x07);

    /// <summary>The near-black text drawn on accent-coloured buttons (Tokens.GroundHex).</summary>
    public static readonly Rgb OnAccent = new(0x0B, 0x0B, 0x0D);

    /// <summary>Minimum contrast of the accent against the island and the dark text on it (the old accents ran 6.6–8.3).</summary>
    public const double AccentContrast = 6.5;

    /// <summary>Minimum contrast of an album colour against the island, so waves and the rim stay visible on dark covers.</summary>
    public const double AlbumContrast = 3.0;

    static IslandPalette P(string id, string name, string a, string b, string c) => new(id, name, Colour.Parse(a), Colour.Parse(b), Colour.Parse(c));

    public static readonly IslandPalette MonsoonPalette = P(Monsoon, "Monsoon", "#3B5BFF", "#7A3CFF", "#19C3B2");

    /// <summary>All palettes in the order the picker shows them. Album's own colours stand in until a song plays (Monsoon's).</summary>
    public static readonly IReadOnlyList<IslandPalette> All = new[]
    {
        MonsoonPalette with { Id = Album, Name = "Album" },
        MonsoonPalette,
        P(NeonDusk, "Neon dusk", "#FF3D7F", "#7B2CFF", "#2D7BFF"),
        P(Northern, "Northern", "#1BD68C", "#0E8BA8", "#7CF2D2"),
        P(Holo, "Holo", "#F2B6FF", "#9FD8FF", "#FFE3B0"),
        P(Frost, "Frost", "#D8DCE6", "#6E7486", "#AEB4C4"),
    };

    public static IslandPalette Find(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, Normalize(id), StringComparison.OrdinalIgnoreCase)) ?? All[0];

    /// <summary>A known palette id ("Neon dusk", "NEON-DUSK" and "neon_dusk" all mean neon-dusk); anything else is Album.</summary>
    public static string Normalize(string? id)
    {
        var key = (id ?? "").Trim().ToLowerInvariant().Replace(' ', '-').Replace('_', '-');
        return All.Any(p => p.Id == key) ? key : Album;
    }

    /// <summary>
    /// The palette for someone who picked an accent before palettes existed: the palette whose first colour is
    /// closest to it. Orange was the default (nobody chose it), so it means Album.
    /// </summary>
    public static string FromAccent(AccentColor accent)
    {
        if (accent == AccentColor.Orange || !Enum.IsDefined(accent)) return Album;
        var was = Colour.Parse(NotchLook.Colours(accent).Accent);
        return All.Where(p => p.Id != Album).MinBy(p => Colour.Distance(p.A, was))!.Id;
    }

    /// <summary>The saved palette, or (no palette saved yet) the one the old accent maps to.</summary>
    public static string Migrate(string? saved, AccentColor accent) =>
        string.IsNullOrWhiteSpace(saved) ? FromAccent(accent) : Normalize(saved);

    /// <summary>
    /// The accent and its hover shade from a palette's first colour: lifted just enough to read on the island and to
    /// carry the dark button text, the hover a touch lighter still.
    /// </summary>
    public static (Rgb Accent, Rgb Hover) Accent(Rgb first)
    {
        var accent = Colour.AtLeast(Colour.AtLeast(first, Island, AccentContrast), OnAccent, AccentContrast);
        var hover = Colour.Mix(accent, new Rgb(255, 255, 255), 0.2);
        return (accent, hover);
    }
}

/// <summary>Album, the default palette: the cover's three dominant colours, worked out once per track on a tiny copy.</summary>
public static class AlbumColours
{
    /// <summary>
    /// Three colours from a small BGRA thumbnail (24×24 is plenty), most dominant first. Pixels are binned coarsely and
    /// weighted by how much of the cover they cover and how colourful they are, so a vivid accent can beat a grey
    /// background without a speck of colour taking over. A cover with fewer than three distinct colours gets the
    /// missing ones as hue turns of the first. Each is lifted to <see cref="IslandPalettes.AlbumContrast"/> against the
    /// island. Null for an empty or fully transparent image.
    /// </summary>
    public static Rgb[]? Extract(ReadOnlySpan<byte> bgra)
    {
        const int Bits = 3, Levels = 1 << Bits, Shift = 8 - Bits;
        int bins = Levels * Levels * Levels;
        var weight = new double[bins];
        var sumR = new double[bins];
        var sumG = new double[bins];
        var sumB = new double[bins];
        int seen = 0;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2], a = bgra[i + 3];
            if (a < 128) continue;
            seen++;
            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double sat = max == 0 ? 0 : (max - min) / (double)max;
            double bright = max / 255.0;
            // Near-black and near-white say little about a cover's colour; colourful, bright-enough pixels say most.
            double w = 0.15 + sat * (0.4 + bright);
            if (max < 24) w *= 0.25;
            int bin = (r >> Shift) * Levels * Levels + (g >> Shift) * Levels + (b >> Shift);
            weight[bin] += w;
            sumR[bin] += r * w;
            sumG[bin] += g * w;
            sumB[bin] += b * w;
        }
        if (seen == 0) return null;

        var order = Enumerable.Range(0, bins).Where(i => weight[i] > 0).OrderByDescending(i => weight[i]).ToList();
        var picked = new List<Rgb>();
        // Distinct enough to read as a different band: roughly 45 levels apart in RGB.
        const double Apart = 45 * 45 * 9;
        foreach (int bin in order)
        {
            var c = new Rgb((byte)(sumR[bin] / weight[bin]), (byte)(sumG[bin] / weight[bin]), (byte)(sumB[bin] / weight[bin]));
            if (picked.All(p => Colour.Distance(p, c) >= Apart)) picked.Add(c);
            if (picked.Count == 3) break;
        }
        var (h, s, l) = Colour.ToHsl(picked[0]);
        double[] turns = { 38, -38 };
        for (int t = 0; picked.Count < 3; t++) picked.Add(Colour.FromHsl(h + turns[t % 2] * (1 + t / 2), Math.Max(s, 0.08), l));
        return picked.Select(c => Colour.AtLeast(c, IslandPalettes.Island, IslandPalettes.AlbumContrast)).ToArray();
    }
}
