using System.Text.Json.Serialization;

namespace Shammy.Core.CircleMemory;

/// <summary>
/// One remembered circle: the words read from the circled region, the answer the island gave (its title and its
/// answer or body line), what kind of thing it was, the app it was circled in, and when. The small picture of the
/// region is kept beside it (see <see cref="HasPicture"/>), never inside it, so the list stays small in memory.
/// </summary>
public sealed class RememberedCircle
{
    /// <summary>A short random id; also names the picture file.</summary>
    public string Id { get; set; } = NewId();

    /// <summary>The text read from the circled region ("明日の会議は…", "6E 2134"); empty for a plain picture.</summary>
    public string Words { get; set; } = "";

    /// <summary>The island card's big line ("Tomorrow's meeting starts at 3 PM", "6E 2134 · IndiGo").</summary>
    public string Title { get; set; } = "";

    /// <summary>The island card's answer or body line ("4:30 AM your time, Sun 15 Nov"); null when it had none.</summary>
    public string? Line { get; set; }

    /// <summary>
    /// What it was: a <c>CircleKind</c> name ("When", "ZoneTime", "Flight", "Foreign"…) or one of
    /// <see cref="CircleKinds.Picture"/>, <see cref="CircleKinds.Translation"/>, <see cref="CircleKinds.Product"/>,
    /// <see cref="CircleKinds.Question"/>; "Text" when nothing more is known.
    /// </summary>
    public string Kind { get; set; } = CircleKinds.Text;

    /// <summary>The friendly name of the app in front when it was circled ("WhatsApp", "Chrome"); null if unknown.</summary>
    public string? App { get; set; }

    /// <summary>When it was circled, in local time.</summary>
    public DateTime At { get; set; }

    /// <summary>Whether a small picture of the region was kept.</summary>
    public bool HasPicture { get; set; }

    /// <summary>The row's bold line: what was circled ("6E 2134", "3 PM PST"), or the title for a plain picture.</summary>
    [JsonIgnore]
    public string What => OneLine(Words.Trim().Length > 0 ? Words : Title);

    /// <summary>The row's quiet line: the answer it got ("IndiGo flight", "4:30 AM · your time, Sun 15 Nov").</summary>
    [JsonIgnore]
    public string Answer
    {
        get
        {
            var line = (Line ?? "").Trim();
            if (Words.Trim().Length == 0 || TitleIsWords) return OneLine(line.Length > 0 ? line : Title);
            return OneLine(line.Length > 0 ? $"{Title.Trim()} · {line}" : Title);
        }
    }

    /// <summary>The title only repeats the circled words (a flight number, a translation's original).</summary>
    bool TitleIsWords
    {
        get
        {
            string t = Title.Trim(), w = Words.Trim();
            if (t.Length == 0) return true;
            if (t.EndsWith('…')) t = t[..^1];
            return w.Contains(t, StringComparison.OrdinalIgnoreCase) || t.Contains(w, StringComparison.OrdinalIgnoreCase);
        }
    }

    static string OneLine(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// The answer in words, for Copy and for a drag: the title and the answer line, once each; for a translation only
    /// the translation (the original is what was circled).
    /// </summary>
    [JsonIgnore]
    public string AnswerText
    {
        get
        {
            var title = Title.Trim();
            var line = (Line ?? "").Trim();
            if (line.Length == 0) return title;
            if (CircleKinds.GroupOf(Kind) == CircleGroup.Translations && Words.Trim().Length > 0 && TitleIsWords) return line;
            if (title.Length == 0 || line.Contains(title, StringComparison.OrdinalIgnoreCase)) return line;
            if (title.Contains(line, StringComparison.OrdinalIgnoreCase)) return title;
            return $"{title}\n{line}";
        }
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];
}

/// <summary>What the circle memory keeps on disk: the circles (newest first) and whether remembering is paused.</summary>
public sealed class CircleMemoryData
{
    public bool Paused { get; set; }
    public List<RememberedCircle> Circles { get; set; } = new();
}
