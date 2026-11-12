using System.Text.RegularExpressions;
using Shammy.Core.Productivity;

namespace Shammy.Core.Quick;

/// <summary>
/// The Laptop card's switches said or typed: "turn off wifi", "wifi on", "bluetooth off", "night light on",
/// "do not disturb", "quiet for an hour", "performance mode", "best battery", and Hinglish such as "wifi band karo"
/// or "bluetooth chalu karo". Like <see cref="QuickPhrases"/>, the whole text must be the request: these run
/// before every other command, so "wifi password", "bluetooth headphones" or "how to turn off wifi" are searches.
/// Restart and shut down are never here (the card asks first).
/// </summary>
public static class SwitchPhrases
{
    const RegexOptions Opts = RegexOptions.CultureInvariant;

    // Each switch's names (after Normalize has joined "wi-fi", "blue tooth", "night-light" and "dnd" spellings).
    static readonly (QuickKind Kind, string Names)[] Things =
    {
        (QuickKind.Wifi, "wifi|wireless|wlan"),
        (QuickKind.Bluetooth, "bluetooth"),
        (QuickKind.NightLight, "night light|blue light filter|warm screen"),
    };

    const string My = "(?:(?:the|my|this|pc|laptop|pc's|laptop's) )*";
    const string HinglishOn = "chalu|chaalu|shuru|start|on|kholo";
    const string HinglishOff = "band|bandh|bund|off";
    const string HinglishVerb = "(?: (?:karo|kar do|kardo|kar dijiye|kijiye|kar dena|kar de|kar|do))?";

    static readonly Regex[] Patterns = Things.Select(t => new Regex(
        // "turn off wifi", "switch on the bluetooth", "enable night light"
        $"^(?:(?:(?:turn|switch|put) (?<state>on|off) {My}(?<thing>{t.Names}))|" +
        $"(?:(?<verb>enable|disable|start|stop) {My}(?<thing>{t.Names}))|" +
        // "wifi off", "turn the wifi off", "bluetooth on now"
        $"(?:(?:(?:turn|switch) )?{My}(?<thing>{t.Names}) (?<state>on|off))|" +
        // "wifi band karo", "bluetooth chalu kar do", "wifi ko off karo"
        $"(?:{My}(?<thing>{t.Names}) (?:ko )?(?<hindi>{HinglishOn}|{HinglishOff}){HinglishVerb}))(?: now| please| abhi)?$", Opts)).ToArray();

    // "do not disturb", "dnd on", "turn on quiet mode", "quiet for an hour", "do not disturb for 2 hours", "disturb mat karo".
    static readonly Regex QuietOn = new(
        "^(?:(?:turn on|switch on|start|enable|go) )?(?:do not disturb(?: me)?|dnd|quiet mode|quiet|silent mode)(?: mode)?(?: on)?" +
        "(?: (?:for )?(?<for>.+))?$", Opts);

    static readonly Regex QuietHinglish = new("^(?:mujhe )?disturb (?:mat|na) (?:karo|karna|kar)$", Opts);

    static readonly Regex QuietOff = new(
        "^(?:(?:(?:turn|switch) off|stop|end|disable|cancel) (?:the )?(?:do not disturb|dnd|quiet mode|quiet|silent mode)(?: mode)?|" +
        "(?:turn |switch )?(?:the )?(?:do not disturb|dnd|quiet mode|quiet|silent mode)(?: mode)? off|" +
        "(?:do not disturb|dnd|quiet mode) (?:band|bandh) (?:karo|kar do|kardo))$", Opts);

    /// <summary>What the text asks of a switch, or null when it isn't one of these requests.</summary>
    public static QuickCommand? Parse(string text)
    {
        var s = Normalize(text);
        if (s.Length == 0) return null;

        if (PowerMode.Parse(s) is { } mode) return new QuickCommand(QuickKind.PowerMode, Mode: mode);

        if (QuietOff.IsMatch(s)) return new QuickCommand(QuickKind.Quiet, On: false);
        if (QuietHinglish.IsMatch(s)) return new QuickCommand(QuickKind.Quiet, On: true);
        var quiet = QuietOn.Match(s);
        if (quiet.Success)
        {
            // "quiet" alone is a word to search; it needs "mode", "on", a length or "go".
            bool bare = s is "quiet" or "silent mode";
            if (bare) return null;
            if (!quiet.Groups["for"].Success) return new QuickCommand(QuickKind.Quiet, On: true);
            // Whatever follows must be a length and nothing else ("quiet places in delhi" is a search).
            if (DurationParser.Parse(quiet.Groups["for"].Value) is not { } d || d.TotalMinutes < 1) return null;
            return new QuickCommand(QuickKind.Quiet, (int)Math.Min(QuietMaxMinutes, Math.Ceiling(d.TotalMinutes)), On: true);
        }

        for (int i = 0; i < Things.Length; i++)
        {
            var m = Patterns[i].Match(s);
            if (!m.Success) continue;
            bool on = m.Groups["state"].Success ? m.Groups["state"].Value == "on"
                : m.Groups["verb"].Success ? m.Groups["verb"].Value is "enable" or "start"
                : !Regex.IsMatch(m.Groups["hindi"].Value, $"^(?:{HinglishOff})$", Opts);
            return new QuickCommand(Things[i].Kind, On: on);
        }
        return null;
    }

    /// <summary>The longest quiet a command asks for (a forgotten one must still end).</summary>
    public const int QuietMaxMinutes = 12 * 60;

    /// <summary>Spellings joined: "wi-fi"/"wi fi" → "wifi", "blue tooth" → "bluetooth", "nightlight" → "night light", "don't disturb" → "do not disturb".</summary>
    static string Normalize(string text)
    {
        var s = Text.PhraseText.Normalize(text);
        s = Regex.Replace(s, @"\bwi[- ]?fi\b", "wifi", Opts);
        s = Regex.Replace(s, @"\bblue[- ]tooth\b", "bluetooth", Opts);
        s = Regex.Replace(s, @"\bnight[- ]?light\b", "night light", Opts);
        s = Regex.Replace(s, @"\b(?:don'?t|don’t|dont) disturb\b", "do not disturb", Opts);
        s = Regex.Replace(s, @"\bd\.?n\.?d\b", "dnd", Opts);
        s = s.Replace("do-not-disturb", "do not disturb");
        return s;
    }
}

/// <summary>"health", "open health", "clean up my pc", "storage", "disk space": open the Health card.</summary>
public static class HealthPhrases
{
    static readonly HashSet<string> Open = new(StringComparer.Ordinal)
    {
        "health", "open health", "show health", "pc health", "laptop health", "my pc health", "my laptop health", "check my pc health",
        "check pc health", "open the health card", "health card", "system health",
        "cleanup", "clean up", "clean up my pc", "clean up my laptop", "clean my pc", "clean my laptop", "cleanup my pc",
        "clean up the pc", "clean up pc", "pc cleanup", "free up space", "free up disk space", "free some space",
        "storage", "disk space", "open storage", "show storage", "check storage", "check disk space", "storage space",
    };

    public static bool IsOpen(string text) => Open.Contains(Text.PhraseText.Normalize(text).Replace("clean-up", "clean up"));
}
