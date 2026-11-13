using Shammy.Core.Prompter;

namespace Shammy.Core.Stage;

/// <summary>
/// Where the reader is in a Stage script and how fast it moves on. Time only counts while the reader is speaking
/// (or always, at a steady pace, when there's no microphone): each line takes as long as its words take to say at
/// the reading rate, about 150 words a minute at the Normal pace, and then the next line becomes the current one.
/// Positions are kept as a word count too, so the script can be wrapped again (a new text size) without losing
/// the place.
/// </summary>
public sealed class StageReader
{
    /// <summary>The reading rate at the Normal pace (words a minute); Slow and Fast scale it like the old teleprompter.</summary>
    public const double SpeakingRate = 150;

    /// <summary>No line goes by faster than this, so a one-word line is still seen (seconds).</summary>
    public const double ShortestLine = 1.2;

    IReadOnlyList<string> _lines = Array.Empty<string>();
    int[] _words = Array.Empty<int>();
    int[] _before = Array.Empty<int>();

    public StageReader(IReadOnlyList<string> lines, PrompterPace pace = PrompterPace.Normal)
    {
        Pace = pace;
        SetLines(lines);
    }

    public IReadOnlyList<string> Lines => _lines;
    public PrompterPace Pace { get; set; }
    public int Index { get; private set; }

    /// <summary>Seconds spent on the current line so far.</summary>
    public double Into { get; private set; }

    /// <summary>Stopped by the reader (Space); a quiet microphone only holds, it doesn't pause.</summary>
    public bool Paused { get; set; }

    public int TotalWords { get; private set; }

    public string Previous => Index > 0 && Index - 1 < _lines.Count ? _lines[Index - 1] : "";
    public string Current => Index < _lines.Count ? _lines[Index] : "";
    public string Next => Index + 1 < _lines.Count ? _lines[Index + 1] : "";

    /// <summary>The last line has had its time.</summary>
    public bool Done => _lines.Count == 0 || (Index >= _lines.Count - 1 && Into >= LineSeconds(Index));

    /// <summary>Words a minute at a pace: 150 at Normal, about 107 at Slow and 193 at Fast.</summary>
    public static double WordsPerMinute(PrompterPace pace) => SpeakingRate * PrompterText.WordsPerMinute(pace) / PrompterText.WordsPerMinute(PrompterPace.Normal);

    /// <summary>How long a line of <paramref name="words"/> words stays current.</summary>
    public static double Seconds(int words, PrompterPace pace) => Math.Max(ShortestLine, words * 60.0 / WordsPerMinute(pace));

    public double LineSeconds(int index) => index >= 0 && index < _words.Length ? Seconds(_words[index], Pace) : ShortestLine;

    /// <summary>The first word of the current line, counted from the start of the script.</summary>
    public int WordIndex => Index < _before.Length ? _before[Index] : TotalWords;

    /// <summary>How far through the script, by words (0–100), counting the part of the current line already said.</summary>
    public double Percent
    {
        get
        {
            if (TotalWords == 0) return 0;
            double within = Index < _words.Length ? _words[Index] * Math.Clamp(Into / LineSeconds(Index), 0, 1) : 0;
            return Math.Clamp((WordIndex + within) / TotalWords * 100, 0, 100);
        }
    }

    /// <summary>
    /// Lets <paramref name="seconds"/> pass. Time counts only while <paramref name="speaking"/> and not paused.
    /// Returns how many lines it moved on (usually 0 or 1).
    /// </summary>
    public int Advance(double seconds, bool speaking)
    {
        if (Paused || !speaking || seconds <= 0 || _lines.Count == 0 || Done) return 0;
        Into += seconds;
        int moved = 0;
        while (Index < _lines.Count - 1 && Into >= LineSeconds(Index))
        {
            Into -= LineSeconds(Index);
            Index++;
            moved++;
        }
        if (Index == _lines.Count - 1) Into = Math.Min(Into, LineSeconds(Index));
        return moved;
    }

    /// <summary>One line back or on (arrows); the new line starts from its beginning. Returns the lines moved.</summary>
    public int Step(int lines)
    {
        int was = Index;
        Index = Math.Clamp(Index + lines, 0, Math.Max(0, _lines.Count - 1));
        Into = 0;
        return Index - was;
    }

    /// <summary>To a point in the script (0–100 %, by words).</summary>
    public void Seek(double percent)
    {
        int word = (int)Math.Round(TotalWords * Math.Clamp(percent, 0, 100) / 100);
        Index = LineOfWord(word);
        Into = 0;
    }

    public void Restart()
    {
        Index = 0;
        Into = 0;
        Paused = false;
    }

    /// <summary>New lines for the same script (wrapped at another width): the current line becomes the one holding its first word.</summary>
    public void SetLines(IReadOnlyList<string> lines)
    {
        int word = _lines.Count == 0 ? 0 : WordIndex;
        _lines = lines;
        _words = lines.Select(StageLines.Words).ToArray();
        _before = new int[_words.Length];
        int sum = 0;
        for (int i = 0; i < _words.Length; i++)
        {
            _before[i] = sum;
            sum += _words[i];
        }
        TotalWords = sum;
        Index = LineOfWord(word);
        Into = 0;
    }

    /// <summary>The line a word (counted from 0) is on.</summary>
    public int LineOfWord(int word)
    {
        if (_lines.Count == 0) return 0;
        for (int i = _before.Length - 1; i >= 0; i--)
            if (_before[i] <= word) return i;
        return 0;
    }
}
