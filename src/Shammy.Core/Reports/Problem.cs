using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Shammy.Core.Reports;

/// <summary>An exception as a report sees it: its type, message and stack frames ("Namespace.Type.Method").</summary>
public sealed record CrashStack(string Type, string Message, IReadOnlyList<string> Frames)
{
    static readonly Regex Frame = new(@"^\s*at\s+(?<name>[^(\s]+)", RegexOptions.CultureInvariant);

    public string ShortType => Type[(Type.LastIndexOf('.') + 1)..];

    public static CrashStack FromException(Exception ex)
    {
        // The root cause: a TargetInvocationException or a single-error AggregateException only wraps it.
        var root = ex.GetBaseException();
        return new CrashStack(root.GetType().FullName ?? root.GetType().Name, root.Message, ParseFrames(root.StackTrace ?? ""));
    }

    /// <summary>From Exception.ToString() text, as the .NET Runtime event and Shammy's own crash file hold it.</summary>
    public static CrashStack? FromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Replace("\r", "").Split('\n');
        // "Outer: message ---> Inner: message": the innermost is the cause, and its frames come first.
        var head = lines[0];
        var inner = head.LastIndexOf("---> ", StringComparison.Ordinal);
        if (inner >= 0) head = head[(inner + 5)..];
        var colon = head.IndexOf(": ", StringComparison.Ordinal);
        var type = (colon > 0 ? head[..colon] : head).Trim();
        if (type.Length == 0 || type.Contains(' ')) return null;
        var message = colon > 0 ? head[(colon + 2)..].Trim() : "";
        var frames = new List<string>();
        foreach (var line in lines.Skip(1))
        {
            if (line.Contains("--- End of inner exception stack trace ---", StringComparison.Ordinal)) break;
            if (Frame.Match(line) is { Success: true } m) frames.Add(m.Groups["name"].Value);
        }
        return new CrashStack(type, message, frames);
    }

    /// <summary>The ".NET Runtime" event Windows logs when Shammy dies of an exception (id 1026).</summary>
    public static CrashStack? FromRuntimeEvent(string message)
    {
        const string Marker = "Exception Info: ";
        var at = message.IndexOf(Marker, StringComparison.Ordinal);
        return at < 0 ? null : FromText(message[(at + Marker.Length)..]);
    }

    /// <summary>The "Application Error" event (id 1000) for a crash outside .NET: only the faulting module and the code.</summary>
    public static CrashStack FromNativeCrash(string module, string code)
    {
        module = module.Trim();
        code = code.Trim().ToLowerInvariant();
        if (code.Length == 0) code = "unknown";
        else if (!code.StartsWith("0x", StringComparison.Ordinal)) code = "0x" + code;
        return new CrashStack($"NativeCrash.{code}", $"in {module}", module.Length > 0 ? new[] { module } : Array.Empty<string>());
    }

    static List<string> ParseFrames(string stackTrace) =>
        stackTrace.Replace("\r", "").Split('\n').Select(l => Frame.Match(l)).Where(m => m.Success).Select(m => m.Groups["name"].Value).ToList();
}

/// <summary>
/// Which part of Shammy a stack points at: its name for people, and the feature safe mode can pause for it (none for
/// the parts Shammy can't run without, like the island itself or search).
/// </summary>
public sealed record ProblemPlace(string AreaKey, string AreaName, string? FeatureKey, string? FeatureName, string Where)
{
    public const string Dictation = "dictation";
    const string ModulePrefix = "module:";

    // Checked against each frame from the top; the first frame that matches decides.
    static readonly (string Contains, string Key, string Name, string? Feature, string? FeatureName)[] Rules =
    {
        ("Shammy.Search.Dictation", "voice", "Voice › Dictation", Dictation, "Dictation"),
        ("Shammy.Search.MicRecorder", "voice", "Voice › Microphone", Dictation, "Dictation"),
        ("Shammy.Search.MicCapture", "voice", "Voice › Microphone", Dictation, "Dictation"),
        ("Shammy.Search.Whisper", "voice", "Voice › Speech", Dictation, "Dictation"),
        ("NAudio.Wave.WaveIn", "voice", "Voice › Microphone", Dictation, "Dictation"),
        ("Whisper.net", "voice", "Voice › Speech", Dictation, "Dictation"),
        // A crash outside .NET only names a module: these are the microphone's.
        ("wdmaud.drv", "voice", "Voice › Microphone", Dictation, "Dictation"),
        ("winmm.dll", "voice", "Voice › Microphone", Dictation, "Dictation"),
        ("Shammy.Search.", "search", "Search and circle", null, null),
        ("Shammy.Playback.", "music", "Music › Now playing", null, null),
        ("Shammy.Notifications.", "alerts", "Alerts", null, null),
        ("Shammy.Shell.", "island", "The island", null, null),
        ("Shammy.Views.", "island", "The island", null, null),
        ("Shammy.Motion.", "island", "The island", null, null),
    };

    static readonly Dictionary<string, string> ModuleNames = new()
    {
        ["ClipboardHistory"] = "Clipboard history",
        ["OnlineVoice"] = "Online voice",
        ["PhoneLink"] = "Phone link",
        ["PhoneSend"] = "To phone",
        ["CircleMemory"] = "Circle memory",
        ["MeetingSystem"] = "Meeting tools",
        ["SelectionTools"] = "Selection tools",
        ["WhatsNew"] = "Update note",
        ["Ambient"] = "Weather and ticker",
    };

    public static ProblemPlace Find(IReadOnlyList<string> frames)
    {
        foreach (var frame in frames)
        {
            if (frame.StartsWith("Shammy.Modules.", StringComparison.Ordinal))
            {
                // Namespace.Type.Method: "Shammy.Modules.Music.LyricsSource.Pick". Shammy.Modules' own helpers
                // (ModuleContext, ...) belong to no one module, so the next frame decides.
                var parts = frame.Split('.');
                if (parts.Length < 5) continue;
                var folder = parts[2];
                var name = ModuleNames.TryGetValue(folder, out var known) ? known : Words(folder);
                // Settings is how a paused feature is turned back on, and Reports tells the user about it: neither pauses.
                // The module's folder: what a lambda or a nested folder (Productivity.Alarms) crashed in belongs to it.
                var feature = folder is "Settings" or "Reports" ? null : ModulePrefix + "Shammy.Modules." + folder;
                return new ProblemPlace(folder.ToLowerInvariant(), name, feature, feature is null ? null : name, Short(frame));
            }
            foreach (var rule in Rules)
            {
                if (frame.Contains(rule.Contains, StringComparison.OrdinalIgnoreCase))
                    return new ProblemPlace(rule.Key, rule.Name, rule.Feature, rule.FeatureName, Short(frame));
            }
        }
        var shammy = frames.FirstOrDefault(f => f.StartsWith("Shammy.", StringComparison.Ordinal));
        return new ProblemPlace("shammy", "Shammy", null, null, shammy is null ? (frames.Count > 0 ? Short(frames[0]) : "") : Short(shammy));
    }

    /// <summary>The module namespace a feature key pauses ("Shammy.Modules.Music"), or null for one that isn't a module.</summary>
    public static string? ModuleNamespace(string featureKey) =>
        featureKey.StartsWith(ModulePrefix, StringComparison.Ordinal) ? featureKey[ModulePrefix.Length..] : null;

    /// <summary>"WaveInEvent.DoRecording" from "NAudio.Wave.WaveInEvent.DoRecording".</summary>
    static string Short(string frame)
    {
        var parts = frame.Split('.');
        return parts.Length >= 2 ? $"{parts[^2]}.{parts[^1]}" : frame;
    }

    static string Words(string pascal)
    {
        var spaced = Regex.Replace(pascal, "(?<=[a-z])(?=[A-Z])", " ");
        return spaced.Length > 1 ? spaced[0] + spaced[1..].ToLowerInvariant() : spaced;
    }
}

/// <summary>Rules for what's worth a report and how the same problem is recognised again.</summary>
public static class ProblemRules
{
    /// <summary>An error is offered for a report once it happens this often in a day.</summary>
    public const int RepeatsPerDay = 5;

    /// <summary>Safe mode pauses a feature after this many crashes in it in a day.</summary>
    public const int CrashesPerDay = 2;

    /// <summary>
    /// Same error, same place: the same problem. "crash/voice/3f9ac2". Line numbers, lambda numbers and the message's
    /// numbers don't count, so the next version's build still matches.
    /// </summary>
    public static string Signature(string kind, ProblemPlace place, string type, string? context = null)
    {
        var basis = $"{type}|{Regex.Replace(place.Where, @"\d+", "")}|{Regex.Replace(context ?? "", @"\d+", "")}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(basis)))[..6].ToLowerInvariant();
        var area = Regex.Replace(place.AreaKey.ToLowerInvariant(), "[^a-z0-9]", "");
        return $"{kind}/{(area.Length > 0 ? area : "shammy")}/{hash}";
    }

    /// <summary>
    /// A dropped connection, an unreachable server or a timeout: the network's fault, not Shammy's, and they come and go
    /// with Wi-Fi. A server's error answer (a 400, a 500) isn't one: that can be a bug in what Shammy asked.
    /// </summary>
    public static bool IsNetworkBlip(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case OperationCanceledException or TimeoutException or SocketException:
                    return true;
                case HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
                    or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError or HttpRequestError.ResponseEnded }:
                    return true;
                case System.Net.WebSockets.WebSocketException:
                    return true;
            }
        }
        return false;
    }
}
