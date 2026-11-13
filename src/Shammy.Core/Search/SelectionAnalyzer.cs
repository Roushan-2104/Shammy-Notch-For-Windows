namespace Shammy.Core.Search;

public readonly record struct Pt(double X, double Y);

public readonly record struct Box(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public double Area => Math.Max(0, W) * Math.Max(0, H);
    public Pt Center => new(X + W / 2, Y + H / 2);

    public bool Contains(Pt p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;

    public Box Inflate(double d) => new(X - d, Y - d, W + 2 * d, H + 2 * d);

    public Box Union(Box o)
    {
        double x = Math.Min(X, o.X), y = Math.Min(Y, o.Y);
        return new Box(x, y, Math.Max(Right, o.Right) - x, Math.Max(Bottom, o.Bottom) - y);
    }

    public Box Clamp(double width, double height)
    {
        double x = Math.Clamp(X, 0, width), y = Math.Clamp(Y, 0, height);
        return new Box(x, y, Math.Clamp(Right, 0, width) - x, Math.Clamp(Bottom, 0, height) - y);
    }
}

public sealed record OcrWord(string Text, Box Bounds, int Line);

public enum SelectionKind { Text, Image }

public sealed record Selection(SelectionKind Kind, string Text, Box Region);

/// <summary>
/// Decides what a circle, scribble or tap on the frozen screen meant. A closed loop selects the words
/// inside it; an open stroke works like a highlighter over the words it passes; a tap picks the line under
/// the cursor. Mostly-text selections become a text query, anything else goes to Lens as an image.
/// </summary>
public static class SelectionAnalyzer
{
    const double TapTolerance = 10;
    const double TapRegion = 360;

    public static Selection Analyze(IReadOnlyList<OcrWord> words, IReadOnlyList<Pt> stroke)
    {
        if (stroke.Count == 0) throw new ArgumentException("Empty stroke", nameof(stroke));

        var bounds = Bounds(stroke);
        if (bounds.W <= TapTolerance && bounds.H <= TapTolerance) return Tap(words, stroke[0]);

        double polygonArea = Math.Abs(SignedArea(stroke));
        bool closed = bounds.Area > 0 && polygonArea >= 0.2 * bounds.Area && EndsMeet(stroke, bounds);

        List<OcrWord> selected = closed
            ? words.Where(w => Contains(stroke, w.Bounds.Center)).ToList()
            : words.Where(w => DistanceToPolyline(stroke, w.Bounds.Center) <= Math.Max(w.Bounds.H * 0.9, 12)).ToList();

        var text = JoinLines(selected);
        if (!closed)
        {
            return selected.Count > 0
                ? new Selection(SelectionKind.Text, text, UnionOf(selected).Inflate(6))
                : new Selection(SelectionKind.Image, "", bounds.Inflate(24));
        }

        double coverage = bounds.Area > 0 ? selected.Sum(w => w.Bounds.Area) / bounds.Area : 0;
        bool mostlyText = (selected.Count >= 3 && coverage >= 0.12) || selected.Count >= 12;
        return new Selection(mostlyText ? SelectionKind.Text : SelectionKind.Image, text, bounds);
    }

    static Selection Tap(IReadOnlyList<OcrWord> words, Pt p)
    {
        var hit = words.FirstOrDefault(w => w.Bounds.Inflate(4).Contains(p));
        if (hit is not null)
        {
            var line = words.Where(w => w.Line == hit.Line).ToList();
            return new Selection(SelectionKind.Text, JoinLines(line), UnionOf(line).Inflate(6));
        }
        return new Selection(SelectionKind.Image, "", new Box(p.X - TapRegion / 2, p.Y - TapRegion / 2, TapRegion, TapRegion));
    }

    public static Box Bounds(IReadOnlyList<Pt> pts)
    {
        double minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y);
        return new Box(minX, minY, pts.Max(p => p.X) - minX, pts.Max(p => p.Y) - minY);
    }

    public static bool Contains(IReadOnlyList<Pt> poly, Pt p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    static double SignedArea(IReadOnlyList<Pt> poly)
    {
        double sum = 0;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            sum += (poly[j].X * poly[i].Y) - (poly[i].X * poly[j].Y);
        return sum / 2;
    }

    /// <summary>A loop counts as closed when its ends finish near each other relative to its size.</summary>
    static bool EndsMeet(IReadOnlyList<Pt> stroke, Box bounds)
    {
        var a = stroke[0];
        var b = stroke[^1];
        double gap = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        return gap <= 0.45 * Math.Max(bounds.W, bounds.H);
    }

    static double DistanceToPolyline(IReadOnlyList<Pt> line, Pt p)
    {
        if (line.Count == 1) return Distance(line[0], p);
        double best = double.MaxValue;
        for (int i = 1; i < line.Count; i++) best = Math.Min(best, DistanceToSegment(line[i - 1], line[i], p));
        return best;
    }

    static double DistanceToSegment(Pt a, Pt b, Pt p)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        if (len2 == 0) return Distance(a, p);
        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        return Distance(new Pt(a.X + t * dx, a.Y + t * dy), p);
    }

    static double Distance(Pt a, Pt b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    static Box UnionOf(IEnumerable<OcrWord> words) => words.Select(w => w.Bounds).Aggregate((a, b) => a.Union(b));

    /// <summary>
    /// The words in reading order: OCR's lines in its order, each left to right. OCR sometimes splits one row of text into
    /// two "lines" (a "+91" a little apart from the number after it); lines that sit on the same row, close together, are
    /// read as one, so the words never come out of order ("98765 43210 +91").
    /// </summary>
    public static string JoinLines(IEnumerable<OcrWord> words)
    {
        var rows = words.GroupBy(w => w.Line).Select(g => (Order: g.Key, Words: g.ToList())).ToList();
        for (bool merged = true; merged;)
        {
            merged = false;
            for (int i = 0; i < rows.Count && !merged; i++)
            for (int j = i + 1; j < rows.Count && !merged; j++)
            {
                if (!SameRow(UnionOf(rows[i].Words), UnionOf(rows[j].Words))) continue;
                rows[i] = (Math.Min(rows[i].Order, rows[j].Order), rows[i].Words.Concat(rows[j].Words).ToList());
                rows.RemoveAt(j);
                merged = true;
            }
        }
        return string.Join(' ', rows.OrderBy(r => r.Order).Select(r => string.Join(' ', r.Words.OrderBy(w => w.Bounds.X).Select(w => w.Text))));
    }

    /// <summary>Mostly the same height band, and no wider apart than a few line heights (a second column stays separate).</summary>
    static bool SameRow(Box a, Box b)
    {
        double overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
        if (overlap < 0.6 * Math.Min(a.H, b.H)) return false;
        double gap = Math.Max(b.X - a.Right, a.X - b.Right);
        return gap <= 3 * Math.Max(a.H, b.H);
    }
}
