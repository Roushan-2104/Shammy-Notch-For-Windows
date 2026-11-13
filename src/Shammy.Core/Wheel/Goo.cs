namespace Shammy.Core.Wheel;

public readonly record struct GooPoint(double X, double Y);

/// <summary>
/// The tapered neck between two blobs: start at <see cref="P1"/>, curve (<see cref="H1"/>, <see cref="H3"/>) to
/// <see cref="P3"/>, straight across inside the second blob to <see cref="P4"/>, curve back (<see cref="H4"/>,
/// <see cref="H2"/>) to <see cref="P2"/> and close inside the first. Filled together with both circles it reads as
/// one drop of liquid.
/// </summary>
public sealed record GooBridge(GooPoint P1, GooPoint H1, GooPoint H3, GooPoint P3, GooPoint P4, GooPoint H4, GooPoint H2, GooPoint P2);

/// <summary>
/// Cheap liquid geometry for the wheel: metaball-style necks between circles and the poured stream from the island,
/// computed per frame as plain points (no blur, no shader). Pure, so it is tested here and drawn by the app.
/// </summary>
public static class Goo
{
    /// <summary>
    /// The neck between circle 1 and circle 2, or null when they are too far apart to touch (more than
    /// r1 + r2 × <paramref name="reach"/> between centres) or one sits inside the other. <paramref name="v"/> is how
    /// wide the neck spreads (0 thin – 1 wide), <paramref name="handle"/> how round its curves are.
    /// </summary>
    public static GooBridge? Bridge(double x1, double y1, double r1, double x2, double y2, double r2, double v = 0.5, double handle = 2.4, double reach = 2.5)
    {
        double dx = x2 - x1, dy = y2 - y1, d = Math.Sqrt(dx * dx + dy * dy);
        if (r1 <= 0 || r2 <= 0 || d > r1 + r2 * reach || d <= Math.Abs(r1 - r2) || d < 1e-6) return null;

        double u1 = 0, u2 = 0;
        if (d < r1 + r2)
        {
            u1 = Math.Acos(Math.Clamp((r1 * r1 + d * d - r2 * r2) / (2 * r1 * d), -1, 1));
            u2 = Math.Acos(Math.Clamp((r2 * r2 + d * d - r1 * r1) / (2 * r2 * d), -1, 1));
        }
        double between = Math.Atan2(dy, dx);
        double spread = Math.Acos(Math.Clamp((r1 - r2) / d, -1, 1));

        double a1 = between + u1 + (spread - u1) * v;
        double a2 = between - u1 - (spread - u1) * v;
        double a3 = between + Math.PI - u2 - (Math.PI - u2 - spread) * v;
        double a4 = between - Math.PI + u2 + (Math.PI - u2 - spread) * v;

        var p1 = At(x1, y1, a1, r1);
        var p2 = At(x1, y1, a2, r1);
        var p3 = At(x2, y2, a3, r2);
        var p4 = At(x2, y2, a4, r2);

        double total = r1 + r2;
        double d2 = Math.Min(v * handle, Distance(p1, p3) / total) * Math.Min(1, d * 2 / total);
        double h1Len = r1 * d2, h2Len = r2 * d2;
        var h1 = At(p1.X, p1.Y, a1 - Math.PI / 2, h1Len);
        var h2 = At(p2.X, p2.Y, a2 + Math.PI / 2, h1Len);
        var h3 = At(p3.X, p3.Y, a3 + Math.PI / 2, h2Len);
        var h4 = At(p4.X, p4.Y, a4 - Math.PI / 2, h2Len);
        return new GooBridge(p1, h1, h3, p3, p4, h4, h2, p2);
    }

    /// <summary>
    /// The outline of the poured stream from the island (<paramref name="ix"/>, <paramref name="iy"/>) toward the
    /// pointer (<paramref name="px"/>, <paramref name="py"/>), between the fractions <paramref name="from"/> and
    /// <paramref name="to"/> of the way. It is <paramref name="thick"/> wide, flares to <paramref name="flare"/>
    /// where it leaves the island, wobbles with <paramref name="time"/> (seconds) by up to <paramref name="wobble"/>
    /// and carries a bulge flowing down it. Points go down one side and back up the other; empty when there is
    /// nothing to draw.
    /// </summary>
    public static IReadOnlyList<GooPoint> Stream(double ix, double iy, double px, double py, double from, double to,
        double thick, double flare, double time, double wobble = 1)
    {
        from = Math.Clamp(from, 0, 1);
        to = Math.Clamp(to, 0, 1);
        double dx = px - ix, dy = py - iy, length = Math.Sqrt(dx * dx + dy * dy);
        if (to - from <= 1e-4 || length < 1 || thick <= 0) return Array.Empty<GooPoint>();
        double ux = dx / length, uy = dy / length, nx = -uy, ny = ux;
        int samples = Math.Clamp((int)Math.Ceiling(length * (to - from) / 8), 2, 120);
        var left = new GooPoint[samples + 1];
        var right = new GooPoint[samples + 1];
        double flow = time * 1.6 % 1.0;
        for (int i = 0; i <= samples; i++)
        {
            double f = from + (to - from) * i / samples;
            // Sideways sway, none at either end so the stream stays attached to the island and the hub.
            double sway = wobble * 2.2 * Math.Sin(time * 5.1 + f * 11) * (4 * f * (1 - f));
            double cx = ix + dx * f + nx * sway, cy = iy + dy * f + ny * sway;
            double w = thick / 2 * (1 + 0.22 * wobble * Math.Sin(time * 7.3 - f * 17));
            w += Math.Max(0, flare - thick) / 2 * Math.Pow(1 - f, 6);
            w += thick * 0.45 * wobble * Math.Exp(-(f - flow) * (f - flow) / 0.004);
            left[i] = new GooPoint(cx + nx * w, cy + ny * w);
            right[i] = new GooPoint(cx - nx * w, cy - ny * w);
        }
        var outline = new List<GooPoint>(left.Length * 2);
        outline.AddRange(left);
        for (int i = right.Length - 1; i >= 0; i--) outline.Add(right[i]);
        return outline;
    }

    /// <summary>Gravity's ease for a pour or a drain: slow to start, then quicker (0..1 → 0..1).</summary>
    public static double EaseIn(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (1.6 - 0.6 * t);
    }

    static GooPoint At(double x, double y, double angle, double r) => new(x + Math.Cos(angle) * r, y + Math.Sin(angle) * r);

    static double Distance(GooPoint a, GooPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}

/// <summary>The wheel's turning click: a few milliseconds of a damped tick, rendered once as 16-bit mono PCM.</summary>
public static class DialTick
{
    public const int SampleRate = 44100;

    public static short[] Render(double loudness = 0.22)
    {
        int n = SampleRate * 22 / 1000;
        var samples = new short[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / SampleRate;
            double s = 0.65 * Math.Sin(2 * Math.PI * 2600 * t) * Math.Exp(-t / 0.0018)
                     + 0.35 * Math.Sin(2 * Math.PI * 950 * t) * Math.Exp(-t / 0.0045);
            // A 1 ms fade-in so it starts without a pop.
            s *= Math.Min(1, t / 0.001);
            samples[i] = (short)Math.Round(Math.Clamp(s * loudness, -1, 1) * short.MaxValue);
        }
        return samples;
    }
}
