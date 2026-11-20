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
        ["1.6.0"] = new[]
        {
            "After a crash, Shammy offers to send a report",
            "You see every report before it’s sent",
            "A feature that keeps crashing pauses itself",
        },
        ["1.5.3"] = new[]
        {
            "Lyrics for songs playing in your browser too",
            "Copied pictures always land in clipboard history",
            "Dictation no longer fails when Gnani is busy",
        },
        ["1.5.2"] = new[]
        {
            "Update checks no longer fail on shared connections",
            "Fixed: Shammy closing when you start talking",
        },
        ["1.5.1"] = new[]
        {
            "Fixed: Shammy closing when you start talking",
            "Quick taps of the talk key free the mic properly",
        },
        ["1.5.0"] = new[]
        {
            "New: Shammy Island links your Android phone",
            "Phone button at the top: scan a QR to get the app",
            "One list for both; answer Claude from your phone",
        },
        ["1.4.0"] = new[]
        {
            "The time, always: on the island and when it’s open",
            "Tap the date: your month opens right beside it",
            "Alarms ring in the sky outside: dawn, day or night",
        },
        ["1.3.0"] = new[]
        {
            "Hold Ctrl + Win, talk: it types where you are",
            "Say “in Hindi” and it’s typed in Devanagari",
            "Undo in one tap; your clipboard is put back",
        },
        ["1.2.3"] = new[]
        {
            "Private copies and hidden alerts stay private",
            "Hotkeys and hover come back after lock or sleep",
            "Lighter on memory, with 30 fixes under the hood",
        },
        ["1.2.2"] = new[]
        {
            "The everything page stays full size after a card",
            "The eyes always take you back to the first page",
        },
        ["1.2.1"] = new[]
        {
            "Slide right on the island: everything, A to Z",
            "Stage: paste, type or drop your script in",
            "To phone asks what to send; drag the song line",
        },
        ["1.2.0"] = new[]
        {
            "Circle a product: who sells it cheapest",
            "Your circles remembered, found in your words",
            "Drag answers anywhere; hold Left Alt to understand",
        },
        ["1.1.0"] = new[]
        {
            "Circle anything: say what to do, or get the answer",
            "Questions get a short answer, read aloud",
            "Every card redesigned to fit the island",
        },
        ["1.0.0"] = new[]
        {
            "A floating island with soft light inside",
            "Stage: your script under the camera, paced by voice",
            "The dial blooms where you hold the middle button",
        },
        ["0.9.0"] = new[]
        {
            "Settings has a search box and clearer tabs",
            "Laptop card: Quiet, night light and voice control",
            "Click the open notch to use it with the keyboard",
        },
        ["0.8.0"] = new[]
        {
            "Laptop controls, health and cleanup in one card",
            "One-time codes show big in the notch, with Copy",
            "Say “good morning” for your morning brief",
        },
        ["0.7.0"] = new[]
        {
            "Hold the middle mouse button for the cat wheel",
            "Hinglish works: “screenshot le lo”, “gaana bajao”",
            "Your words show as you talk; it asks if unsure",
        },
        ["0.6.3"] = new[]
        {
            "Click the small tab under the notch",
            "It slides away for five seconds, then returns",
        },
        ["0.6.2"] = new[]
        {
            "Something under the notch? Push up into it",
            "It slides away for five seconds, then returns",
        },
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

    /// <summary>A version's note can carry one button that opens a card: the way in to what's new (its label, the card's id).</summary>
    public static readonly IReadOnlyDictionary<string, (string Label, string CardId)> Links = new Dictionary<string, (string, string)>
    {
        ["1.5.0"] = ("Get it for your phone", "phone"),
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
