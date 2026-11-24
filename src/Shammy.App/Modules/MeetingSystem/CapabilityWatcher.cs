using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Threading;
using Shammy.Core.MeetingSystem;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace Shammy.Modules.MeetingSystem;

/// <param name="Source">"registry" (privacy usage record) or "audio" (live capture session).</param>
internal sealed record CapabilityApp(string Key, string? Path, string Name, string Source);

/// <param name="Screen">Apps capturing the screen through Windows' capture API (Teams, Snipping Tool, OBS...).</param>
internal sealed record CapabilitySnapshot(IReadOnlyList<CapabilityApp> Microphone, IReadOnlyList<CapabilityApp> Camera, IReadOnlyList<CapabilityApp> Screen)
{
    public static readonly CapabilitySnapshot Empty = new(Array.Empty<CapabilityApp>(), Array.Empty<CapabilityApp>(), Array.Empty<CapabilityApp>());

    public string Signature =>
        string.Join("|", Microphone.Select(a => $"{a.Key}={a.Name}")) + "#" + string.Join("|", Camera.Select(a => a.Key)) +
        "#" + string.Join("|", Screen.Select(a => a.Key));
}

/// <summary>
/// Which apps have the microphone / camera open right now. The primary source is the privacy
/// "recently used" records in the registry, watched with RegNotifyChangeKeyValue on a background
/// thread. Some Windows builds no longer write those records, so the microphone is also checked
/// through active WASAPI capture sessions every 2 s (a handful of COM calls); the camera has no such
/// fallback.
/// </summary>
internal sealed class CapabilityWatcher : IDisposable
{
    const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";
    const uint Filter = MeetingSystemNative.REG_NOTIFY_CHANGE_NAME | MeetingSystemNative.REG_NOTIFY_CHANGE_LAST_SET |
                        MeetingSystemNative.REG_NOTIFY_THREAD_AGNOSTIC;

    static readonly ConcurrentDictionary<string, string?> Descriptions = new(StringComparer.OrdinalIgnoreCase);

    readonly Dispatcher _ui;
    readonly Action<string, Exception?> _warn;
    readonly ManualResetEvent _stop = new(false);
    readonly Dictionary<string, string> _webAppNames = new(StringComparer.OrdinalIgnoreCase);
    Thread? _thread;
    string? _lastSignature;
    DateTime _sessionsRetryAt;
    /// <summary>When each app holding an "in use" record last had a live capture stream of its own (watcher thread).</summary>
    readonly ConcurrentDictionary<string, DateTime> _lastCapture = new(StringComparer.OrdinalIgnoreCase);

    public CapabilityWatcher(Dispatcher ui, Action<string, Exception?> warn)
    {
        _ui = ui;
        _warn = warn;
    }

    /// <summary>Raised on the UI thread whenever the set of apps (or their names) changes.</summary>
    public event Action<CapabilitySnapshot>? Changed;

    public bool UsingNotifications { get; private set; }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Shammy meeting watcher", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    void Run()
    {
        RegistryKey? mic = null, cam = null;
        using var micChanged = new AutoResetEvent(false);
        using var camChanged = new AutoResetEvent(false);
        try
        {
            mic = Registry.CurrentUser.OpenSubKey(Root + "microphone");
            cam = Registry.CurrentUser.OpenSubKey(Root + "webcam");
            UsingNotifications = Arm(mic, micChanged);
            Arm(cam, camChanged);
            Publish(Scan());

            var handles = new WaitHandle[] { _stop, micChanged, camChanged };
            while (true)
            {
                int i = WaitHandle.WaitAny(handles, TimeSpan.FromSeconds(2));
                if (i == 0) return;
                if (i == 1) UsingNotifications = Arm(mic, micChanged);
                if (i == 2) Arm(cam, camChanged);
                // Start and Stop are written separately; let the burst settle before reading.
                if (i is 1 or 2 && _stop.WaitOne(150)) return;
                Publish(Scan());
            }
        }
        catch (Exception ex)
        {
            _warn("Microphone watcher stopped", ex);
        }
        finally
        {
            mic?.Dispose();
            cam?.Dispose();
        }
    }

    bool Arm(RegistryKey? key, AutoResetEvent changed)
    {
        if (key is null) return false;
        try
        {
            return MeetingSystemNative.RegNotifyChangeKeyValue(key.Handle, true, Filter, changed.SafeWaitHandle, true) == 0;
        }
        catch (Exception ex)
        {
            _warn("Registry notifications unavailable", ex);
            return false;
        }
    }

    void Publish(CapabilitySnapshot snapshot)
    {
        snapshot = KeepWebAppNames(snapshot);
        var signature = snapshot.Signature;
        if (signature == _lastSignature) return;
        _lastSignature = signature;
        _ui.BeginInvoke(() => Changed?.Invoke(snapshot));
    }

    /// <summary>
    /// Once a browser was seen hosting "Google Meet", keep that name for as long as it holds the mic,
    /// even after the user switches tabs and the window title says something else.
    /// </summary>
    CapabilitySnapshot KeepWebAppNames(CapabilitySnapshot snapshot)
    {
        var live = snapshot.Microphone.Select(a => a.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _webAppNames.Keys.Where(k => !live.Contains(k)).ToList()) _webAppNames.Remove(key);
        var mic = snapshot.Microphone.Select(a =>
        {
            if (a.Path is null) return a;
            var plain = AppNames.Friendly(a.Path, null, Descriptions.GetValueOrDefault(a.Path));
            if (a.Name != plain)
            {
                _webAppNames[a.Key] = a.Name;
                return a;
            }
            return _webAppNames.TryGetValue(a.Key, out var name) ? a with { Name = name } : a;
        }).ToList();
        return snapshot with { Microphone = mic };
    }

    /// <summary>Reads the current state now. Safe from any thread.</summary>
    public CapabilitySnapshot Scan()
    {
        try
        {
            var titles = new Lazy<List<(uint Pid, string Title)>>(VisibleWindowTitles);
            var capturing = Capturing(titles, out bool sessionsRead, out var owners);
            var now = DateTime.UtcNow;
            // A record left "in use" by an app that isn't capturing (Windows Settings in the background after its mic
            // test, a suspended app) is stale, not a call; one whose app is capturing, or did just now, counts.
            bool Trust(string key, string? path, DateTime started)
            {
                bool live = !sessionsRead || owners.Contains(key) || (path is not null && owners.Contains(path));
                if (live) _lastCapture[key] = now;
                return CapabilityUsage.TrustRecord(live, _lastCapture.TryGetValue(key, out var last) && last > started ? last : started, now);
            }
            var mic = Registered("microphone", titles, Trust);
            foreach (var key in _lastCapture.Keys.Where(k => !mic.Any(m => m.Key == k)).ToList()) _lastCapture.TryRemove(key, out _);
            foreach (var app in capturing)
                if (!mic.Any(m => CapabilityUsage.SamePath(m.Path, app.Path))) mic.Add(app);
            // Screen capture has no change notification here; the 2 s poll picks it up.
            var screen = Registered("graphicsCaptureProgrammatic", titles, null);
            foreach (var app in Registered("graphicsCaptureWithoutBorder", titles, null))
                if (!screen.Any(s => s.Key == app.Key)) screen.Add(app);
            // Browsers and most meeting apps capture without touching those keys, but show their own "sharing" bar.
            foreach (var (_, title) in titles.Value)
                if (Shammy.Core.Connect.ScreenShareWindows.Sharer(title) is { } sharer && !screen.Any(s => s.Name == sharer))
                    screen.Add(new CapabilityApp("share:" + sharer, null, sharer, "window"));
            return new CapabilitySnapshot(mic, Registered("webcam", titles, null), screen);
        }
        catch (Exception ex)
        {
            _warn("Reading microphone usage failed", ex);
            return CapabilitySnapshot.Empty;
        }
    }

    /// <param name="trust">For the microphone: whether a record still counts (its key, its exe path, when it started);
    /// null trusts every record of a running app.</param>
    static List<CapabilityApp> Registered(string capability, Lazy<List<(uint Pid, string Title)>> titles, Func<string, string?, DateTime, bool>? trust)
    {
        var apps = new List<CapabilityApp>();
        using var root = Registry.CurrentUser.OpenSubKey(Root + capability);
        if (root is null) return apps;
        var own = Environment.ProcessPath;
        HashSet<string>? packaged = null;
        foreach (var name in root.GetSubKeyNames())
        {
            if (name == CapabilityUsage.NonPackagedKey)
            {
                using var desktop = root.OpenSubKey(name);
                if (desktop is null) continue;
                foreach (var exeKey in desktop.GetSubKeyNames())
                {
                    using var k = desktop.OpenSubKey(exeKey);
                    if (!IsInUse(k)) continue;
                    var path = CapabilityUsage.PathFromKey(exeKey);
                    // Shammy itself records the mic for voice search; that is not a meeting.
                    if (CapabilityUsage.SamePath(path, own)) continue;
                    if (trust is not null && !trust(exeKey, path, Started(k))) continue;
                    var app = Desktop(exeKey, path, titles, "registry", requireRunning: true);
                    if (app is not null) apps.Add(app);
                }
            }
            else
            {
                using var k = root.OpenSubKey(name);
                if (!IsInUse(k)) continue;
                if (trust is not null && !trust(name, null, Started(k))) continue;
                // Same rule as desktop apps: a crash leaves the record "in use" forever, so only trust it
                // while the app is running (otherwise the mic pill and hidden notifications never go away).
                packaged ??= MeetingSystemNative.RunningPackageFamilies();
                if (packaged.Contains(name)) apps.Add(new CapabilityApp(name, null, AppNames.Friendly(name), "registry"));
            }
        }
        return apps;
    }

    /// <summary>When the record says the device was opened (UTC; the distant past if it can't say).</summary>
    static DateTime Started(RegistryKey? k)
    {
        try { return k?.GetValue("LastUsedTimeStart") is long s && s > 0 ? DateTime.FromFileTimeUtc(s) : DateTime.MinValue; }
        catch (ArgumentOutOfRangeException) { return DateTime.MinValue; }
    }

    static bool IsInUse(RegistryKey? k) =>
        k is not null && CapabilityUsage.IsInUse(k.GetValue("LastUsedTimeStart") is long s ? s : 0, k.GetValue("LastUsedTimeStop") is long e ? e : 0);

    /// <summary>
    /// Apps with an active capture stream on any microphone. <paramref name="read"/> is false when no device's
    /// streams could be read; <paramref name="owners"/> holds each other capturing process's exe path and package
    /// family, so a privacy record can be matched to an app that's really listening.
    /// </summary>
    List<CapabilityApp> Capturing(Lazy<List<(uint Pid, string Title)>> titles, out bool read, out HashSet<string> owners)
    {
        var apps = new List<CapabilityApp>();
        read = false;
        owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // After a failure (the audio service restarting during a driver update) try again a minute later
        // rather than giving up on mic detection for the rest of the session.
        if (DateTime.UtcNow < _sessionsRetryAt) return apps;
        var pids = new HashSet<uint>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                try
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    read = true;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        if (session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive && !session.IsSystemSoundsSession)
                            pids.Add(session.GetProcessID);
                    }
                }
                catch (Exception)
                {
                    // A device unplugged mid-scan; the next scan sees the new list.
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _sessionsRetryAt = DateTime.UtcNow.AddMinutes(1);
            _warn("Audio capture sessions unavailable", ex);
            // Half-read streams prove nothing: the records are trusted until they can be read again.
            read = false;
            return apps;
        }

        // Shammy's own listening (voice) says nothing about other apps.
        pids.Remove((uint)Environment.ProcessId);
        foreach (var pid in pids)
        {
            var path = MeetingSystemNative.ProcessPath(pid);
            if (path is not null) owners.Add(path);
            if (MeetingSystemNative.PackageFamily(pid) is { } family) owners.Add(family);
            if (path is null || apps.Any(a => CapabilityUsage.SamePath(a.Path, path))) continue;
            var app = Desktop(path, path, titles, "audio", requireRunning: false);
            if (app is not null) apps.Add(app);
        }
        return apps;
    }

    static CapabilityApp? Desktop(string key, string path, Lazy<List<(uint Pid, string Title)>> titles, string source, bool requireRunning)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName(stem))
        {
            pids.Add((uint)p.Id);
            p.Dispose();
        }
        // A crash leaves Stop at zero forever; only trust the record while the app is running.
        if (requireRunning && pids.Count == 0) return null;

        var description = Descriptions.GetOrAdd(path, static p =>
        {
            try { return File.Exists(p) ? FileVersionInfo.GetVersionInfo(p).FileDescription : null; }
            catch (Exception) { return null; }
        });
        var appTitles = titles.Value.Where(t => pids.Contains(t.Pid)).Select(t => t.Title);
        return new CapabilityApp(key, path, AppNames.Friendly(path, appTitles, description), source);
    }

    /// <summary>Titles of visible top-level windows, the foreground one first.</summary>
    static List<(uint Pid, string Title)> VisibleWindowTitles()
    {
        var list = new List<(uint, string)>();
        var foreground = Interop.FrontWindow.Handle;
        MeetingSystemNative.EnumWindows((hwnd, _) =>
        {
            if (!MeetingSystemNative.IsWindowVisible(hwnd)) return true;
            var title = Interop.FrontWindow.TitleOf(hwnd);
            if (title.Length == 0) return true;
            MeetingSystemNative.GetWindowThreadProcessId(hwnd, out var pid);
            if (hwnd == foreground) list.Insert(0, (pid, title));
            else list.Add((pid, title));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public void Dispose()
    {
        _stop.Set();
        _thread?.Join(500);
    }
}
