namespace Shammy.Core.Mixer;

/// <summary>One audio session as Windows reports it, with what is known about the process behind it.</summary>
public sealed record MixerSource
{
    public uint ProcessId { get; init; }

    /// <summary>The exe's name without ".exe"; null when the process couldn't be read.</summary>
    public string? ProcessName { get; init; }

    public string? Description { get; init; }
    public string? WindowTitle { get; init; }

    /// <summary>The exe's full path, for its icon.</summary>
    public string? Path { get; init; }

    public bool IsSystemSounds { get; init; }

    /// <summary>Windows says a stream is running in this session.</summary>
    public bool Active { get; init; }

    /// <summary>The session's own volume, 0–1 (a share of the output's volume).</summary>
    public float Level { get; init; }

    public bool Muted { get; init; }

    /// <summary>How loud it is at this moment, 0–1.</summary>
    public float Peak { get; init; }
}

public enum MixerRowKind { Everything, App, SystemSounds }

/// <summary>A row of the volume view: the whole output, one app, or Windows' own sounds.</summary>
/// <param name="Key">Stable for as long as the app runs; what sliders and the pipe act on.</param>
/// <param name="IconPath">The exe whose icon the row wears; null for Everything, system sounds and unreadable apps.</param>
public sealed record MixerRow(string Key, string Name, int Percent, bool Muted, MixerRowKind Kind, string? IconPath = null);

/// <summary>
/// Which rows the volume view shows, and in what order. "Everything" (the output's own volume) is always first.
/// Under it, one row per app that is playing something: an app's sessions count as one row however many
/// processes it plays from. A row the user changed stays until the view closes, even once the app falls silent,
/// so a slider never disappears from under the pointer.
///
/// Order: when the view opens, the loudest app comes first. After that nobody moves: an app that starts
/// playing later joins at the end, and one that stops just leaves its place. System sounds sit after the apps
/// they arrived with.
/// </summary>
public sealed class MixerBoard
{
    public const string EverythingKey = "everything", SystemKey = "system";

    readonly List<string> _order = new();
    readonly HashSet<string> _kept = new(StringComparer.Ordinal);

    /// <summary>The row a session belongs to: all of an app's processes share one.</summary>
    public static string KeyOf(MixerSource source) =>
        source.IsSystemSounds ? SystemKey
        : !string.IsNullOrWhiteSpace(source.ProcessName) ? "app:" + source.ProcessName.Trim().ToLowerInvariant()
        : "pid:" + source.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The user changed this row: it stays listed until <see cref="Clear"/>, playing or not.</summary>
    public void Keep(string key) => _kept.Add(key);

    public bool IsKept(string key) => _kept.Contains(key);

    /// <summary>The view closed: forget the order and the rows that were being kept.</summary>
    public void Clear()
    {
        _order.Clear();
        _kept.Clear();
    }

    /// <summary>The rows to show now.</summary>
    /// <param name="masterLevel">The output's volume, 0–1; null when there is no output (no Everything row).</param>
    /// <param name="useTitles">False while the screen is being shared: window titles may say more than an app's name.</param>
    public IReadOnlyList<MixerRow> Update(float? masterLevel, bool masterMuted, IEnumerable<MixerSource> sources, bool useTitles = true)
    {
        var groups = sources.GroupBy(KeyOf, StringComparer.Ordinal)
            .Where(g => g.Any(s => s.Active) || _kept.Contains(g.Key))
            .ToDictionary(g => g.Key, g => (Row: Row(g.Key, g.ToList(), useTitles), Peak: g.Max(s => s.Peak)), StringComparer.Ordinal);

        _order.RemoveAll(key => !groups.ContainsKey(key));
        // An app that quit is no longer being kept: if it comes back it is a new arrival.
        _kept.RemoveWhere(key => !groups.ContainsKey(key));
        var arrivals = groups.Where(g => !_order.Contains(g.Key))
            .OrderBy(g => g.Value.Row.Kind == MixerRowKind.SystemSounds)
            // Loudness to the nearest tenth: two apps at about the same level fall back to their names.
            .ThenByDescending(g => Math.Round(g.Value.Peak, 1))
            .ThenBy(g => g.Value.Row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key);
        _order.AddRange(arrivals);

        var rows = new List<MixerRow>(_order.Count + 1);
        if (masterLevel is { } level) rows.Add(new MixerRow(EverythingKey, MixerNames.Everything, Percent(level), masterMuted, MixerRowKind.Everything));
        rows.AddRange(_order.Select(key => groups[key].Row));
        return rows;
    }

    static MixerRow Row(string key, IReadOnlyList<MixerSource> sessions, bool useTitles)
    {
        // What is playing speaks for the app; a silent leftover session's old setting doesn't.
        var live = sessions.Where(s => s.Active).ToList();
        if (live.Count == 0) live = sessions.ToList();
        int percent = Percent(live.Max(s => s.Level));
        bool muted = live.All(s => s.Muted);
        if (key == SystemKey) return new MixerRow(key, MixerNames.SystemSounds, percent, muted, MixerRowKind.SystemSounds);

        // Any of the app's processes may be the one with the description, the window or a readable path.
        string? First(Func<MixerSource, string?> pick) => sessions.Select(pick).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        string name = MixerNames.App(First(s => s.ProcessName), First(s => s.Description), useTitles ? First(s => s.WindowTitle) : null);
        return new MixerRow(key, name, percent, muted, MixerRowKind.App, First(s => s.Path));
    }

    /// <summary>A 0–1 level as the whole number the row shows.</summary>
    public static int Percent(float level) =>
        float.IsNaN(level) ? 0 : (int)Math.Round(Math.Clamp(level, 0f, 1f) * 100, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The row someone means by a name ("everything", "chrome", "youtube"): the exact name first, then one that
    /// starts with it, then one that contains it. Null when none does.
    /// </summary>
    public static MixerRow? Find(IReadOnlyList<MixerRow> rows, string? name)
    {
        var wanted = (name ?? "").Trim();
        if (wanted.Length == 0) return null;
        if (wanted.Equals("master", StringComparison.OrdinalIgnoreCase)) wanted = MixerNames.Everything;
        return rows.FirstOrDefault(r => r.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            ?? rows.FirstOrDefault(r => r.Name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))
            ?? rows.FirstOrDefault(r => r.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));
    }
}
