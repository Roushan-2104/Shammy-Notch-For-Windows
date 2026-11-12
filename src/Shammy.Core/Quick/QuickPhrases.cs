using System.Text.RegularExpressions;
using Shammy.Core.Productivity;

namespace Shammy.Core.Quick;

public enum QuickKind
{
    /// <summary>"keep awake", "stay awake 30 min".</summary>
    KeepAwake,
    /// <summary>"stop keep awake".</summary>
    StopKeepAwake,
    /// <summary>"lock", "lock my pc".</summary>
    Lock,
    /// <summary>"sleep my pc", "go to sleep".</summary>
    Sleep,
    /// <summary>"laptop controls", "quick actions": open the Laptop card.</summary>
    Open,
    /// <summary>"turn off wifi", "wifi band karo" (see <see cref="SwitchPhrases"/>).</summary>
    Wifi,
    /// <summary>"bluetooth on", "bluetooth chalu karo".</summary>
    Bluetooth,
    /// <summary>"night light on": Shammy's own warm tint.</summary>
    NightLight,
    /// <summary>"do not disturb", "quiet for an hour": Shammy holds its pop-ups.</summary>
    Quiet,
    /// <summary>"performance mode", "best battery", "balanced mode".</summary>
    PowerMode,
}

/// <param name="Minutes">For <see cref="QuickKind.KeepAwake"/> and <see cref="QuickKind.Quiet"/>: how long was asked for, or null for the usual hour.</param>
/// <param name="On">For the switches: on (true) or off (false).</param>
/// <param name="Mode">For <see cref="QuickKind.PowerMode"/>: the mode asked for.</param>
public sealed record QuickCommand(QuickKind Kind, int? Minutes = null, bool? On = null, PowerModeKind? Mode = null);

/// <summary>
/// The typed or spoken phrases the quick actions answer. The whole text must be one of them: a question or any
/// extra topic word ("lock screen wallpaper ideas", "how to sleep better", "keep awake pills") is a web search.
/// These run before every other module's commands, so nothing here is a loose pattern.
/// </summary>
public static class QuickPhrases
{
    const string Device = "(?:pc|computer|laptop|system|machine)", My = "(?:my|the|this)";

    /// <summary>"keep awake", "keep my pc awake", "keep the screen awake", "keep it awake".</summary>
    const string Keep = $"keep (?:(?:(?:{My} )?(?:{Device}|screen|display)|it) )?awake";

    const string Keeping = $"(?:keep(?:ing)? (?:(?:(?:{My} )?(?:{Device}|screen|display)|it) )?awake|stay(?:ing)? awake)";

    static readonly HashSet<string> OpenCard = new(StringComparer.Ordinal)
    {
        "quick actions", "quick action", "open quick actions", "show quick actions", "my quick actions", "show my quick actions",
        // The card is called Laptop; a bare "laptop" stays a search.
        "laptop controls", "open laptop controls", "show laptop controls", "my laptop controls", "open laptop", "open the laptop card",
        "laptop card", "open laptop card", "pc controls", "open pc controls",
    };

    // "lock screen" alone stays the Windows settings page the palette offers; "lock the screen" locks.
    static readonly Regex LockRx = new($"^lock(?: (?:(?:{My} )?{Device}|{My} screen))?(?: now)?$", RegexOptions.CultureInvariant);

    // Never bare "sleep": that is a search (or the power settings page).
    static readonly Regex SleepRx = new(
        $"^(?:sleep (?:{My} )?{Device}|put (?:{My} )?{Device} to sleep|go to sleep)(?: now)?$", RegexOptions.CultureInvariant);

    static readonly Regex StopRx = new(
        $"^(?:(?:stop|cancel|end|disable|turn off) {Keeping}|{Keeping} (?:off|stop)|turn {Keeping} off)$", RegexOptions.CultureInvariant);

    static readonly Regex KeepRx = new(
        $"^(?:(?:turn on|start) )?(?:{Keep}|stay awake)(?: on)?(?: (?:for )?(?<for>.+))?$", RegexOptions.CultureInvariant);

    /// <summary>What the text asks for, or null when it isn't one of the quick actions.</summary>
    public static QuickCommand? Parse(string text)
    {
        var s = Normalize(text);
        if (s.Length == 0) return null;
        if (OpenCard.Contains(s)) return new QuickCommand(QuickKind.Open);
        if (LockRx.IsMatch(s)) return new QuickCommand(QuickKind.Lock);
        if (SleepRx.IsMatch(s)) return new QuickCommand(QuickKind.Sleep);
        if (StopRx.IsMatch(s)) return new QuickCommand(QuickKind.StopKeepAwake);
        var keep = KeepRx.Match(s);
        if (!keep.Success) return SwitchPhrases.Parse(text);
        if (!keep.Groups["for"].Success) return new QuickCommand(QuickKind.KeepAwake);
        // Whatever follows must be a duration and nothing else ("keep awake pills" is not).
        if (DurationParser.Parse(keep.Groups["for"].Value) is not { } d) return null;
        return new QuickCommand(QuickKind.KeepAwake, (int)Math.Min(int.MaxValue, Math.Ceiling(d.TotalMinutes)));
    }

    /// <summary>Lower case, single spaces, no trailing punctuation, "keep-awake" as two words, no "please" or "hey shammy".</summary>
    static string Normalize(string text)
    {
        var s = Text.PhraseText.Normalize(text);
        return s.Replace("keep-awake", "keep awake").Replace("keepawake", "keep awake");
    }
}
