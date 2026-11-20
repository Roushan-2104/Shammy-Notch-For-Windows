using System.Text.Json;


namespace Shammy.Core.Reports;

/// <summary>A report ready to offer: everything that would be sent, already cleaned, except the user's note.</summary>
public sealed record Report(
    string Kind,
    string Signature,
    string Version,
    string Error,
    string Area,
    string Where,
    string Windows,
    DateTime WhenUtc,
    int Times,
    string? Healing,
    IReadOnlyList<string> Stack,
    IReadOnlyList<string> Log)
{
    public const string CrashKind = "crash", ErrorKind = "error";

    public bool IsCrash => Kind == CrashKind;

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The body the relay takes (relay/src/report.js parseReport); the note is cleaned like the rest.</summary>
    public string ToJson(string? note, IEnumerable<string>? privateWords = null) => JsonSerializer.Serialize(new
    {
        kind = Kind,
        signature = Signature,
        version = Version,
        error = Error,
        area = Area,
        where = Where,
        windows = Windows,
        when = WhenUtc.ToString("o"),
        times = Times,
        healing = Healing ?? "",
        note = ReportText.Clean((note ?? "").Trim(), privateWords),
        stack = Stack,
        log = Log,
    }, Json);
}

/// <summary>A feature safe mode switched off: it stays off until another version runs, or the user turns it back on.</summary>
public sealed record PausedFeature(string Name, string Version, DateTime SinceUtc);

public sealed record CrashMark(string Feature, DateOnly Day);

public sealed class ErrorTally
{
    public DateOnly Day { get; set; }
    public int Count { get; set; }
}

/// <summary>
/// What Shammy remembers between runs about problems: a crash report waiting to be offered, today's count of each
/// repeating error, the answers given, and safe mode's crash counts and paused features. Plain data plus the rules
/// that change it, so the rules are tested without a PC to crash.
/// </summary>
public sealed class ReportsState
{
    /// <summary>A crash found on start that hasn't been offered (or answered) yet.</summary>
    public Report? PendingCrash { get; set; }

    public Dictionary<string, ErrorTally> Errors { get; set; } = new();

    /// <summary>Repeating errors the user answered (sent or not), with the version they answered on.</summary>
    public Dictionary<string, string> Answered { get; set; } = new();

    public List<CrashMark> Crashes { get; set; } = new();

    public Dictionary<string, PausedFeature> Paused { get; set; } = new();

    /// <summary>Features paused since the user last saw the safe-mode card.</summary>
    public List<string> PauseNotices { get; set; } = new();

    public bool IsPaused(string featureKey) => Paused.ContainsKey(featureKey);

    /// <summary>
    /// One more of this error today; true exactly when it reaches <see cref="ProblemRules.RepeatsPerDay"/> and the user
    /// hasn't already answered it on this version (they're asked again only if it outlives an update).
    /// </summary>
    public bool CountError(string signature, DateOnly day, string version)
    {
        if (!Errors.TryGetValue(signature, out var tally) || tally.Day != day) Errors[signature] = tally = new ErrorTally { Day = day };
        tally.Count++;
        if (Answered.TryGetValue(signature, out var answeredOn) && answeredOn == version) return false;
        return tally.Count == ProblemRules.RepeatsPerDay;
    }

    public int TodayCount(string signature, DateOnly day) => Errors.TryGetValue(signature, out var t) && t.Day == day ? t.Count : 0;

    public void Answer(string signature, string version) => Answered[signature] = version;

    /// <summary>
    /// A crash in this feature on this day; true when that makes <see cref="ProblemRules.CrashesPerDay"/> and the feature
    /// is paused now (it's also queued for the safe-mode card).
    /// </summary>
    public bool RecordCrash(string featureKey, string featureName, DateOnly day, string version, DateTime nowUtc)
    {
        Crashes.Add(new CrashMark(featureKey, day));
        if (Paused.ContainsKey(featureKey)) return false;
        if (Crashes.Count(c => c.Feature == featureKey && c.Day == day) < ProblemRules.CrashesPerDay) return false;
        Paused[featureKey] = new PausedFeature(featureName, version, nowUtc);
        if (!PauseNotices.Contains(featureKey)) PauseNotices.Add(featureKey);
        return true;
    }

    /// <summary>The user turned a paused feature back on: its crash count starts over, so one more crash doesn't re-pause it.</summary>
    public void Resume(string featureKey)
    {
        Paused.Remove(featureKey);
        PauseNotices.Remove(featureKey);
        Crashes.RemoveAll(c => c.Feature == featureKey);
    }

    /// <summary>A new version runs: everything paused on an older one comes back (the update may well have fixed it).</summary>
    public IReadOnlyList<string> ResumeAfterUpdate(string version)
    {
        var back = Paused.Where(p => p.Value.Version != version).Select(p => p.Key).ToList();
        foreach (var key in back) Resume(key);
        return back;
    }

    /// <summary>Forgets counts from before today and answers and reports that no longer matter.</summary>
    public void Prune(DateOnly today, DateTime nowUtc)
    {
        foreach (var key in Errors.Where(e => e.Value.Day < today).Select(e => e.Key).ToList()) Errors.Remove(key);
        Crashes.RemoveAll(c => c.Day < today.AddDays(-1));
        if (PendingCrash is { } crash && nowUtc - crash.WhenUtc > TimeSpan.FromDays(7)) PendingCrash = null;
        // Answers only matter for the version they were given on; keep a few in case of a quick downgrade.
        while (Answered.Count > 200) Answered.Remove(Answered.Keys.First());
    }
}

/// <summary>Picks out the log lines that belong in a report.</summary>
public static class LogTail
{
    /// <summary>The line Shammy writes first on every start (App.OnStartup).</summary>
    public static bool IsStart(string line) => line.Contains("] Shammy ", StringComparison.Ordinal) && line.Contains(" starting on ", StringComparison.Ordinal);

    /// <summary>
    /// The last <paramref name="count"/> lines of the run before this one (what led up to a crash), from the log lines
    /// in order with this run's start line among them. Empty when that run isn't in the log any more.
    /// </summary>
    public static IReadOnlyList<string> PreviousRun(IReadOnlyList<string> lines, int count)
    {
        int current = -1, previous = -1;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (!IsStart(lines[i])) continue;
            if (current < 0) current = i;
            else
            {
                previous = i;
                break;
            }
        }
        if (current <= 0) return Array.Empty<string>();
        int from = Math.Max(previous < 0 ? 0 : previous, current - count);
        return lines.Skip(from).Take(current - from).ToList();
    }

    public static IReadOnlyList<string> Last(IReadOnlyList<string> lines, int count) => lines.Skip(Math.Max(0, lines.Count - count)).ToList();
}
