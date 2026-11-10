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
    /// <summary>"quick actions": open the card.</summary>
    Open,
}

/// <param name="Minutes">For <see cref="QuickKind.KeepAwake"/>: how long was asked for, or null for the usual hour.</param>
public sealed record QuickCommand(QuickKind Kind, int? Minutes = null);

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
        if (!keep.Success) return null;
        if (!keep.Groups["for"].Success) return new QuickCommand(QuickKind.KeepAwake);
        // Whatever follows must be a duration and nothing else ("keep awake pills" is not).
        if (DurationParser.Parse(keep.Groups["for"].Value) is not { } d) return null;
        return new QuickCommand(QuickKind.KeepAwake, (int)Math.Min(int.MaxValue, Math.Ceiling(d.TotalMinutes)));
    }

    /// <summary>Lower case, single spaces, no trailing punctuation, "keep-awake" as two words, no "please" or "hey shammy".</summary>
    static string Normalize(string text)
    {
        var s = Regex.Replace(text.Trim().TrimEnd('.', '!', '?').Trim(), @"\s+", " ").ToLowerInvariant();
        s = Regex.Replace(s, @"^(?:hey shammy,? |shammy,? )", "");
        s = Regex.Replace(s, @"^please |,? please$", "");
        return s.Replace("keep-awake", "keep awake").Replace("keepawake", "keep awake");
    }
}
