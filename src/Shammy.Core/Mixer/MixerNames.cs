using System.Text.RegularExpressions;

namespace Shammy.Core.Mixer;

/// <summary>
/// What the volume view calls things. An app is named by its file description ("Google Chrome", "Spotify"),
/// the name people know it by; when the exe has none worth showing (nothing, or a runtime's such as "Electron"
/// or "Java(TM) Platform SE binary"), by the app part of its main window's title ("clip.mp4 - VLC media player"
/// gives "VLC media player"); and last by the process name. Output devices lose Windows' "Speakers (driver)"
/// wrapping.
/// </summary>
public static class MixerNames
{
    public const string Everything = "Everything", SystemSounds = "System sounds";

    /// <summary>Longer than this is a sentence or a document title, not an app's name.</summary>
    const int MaxName = 32;

    /// <summary>Descriptions that name the runtime an app is built on rather than the app.</summary>
    static readonly HashSet<string> Runtimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Electron", "Java(TM) Platform SE binary", "OpenJDK Platform binary", "Python", "Node.js JavaScript Runtime",
        "nwjs", "NW.js", "Windows host process (Rundll32)", "Host Process for Windows Services", "Application Frame Host",
        "Runtime Broker", "Microsoft Edge WebView2", "WebView2", "CefSharp.BrowserSubprocess", "QtWebEngineProcess",
        "BootstrapPackagedGame", "UE4Game", "UnrealGame", "UnityPlayer", "Unity Player", "Game", "Application", "Launcher",
    };

    static readonly string[] TitleSeparators = { " - ", " \u2013 ", " \u2014 ", " | ", " \u00B7 " };

    /// <summary>The name of an app's row.</summary>
    /// <param name="processName">The exe's name without ".exe".</param>
    /// <param name="description">The exe's file description, if it could be read.</param>
    /// <param name="windowTitle">The main window's title, if it has one and it may be shown.</param>
    public static string App(string? processName, string? description, string? windowTitle)
    {
        var said = Clean(description);
        if (said.Length is > 0 and <= MaxName && !Runtimes.Contains(said)) return said;
        if (TitleApp(windowTitle) is { } fromTitle) return fromTitle;
        if (said.Length is > 0 and <= MaxName) return said;
        var exe = Clean(processName);
        if (exe.Length == 0) return "App";
        // "vlc" reads better as "Vlc"; a name that already has capitals ("WhatsApp") is left alone.
        return exe.Any(char.IsUpper) ? exe : char.ToUpperInvariant(exe[0]) + exe[1..];
    }

    /// <summary>
    /// The app's part of a window title: what follows the last separator ("Inbox - Gmail - Google Chrome" gives
    /// "Google Chrome"), or the whole title when it has none ("Minecraft 1.21"). Null when that is too long to be
    /// a name.
    /// </summary>
    public static string? TitleApp(string? windowTitle)
    {
        var title = Clean(windowTitle);
        if (title.Length == 0) return null;
        int cut = -1, length = 0;
        foreach (var separator in TitleSeparators)
        {
            int at = title.LastIndexOf(separator, StringComparison.Ordinal);
            if (at > cut)
            {
                cut = at;
                length = separator.Length;
            }
        }
        var app = cut < 0 ? title : title[(cut + length)..].Trim();
        return app.Length is >= 2 and <= MaxName ? app : null;
    }

    static readonly Regex Wrapped = new(@"^(?<kind>[^(]+?)\s*\((?<device>.+)\)$", RegexOptions.CultureInvariant);

    /// <summary>The parts of a device name that say what chip plays the sound, which nobody chooses by.</summary>
    static readonly Regex Driver = new(
        @"realtek|high definition audio|hd audio|usb audio|audio device|intel|nvidia|\bamd\b|conexant|synaptics|smart sound|sound card|\bcodec\b|display audio",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>What Windows calls an output when the device itself has the real name.</summary>
    static readonly HashSet<string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Speakers", "Speaker", "Headphones", "Headphone", "Headset", "Headset Earphone", "Earphone", "Earphones",
        "Digital Output", "Digital Audio", "Line Out", "Output",
    };

    static readonly Regex BluetoothTail = new(@"\s+(?:Hands-Free(?:\s+AG)?(?:\s+Audio)?|Stereo)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// A short name for an output: "Speakers (Realtek(R) Audio)" is "Speakers", "Headphones (Airdopes 311 Pro)"
    /// is "Airdopes 311 Pro", "LG ULTRAGEAR (NVIDIA High Definition Audio)" is "LG ULTRAGEAR".
    /// </summary>
    public static string Output(string? friendlyName)
    {
        var name = Clean(friendlyName);
        if (name.Length == 0) return "Output";
        if (Wrapped.Match(name) is not { Success: true } m) return name;
        string kind = m.Groups["kind"].Value.Trim(), device = Device(m);
        if (device.Length == 0 || Driver.IsMatch(device)) return kind;
        return Kinds.Contains(kind) ? device : kind;
    }

    /// <summary>
    /// Short names for a set of outputs, in the order given. Two that would read the same ("Airdopes 311 Pro" as
    /// headphones and as a headset) keep Windows' own word in front so they can be told apart.
    /// </summary>
    public static IReadOnlyList<string> Outputs(IReadOnlyList<string> friendlyNames)
    {
        var names = friendlyNames.Select(Output).ToList();
        for (int i = 0; i < names.Count; i++)
        {
            bool shared = false;
            for (int j = 0; j < names.Count; j++) shared |= j != i && string.Equals(Output(friendlyNames[j]), Output(friendlyNames[i]), StringComparison.OrdinalIgnoreCase);
            if (!shared) continue;
            var full = Clean(friendlyNames[i]);
            names[i] = Wrapped.Match(full) is { Success: true } m && !Driver.IsMatch(m.Groups["device"].Value)
                ? $"{m.Groups["kind"].Value.Trim()} \u00B7 {Device(m)}"
                : full;
        }
        return names;
    }

    /// <summary>The device inside the brackets, without Windows' "2- " counter or Bluetooth's profile words.</summary>
    static string Device(Match wrapped) =>
        BluetoothTail.Replace(Regex.Replace(wrapped.Groups["device"].Value.Trim(), @"^\d+-\s*", ""), "").Trim();

    /// <summary>Cuts a label to <paramref name="max"/> characters with an ellipsis, on a clean edge.</summary>
    public static string Fit(string text, int max)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..(max - 1)].TrimEnd(' ', '-', '(', '\u00B7') + "\u2026";
    }

    /// <summary>Trimmed, single spaces, without a trailing ".exe".</summary>
    static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = Regex.Replace(text.Trim(), @"\s+", " ");
        return s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s[..^4].TrimEnd() : s;
    }
}

/// <summary>An output in the volume view's switcher.</summary>
/// <param name="Label">Its short name, cut to fit a segment.</param>
/// <param name="Current">Sound is playing through this one.</param>
public sealed record MixerOutput(string Id, string Label, bool Current);

/// <summary>
/// Which outputs the switcher offers. A segmented control only has room for a few, so it shows the current one
/// and the first others by name; they are always listed by name, so choosing one doesn't shuffle the rest.
/// </summary>
public static class MixerOutputs
{
    public const int Most = 4, LabelLength = 20;

    /// <param name="devices">Every active output, with Windows' full name for each.</param>
    public static IReadOnlyList<MixerOutput> Choose(IEnumerable<(string Id, string Name, bool Current)> devices, int most = Most)
    {
        var shown = devices
            .OrderByDescending(d => d.Current).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, most))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Id, StringComparer.Ordinal)
            .ToList();
        var labels = MixerNames.Outputs(shown.Select(d => d.Name).ToList());
        return shown.Select((d, i) => new MixerOutput(d.Id, MixerNames.Fit(labels[i], LabelLength), d.Current)).ToList();
    }
}
