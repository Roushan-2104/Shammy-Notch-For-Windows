using System.Text.RegularExpressions;
using Shammy.Core.Connect;

namespace Shammy.Core.MeetingSystem;

/// <summary>
/// Reads Windows' privacy "recently used" records (ConsentStore\microphone, \webcam). Every app gets a
/// subkey with LastUsedTimeStart/LastUsedTimeStop FILETIMEs; Stop is zeroed while the device is open.
/// Desktop apps live under NonPackaged with their exe path ('\' written as '#').
/// </summary>
public static class CapabilityUsage
{
    public const string NonPackagedKey = "NonPackaged";

    public static bool IsInUse(long lastUsedStart, long lastUsedStop) => lastUsedStop == 0 && lastUsedStart > 0;

    /// <summary>
    /// How long an app's "in use" record is believed while that app has no live capture stream. An app left in the
    /// background (Windows Settings after its microphone test, any suspended or crashed app) can leave its record
    /// "in use" for hours; the live capture sessions say whether it's really listening.
    /// </summary>
    public static readonly TimeSpan TrustWithoutCapture = TimeSpan.FromSeconds(20);

    /// <summary>
    /// An app's "in use" record counts: the app itself is capturing sound now (or the streams can't be read, so
    /// <paramref name="capturingNow"/> is passed true), or it last captured, or its record started, within
    /// <see cref="TrustWithoutCapture"/> (whichever is later). Another app keeping the mic open (a noise filter, a
    /// voice chat) doesn't count. A time in the future (the clock was ahead when it was written) proves nothing.
    /// </summary>
    public static bool TrustRecord(bool capturingNow, DateTime? lastCaptureUtc, DateTime startedUtc, DateTime nowUtc)
    {
        if (capturingNow) return true;
        var since = lastCaptureUtc is { } last && last > startedUtc ? last : startedUtc;
        var age = nowUtc - since;
        return age >= TimeSpan.Zero && age <= TrustWithoutCapture;
    }

    public static string PathFromKey(string keyName) => keyName.Replace('#', '\\');

    public static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Human names for apps seen in the registry or the process list (exe names from <see cref="CircleMemory.AppNames"/>).</summary>
public static class AppNames
{
    static readonly Regex PackageFamily = new(@"^(?<name>[^\\_]+)_[a-z0-9]{13}$", RegexOptions.IgnoreCase);

    static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
        { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi" };

    static readonly Dictionary<string, string> Packages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MSTeams"] = "Teams",
        ["MicrosoftTeams"] = "Teams",
        ["Microsoft.SkypeApp"] = "Skype",
        ["5319275A.WhatsAppDesktop"] = "WhatsApp",
        ["Microsoft.WindowsCamera"] = "Camera",
        ["Microsoft.WindowsSoundRecorder"] = "Sound Recorder",
        ["Microsoft.ScreenSketch"] = "Snipping Tool",
        ["windows.immersivecontrolpanel"] = "Settings",
    };

    // Web apps that show their name in the tab (= window) title. Word-bounded and case-sensitive so
    // "Meeting notes" or "zoom in" don't count.
    static readonly (Regex Pattern, string Name)[] WebApps =
    {
        (new Regex(@"\bMeet\b"), "Google Meet"),
        (new Regex(@"\bZoom\b"), "Zoom"),
        (new Regex(@"\bTeams\b"), "Teams"),
        (new Regex(@"\bDiscord\b"), "Discord"),
        (new Regex(@"\bSlack\b"), "Slack"),
        (new Regex(@"\bWebex\b"), "Webex"),
        (new Regex(@"\bWhatsApp\b"), "WhatsApp"),
    };

    /// <summary>
    /// <paramref name="identity"/> is an exe path, an exe/process name or a package family name.
    /// For browsers, <paramref name="windowTitles"/> (foreground first) can reveal the web app in use.
    /// </summary>
    public static string Friendly(string identity, IEnumerable<string>? windowTitles = null, string? fileDescription = null)
    {
        identity = identity.Trim();
        if (identity.Length == 0) return "An app";

        var package = PackageFamily.Match(identity);
        if (package.Success && !identity.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var name = package.Groups["name"].Value;
            if (Packages.TryGetValue(name, out var known)) return known;
            var last = name[(name.LastIndexOf('.') + 1)..];
            if (last.StartsWith("Windows", StringComparison.Ordinal) && last.Length > 7) last = last[7..];

            return Regex.Replace(last, "(?<=[a-z])(?=[A-Z])", " ");
        }

        var stem = identity.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || identity.Contains('\\')
            ? Path.GetFileNameWithoutExtension(identity.Replace('/', '\\').Split('\\')[^1])
            : identity;

        if (Browsers.Contains(stem) && windowTitles is not null)
        {
            foreach (var title in windowTitles)
            {
                if (QuietRules.WebCallApp(title) is { } call) return call;
                foreach (var (pattern, webName) in WebApps)
                    if (pattern.IsMatch(title)) return webName;
            }
        }
        return CircleMemory.AppNames.Friendly(stem, fileDescription) ?? "An app";
    }
}
