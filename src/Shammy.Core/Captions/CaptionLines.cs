namespace Shammy.Core.Captions;

/// <summary>The two lines on screen right now; either may be empty.</summary>
public sealed record CaptionView(IReadOnlyList<CaptionWord> Upper, IReadOnlyList<CaptionWord> Lower)
{
    public static readonly CaptionView Empty = new(Array.Empty<CaptionWord>(), Array.Empty<CaptionWord>());

    public bool IsEmpty => Upper.Count == 0 && Lower.Count == 0;
}

/// <summary>
/// Keeps a running caption to two lines, the way broadcast captions roll: words fill the upper line, then the
/// lower; when the lower line is full it moves up, the oldest line leaves, and new words start a fresh lower
/// line. A word never moves sideways once it is shown, so the eye can stay on the words still arriving.
/// </summary>
public sealed class CaptionLines
{
    readonly double _width;
    readonly Func<string, double> _measure;
    long _top;

    /// <param name="width">How wide a line may be, in whatever unit <paramref name="measure"/> answers in.</param>
    /// <param name="measure">The width of a piece of text in the caption's font.</param>
    public CaptionLines(double width, Func<string, double> measure)
    {
        _width = width;
        _measure = measure;
    }

    /// <param name="dropped">The merger's <see cref="CaptionMerger.Dropped"/>, so the lines stay put when it trims.</param>
    public CaptionView Layout(IReadOnlyList<CaptionWord> words, long dropped = 0)
    {
        int start = (int)Math.Clamp(_top - dropped, 0, words.Count);
        while (true)
        {
            int lower = Fill(words, start);
            int end = Fill(words, lower);
            if (end >= words.Count)
            {
                _top = start + dropped;
                return new CaptionView(Slice(words, start, lower), Slice(words, lower, words.Count));
            }
            // The lower line is full: it becomes the upper one.
            start = lower;
        }
    }

    /// <summary>The first word that does not fit on a line starting at <paramref name="from"/> (a line always takes one word).</summary>
    int Fill(IReadOnlyList<CaptionWord> words, int from)
    {
        if (from >= words.Count) return words.Count;
        var line = words[from].Text;
        int i = from + 1;
        for (; i < words.Count; i++)
        {
            var longer = line + " " + words[i].Text;
            if (_measure(longer) > _width) break;
            line = longer;
        }
        return i;
    }

    static IReadOnlyList<CaptionWord> Slice(IReadOnlyList<CaptionWord> words, int from, int to)
    {
        var slice = new CaptionWord[to - from];
        for (int i = from; i < to; i++) slice[i - from] = words[i];
        return slice;
    }
}
