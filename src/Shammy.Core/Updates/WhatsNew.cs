namespace Shammy.Core.Updates;

/// <summary>
/// The short note Shammy shows the first time it starts after an update. The lines ship inside the app, so the note
/// works offline. Add a version's lines here before releasing it: at most three, and short enough for one line each.
/// </summary>
public static class WhatsNew
{
    public const int MaxLines = 3;

    /// <summary>Copies up to this version never recorded what they ran, so "nothing recorded" on an existing install
    /// means this one or older, and every note since is news to them.</summary>
    const string LastBeforeNotes = "0.5.4";

    /// <summary>What fits on one line of the note.</summary>
    public const int MaxLineLength = 52;

    public static readonly IReadOnlyDictionary<string, string[]> Notes = new Dictionary<string, string[]>
    {
        ["0.6.1"] = new[]
        {
            "A guide to everything Shammy does: click ?",
            "Find a feature in your own words",
            "Say help, or ask how do I record my screen",
        },
        ["0.6.0"] = new[]
        {
            "Shammy is a cat now. Rest your pointer on it",
            "Record your screen, live captions, Claude Code",
            "A spinning record, a volume mixer and much more",
        },
        ["0.5.7"] = new[]
        {
            "Update notes now cover everything you missed",
        },
        ["0.5.6"] = new[]
        {
            "Update checks are more patient on slow connections",
        },
        ["0.5.5"] = new[]
        {
            "Messages now open right in the notch",
            "Message text hides the moment you start sharing",
            "This little note after every update",
        },
    };

    /// <summary>
    /// The lines to show now; empty for nothing. A fresh install shows nothing (the tour does the welcoming). Someone
    /// who skipped versions sees the newest lines first, up to <see cref="MaxLines"/>.
    /// </summary>
    /// <param name="lastRun">The version that last ran here; null when none was recorded yet.</param>
    /// <param name="existingUser">Shammy has run here before: a null <paramref name="lastRun"/> then means a version
    /// from before this note existed, not a fresh install.</param>
    public static IReadOnlyList<string> LinesFor(string? lastRun, string current, bool existingUser)
    {
        if (Parse(current) is not { } now) return Array.Empty<string>();
        var last = Parse(lastRun);
        if (last is null)
        {
            if (!existingUser) return Array.Empty<string>();
            last = Parse(LastBeforeNotes);
        }
        if (last >= now) return Array.Empty<string>();

        return Notes
            .Select(n => (Version: Parse(n.Key), Lines: n.Value))
            .Where(n => n.Version is not null && n.Version <= now && n.Version > last)
            .OrderByDescending(n => n.Version)
            .SelectMany(n => n.Lines)
            .Distinct()
            .Take(MaxLines)
            .ToList();
    }

    /// <summary>"0.5.5", also from "0.5.5+abc" or "0.5.5-beta".</summary>
    public static Version? Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var plain = version.Trim().Split('+', '-')[0];
        return Version.TryParse(plain, out var v) ? v : null;
    }
}
