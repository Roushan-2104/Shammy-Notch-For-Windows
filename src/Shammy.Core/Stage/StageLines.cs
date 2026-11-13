namespace Shammy.Core.Stage;

/// <summary>
/// Stage's script as short reading lines: each paragraph is wrapped at word boundaries into lines of at most
/// <c>maxChars</c> characters (about 40–50 reads most comfortably), a paragraph's last line is evened out with the
/// one before it when it would be left with a word or two, and a word too long for a line is split. Blank lines
/// between paragraphs are dropped (the glide between lines is pause enough).
/// </summary>
public static class StageLines
{
    public const int DefaultChars = 46, MinChars = 16, MaxChars = 60;

    /// <summary>A paragraph's last line shorter than this share of a line borrows words from the line before.</summary>
    const double OrphanShare = 0.34;

    public static IReadOnlyList<string> Wrap(string? script, int maxChars = DefaultChars)
    {
        maxChars = Math.Clamp(maxChars, MinChars, MaxChars);
        var lines = new List<string>();
        var text = (script ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ');
        foreach (var paragraph in text.Split('\n'))
        {
            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) continue;
            lines.AddRange(WrapParagraph(words, maxChars));
        }
        return lines;
    }

    static List<string> WrapParagraph(string[] words, int maxChars)
    {
        var lines = new List<List<string>>();
        var line = new List<string>();
        int length = 0;
        foreach (var raw in words)
        {
            foreach (var word in Split(raw, maxChars))
            {
                int needed = line.Count == 0 ? word.Length : length + 1 + word.Length;
                if (line.Count > 0 && needed > maxChars)
                {
                    lines.Add(line);
                    line = new List<string>();
                    needed = word.Length;
                }
                line.Add(word);
                length = needed;
            }
        }
        if (line.Count > 0) lines.Add(line);
        Balance(lines, maxChars);
        return lines.Select(l => string.Join(' ', l)).ToList();
    }

    /// <summary>Moves words from the next-to-last line down while that makes the two closer in length.</summary>
    static void Balance(List<List<string>> lines, int maxChars)
    {
        if (lines.Count < 2) return;
        var last = lines[^1];
        var before = lines[^2];
        if (Length(last) >= maxChars * OrphanShare) return;
        while (before.Count > 1)
        {
            var word = before[^1];
            // Only while it brings the two closer: the gap must be wider than what moves.
            if (Length(last) + word.Length + 1 > maxChars || Length(before) - Length(last) <= word.Length + 1) break;
            before.RemoveAt(before.Count - 1);
            last.Insert(0, word);
        }
    }

    static int Length(List<string> line) => line.Sum(w => w.Length) + Math.Max(0, line.Count - 1);

    /// <summary>A word longer than a line, cut into line-sized pieces (a URL, a long compound).</summary>
    static IEnumerable<string> Split(string word, int maxChars)
    {
        for (int i = 0; i < word.Length; i += maxChars) yield return word.Substring(i, Math.Min(maxChars, word.Length - i));
    }

    public static int Words(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// How many characters fit on a line <paramref name="width"/> wide when an average character is
    /// <paramref name="charWidth"/> wide, kept within the comfortable 40–50 where the width allows it.
    /// </summary>
    public static int CharsFor(double width, double charWidth, int comfortable = 48)
    {
        if (width <= 0 || charWidth <= 0) return DefaultChars;
        int fits = (int)Math.Floor(width / charWidth);
        return Math.Clamp(Math.Min(fits, comfortable), MinChars, MaxChars);
    }
}
