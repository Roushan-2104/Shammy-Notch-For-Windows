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
/// Cheap liquid geometry for the wheel: metaball-style necks between the hub and its droplets, computed per frame as
/// plain points (no blur, no shader). Pure, so it is tested here and drawn by the app.
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
