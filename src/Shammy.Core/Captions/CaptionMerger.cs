namespace Shammy.Core.Captions;

/// <param name="Changed">Something on screen is different: new words, or an old one gained its comma.</param>
/// <param name="Added">How many words are new (they are the bright ones).</param>
public readonly record struct CaptionMerge(bool Changed, int Added);

/// <summary>
/// Joins what the voice model heard in each rolling window into one running caption. Windows overlap, so each
/// hearing mostly repeats the end of the last one and then goes a little further. The two are lined up word by
/// word (case and punctuation ignored, a misheard word allowed), and only what lies past the overlap is added.
/// The last few words on screen were heard with nothing after them, so the newer hearing may reword them;
/// everything older is settled and never changes, which is what keeps the caption from flickering.
/// </summary>
public sealed class CaptionMerger
{
    /// <summary>How many words back a hearing is lined up against: more than fits in one window.</summary>
    const int Tail = 48;

    /// <summary>The words at the end that a newer hearing may still reword.</summary>
    public const int Tentative = 4;

    const int MaxHeard = 80, Keep = 160, TrimAt = 220;
    const int Exact = 2, Close = 1, Miss = -1, Gap = -1;

    readonly List<CaptionWord> _words = new();

    /// <summary>The caption so far, oldest first (only the last couple of hundred words are kept).</summary>
    public IReadOnlyList<CaptionWord> Words => _words;

    /// <summary>How many words have been forgotten from the front, so a position can outlive the trimming.</summary>
    public long Dropped { get; private set; }

    /// <summary>The merge that last added words; words carrying this number are the newest.</summary>
    public int Batch { get; private set; }

    public string Text => string.Join(' ', _words.Select(w => w.Text));

    /// <param name="heard">The model's transcript of the newest window.</param>
    /// <param name="overlaps">False when the window holds only sound that was never transcribed before (speech
    /// after a pause): its words are simply added.</param>
    public CaptionMerge Merge(string? heard, bool overlaps = true)
    {
        var b = CaptionText.Words(heard);
        if (b.Count == 0 || CaptionText.IsInvented(heard)) return default;
        if (b.Count > MaxHeard) b.RemoveRange(0, b.Count - MaxHeard);
        if (!overlaps || _words.Count == 0) return Append(b);

        int n = Math.Min(Tail, _words.Count), m = b.Count, first = _words.Count - n;
        int Pair(int i, int j)
        {
            string x = _words[first + i].Key, y = b[j].Key;
            return x == y ? Exact : CaptionText.Similar(x, y) ? Close : Miss;
        }

        // The end of the old words against the start of the new ones. Starting anywhere in the old words is free
        // and so is stopping anywhere in the new; skipping the new hearing's first words or the old one's last
        // words costs, so a phrase said twice lines up with its latest telling.
        var s = new int[n + 1, m + 1];
        for (int j = 1; j <= m; j++) s[0, j] = j * Gap;
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
                s[i, j] = Math.Max(s[i - 1, j - 1] + Pair(i - 1, j - 1), Math.Max(s[i - 1, j] + Gap, s[i, j - 1] + Gap));
        int end = 0;
        for (int j = 1; j <= m; j++)
            if (s[n, j] > s[n, end]) end = j;

        // Walk the best line-up backwards: which new word is which old word, and where the tentative words begin.
        int zone = Math.Max(0, n - Tentative);
        var sameAs = new int[m];
        Array.Fill(sameAs, -1);
        int same = 0, lastOld = -1, lastNew = -1;
        (int Old, int New) cut = (n, end);
        int a = n, c = end;
        while (a > 0 && c > 0)
        {
            int pair = Pair(a - 1, c - 1);
            if (s[a, c] == s[a - 1, c - 1] + pair)
            {
                a--;
                c--;
                if (pair == Exact)
                {
                    sameAs[c] = a;
                    same++;
                    if (lastOld < 0) (lastOld, lastNew) = (a, c);
                }
            }
            else if (s[a, c] == s[a - 1, c] + Gap) a--;
            else c--;
            if (a >= zone) cut = (a, c);
        }

        // One shared word is only believed where the two meet (the old caption's end, the new hearing's start):
        // anywhere else it is a chance "the", and the hearing is new speech.
        bool meets = lastOld >= n - 2 && lastNew <= 1;
        if (same == 0 || (same == 1 && !meets)) return Append(b);

        int keep = first + cut.Old, removed = _words.Count - keep, added = m - cut.New;
        // A hearing that stops short of what is already shown has nothing to add; words are never taken away.
        if (added < removed) return default;

        var tail = new List<CaptionWord>(added);
        int batch = Batch + 1, fresh = 0;
        for (int j = cut.New; j < m; j++)
        {
            if (sameAs[j] >= 0)
            {
                var old = _words[first + sameAs[j]];
                // The model capitalises whatever word its window happens to start on: keep the old spelling there.
                tail.Add(j == 0 ? old : old with { Text = b[j].Text });
            }
            else
            {
                tail.Add(new CaptionWord(b[j].Text, b[j].Key, batch));
                fresh++;
            }
        }
        bool changed = fresh > 0 || removed != added;
        for (int k = 0; !changed && k < added; k++) changed = tail[k].Text != _words[keep + k].Text;
        if (!changed) return default;

        _words.RemoveRange(keep, removed);
        _words.AddRange(tail);
        if (fresh > 0) Batch = batch;
        Trim();
        return new CaptionMerge(true, fresh);
    }

    CaptionMerge Append(List<(string Text, string Key)> heard)
    {
        Batch++;
        foreach (var (text, key) in heard) _words.Add(new CaptionWord(text, key, Batch));
        Trim();
        return new CaptionMerge(true, heard.Count);
    }

    void Trim()
    {
        if (_words.Count <= TrimAt) return;
        int drop = _words.Count - Keep;
        _words.RemoveRange(0, drop);
        Dropped += drop;
    }
}
