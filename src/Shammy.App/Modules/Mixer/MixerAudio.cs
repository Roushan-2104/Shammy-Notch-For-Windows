using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Shammy.Core.Mixer;
using Shammy.SystemInfo;

namespace Shammy.Modules.Mixer;

/// <summary>One look at the sound: the output's own level, and every session on it.</summary>
/// <param name="MasterLevel">0–1; null when the output's volume can't be read (no Everything row).</param>
internal sealed record MixerReading(float? MasterLevel, bool MasterMuted, IReadOnlyList<MixerSource> Sources);

/// <summary>
/// Where the volume view gets its rows and sends its changes: Windows (<see cref="LiveMixer"/>) or stand-in
/// rows for tests (<see cref="FakeMixer"/>). Called on the UI thread; keys are <see cref="MixerBoard.KeyOf"/>'s,
/// plus <see cref="MixerBoard.EverythingKey"/> for the output itself.
/// </summary>
internal interface IMixerSource
{
    /// <summary>The view came on screen.</summary>
    void Start();

    /// <summary>The view is hidden: let go of everything.</summary>
    void Stop();

    /// <summary>Reads in the background and answers on the UI thread; null means there is no output.</summary>
    void Read(Action<MixerReading?> done);

    /// <summary>The same, waiting for the answer (the test pipe).</summary>
    MixerReading? ReadNow();

    void SetLevel(string key, float level);
    void SetMuted(string key, bool muted);
}

/// <summary>
/// App volumes from Core Audio. Windows' audio objects have to be made, used and let go on one thread, so they
/// all live on a worker that exists only between <see cref="Start"/> and <see cref="Stop"/>: every read and
/// every change is a job on its queue, and when the queue closes the worker releases what it holds and ends.
/// The output's own volume goes through the app's <see cref="VolumeService"/>, on the UI thread like its other
/// users.
/// </summary>
internal sealed class LiveMixer : IMixerSource
{
    readonly Dispatcher _ui;
    readonly Func<VolumeService?> _volume;
    readonly Action<string, Exception?> _warn;
    BlockingCollection<Action<Worker>>? _jobs;
    int _users;

    public LiveMixer(Dispatcher ui, Func<VolumeService?> volume, Action<string, Exception?> warn)
    {
        _ui = ui;
        _volume = volume;
        _warn = warn;
    }

    /// <summary>
    /// Counted, so a view that is let go a moment after its replacement came on screen doesn't take the worker
    /// away from it: the worker runs while anyone still needs it.
    /// </summary>
    public void Start()
    {
        if (_users++ > 0) return;
        var jobs = _jobs = new BlockingCollection<Action<Worker>>();
        var thread = new Thread(() => Run(jobs)) { IsBackground = true, Name = "Shammy app volumes" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public void Stop()
    {
        if (_users == 0 || --_users > 0) return;
        Shutdown();
    }

    /// <summary>Ends the worker whoever was using it (the app is closing).</summary>
    public void Shutdown()
    {
        _users = 0;
        var jobs = _jobs;
        _jobs = null;
        jobs?.CompleteAdding();
    }

    void Run(BlockingCollection<Action<Worker>> jobs)
    {
        var worker = new Worker(_warn);
        try
        {
            foreach (var job in jobs.GetConsumingEnumerable())
            {
                try { job(worker); }
                catch (Exception ex) { _warn("An app volume job failed", ex); }
            }
        }
        finally
        {
            worker.Release(everything: true);
            jobs.Dispose();
        }
    }

    bool Post(Action<Worker> job)
    {
        if (_jobs is not { IsAddingCompleted: false } jobs) return false;
        jobs.Add(job);
        return true;
    }

    public void Read(Action<MixerReading?> done)
    {
        if (!Post(w =>
            {
                var sources = w.Read();
                _ui.BeginInvoke(() => done(Reading(sources)));
            }))
            done(null);
    }

    public MixerReading? ReadNow()
    {
        // Nothing the worker does waits for the UI thread, so waiting for it here can't lock up.
        using var finished = new ManualResetEventSlim();
        IReadOnlyList<MixerSource>? sources = null;
        if (!Post(w =>
            {
                try { sources = w.Read(); }
                finally
                {
                    try { finished.Set(); }
                    catch (ObjectDisposedException) { }
                }
            }))
            return null;
        return finished.Wait(TimeSpan.FromSeconds(3)) ? Reading(sources) : null;
    }

    MixerReading? Reading(IReadOnlyList<MixerSource>? sources)
    {
        if (sources is null) return null;
        try
        {
            return _volume() is { } v ? new MixerReading(v.Level, v.Muted, sources) : new MixerReading(null, false, sources);
        }
        catch (Exception)
        {
            // The output went away between the two reads; the next look sorts it out.
            return new MixerReading(null, false, sources);
        }
    }

    public void SetLevel(string key, float level)
    {
        level = Math.Clamp(level, 0f, 1f);
        if (key != MixerBoard.EverythingKey)
        {
            Post(w => w.Set(key, level, null));
            return;
        }
        try { _volume()?.SetLevel(level); }
        catch (Exception ex) { _warn("Changing the volume failed", ex); }
    }

    public void SetMuted(string key, bool muted)
    {
        if (key != MixerBoard.EverythingKey)
        {
            Post(w => w.Set(key, null, muted));
            return;
        }
        try
        {
            // The service has one mute control, the mute key's toggle.
            if (_volume() is { } v && v.Muted != muted) v.Step(0);
        }
        catch (Exception ex) { _warn("Muting the sound failed", ex); }
    }

    /// <summary>Everything Core Audio; only ever touched on the worker thread.</summary>
    sealed class Worker
    {
        sealed record Facts(string? Name, string? Path, string? Description, string? Title);

        readonly Action<string, Exception?> _warn;
        readonly Dictionary<string, List<AudioSessionControl>> _sessions = new(StringComparer.Ordinal);
        readonly Dictionary<uint, Facts> _facts = new();
        MMDeviceEnumerator? _enumerator;
        MMDevice? _device;
        bool _warned;

        public Worker(Action<string, Exception?> warn) => _warn = warn;

        /// <summary>
        /// Every session on the default output, or null when there is no output. The sessions stay open until
        /// the next read, so a slider can change them in between.
        /// </summary>
        public IReadOnlyList<MixerSource>? Read()
        {
            Release(everything: false);
            try
            {
                _enumerator ??= new MMDeviceEnumerator();
                _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch (Exception)
            {
                // No speakers or headphones right now: a state, not a fault.
                return null;
            }

            var sources = new List<MixerSource>();
            var seen = new HashSet<uint>();
            try
            {
                var sessions = _device.AudioSessionManager.Sessions;
                int count = sessions?.Count ?? 0;
                for (int i = 0; i < count; i++)
                {
                    AudioSessionControl? session = null;
                    try
                    {
                        session = sessions![i];
                        var state = session.State;
                        if (state == AudioSessionState.AudioSessionStateExpired) continue;
                        bool system = session.IsSystemSoundsSession;
                        uint pid = system ? 0 : session.GetProcessID;
                        var facts = system ? null : FactsOf(pid);
                        if (!system) seen.Add(pid);
                        var source = new MixerSource
                        {
                            ProcessId = pid,
                            ProcessName = facts?.Name,
                            Description = facts?.Description,
                            WindowTitle = facts?.Title,
                            Path = facts?.Path,
                            IsSystemSounds = system,
                            Active = state == AudioSessionState.AudioSessionStateActive,
                            Level = session.SimpleAudioVolume.Volume,
                            Muted = session.SimpleAudioVolume.Mute,
                            Peak = Peak(session),
                        };
                        sources.Add(source);
                        string key = MixerBoard.KeyOf(source);
                        if (!_sessions.TryGetValue(key, out var list)) _sessions[key] = list = new List<AudioSessionControl>();
                        list.Add(session);
                        session = null;
                    }
                    catch (Exception)
                    {
                        // A session that ended while it was being read: it just isn't listed.
                    }
                    finally
                    {
                        Dispose(session);
                    }
                }
                _warned = false;
            }
            catch (Exception ex)
            {
                // Once per fault, not once a second.
                if (!_warned) _warn("Reading app volumes failed", ex);
                _warned = true;
            }

            // Process ids get reused: forget what was learnt about ones that are gone.
            foreach (var pid in _facts.Keys.Where(p => !seen.Contains(p)).ToList()) _facts.Remove(pid);
            return sources;
        }

        static float Peak(AudioSessionControl session)
        {
            try { return session.AudioMeterInformation?.MasterPeakValue ?? 0; }
            catch (Exception) { return 0; }
        }

        /// <summary>Changes every session of a row (all of an app's processes), level and/or mute.</summary>
        public void Set(string key, float? level, bool? muted)
        {
            // Changed before the first read (the pipe): look first.
            if (_device is null) Read();
            if (!_sessions.TryGetValue(key, out var list)) return;
            foreach (var session in list)
            {
                try
                {
                    if (level is { } l) session.SimpleAudioVolume.Volume = l;
                    if (muted is { } m) session.SimpleAudioVolume.Mute = m;
                }
                catch (Exception)
                {
                    // The app closed that stream a moment ago; its other sessions still get the change.
                }
            }
        }

        /// <summary>What a process is: read once and remembered while it keeps a session.</summary>
        Facts FactsOf(uint pid)
        {
            if (_facts.TryGetValue(pid, out var known)) return known;
            string? path = MixerNative.ProcessPath(pid), name = path is null ? null : Path.GetFileNameWithoutExtension(path);
            string? description = null, title = null;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                name ??= process.ProcessName;
                // Asking Shammy's own window for its title from here would wait on the UI thread.
                if (pid != (uint)Environment.ProcessId) title = process.MainWindowTitle;
            }
            catch (Exception)
            {
                // Gone already, or not ours to look at.
            }
            try
            {
                if (path is not null) description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            }
            catch (Exception)
            {
                // No version resource to read.
            }
            return _facts[pid] = new Facts(name, path, description, title);
        }

        /// <summary>Lets go of the last read's sessions and device; with <paramref name="everything"/>, of the lot.</summary>
        public void Release(bool everything)
        {
            foreach (var list in _sessions.Values)
                foreach (var session in list) Dispose(session);
            _sessions.Clear();
            AudioDeviceRelease.Dispose(_device);
            _device = null;
            if (!everything) return;
            _facts.Clear();
            try { _enumerator?.Dispose(); }
            catch (Exception) { }
            _enumerator = null;
        }

        static void Dispose(AudioSessionControl? session)
        {
            if (session is null) return;
            try
            {
                session.SimpleAudioVolume?.Dispose();
                session.Dispose();
            }
            catch (Exception)
            {
                // Its finalizer would only try the same again.
                GC.SuppressFinalize(session);
            }
        }
    }
}

/// <summary>
/// Stand-in rows for tests ("mixer-fake YouTube Music=80; Game=35,muted"): the view and the pipe behave as they
/// do with real apps, and nothing on the PC changes. Every row counts as playing, loudest first as written.
/// </summary>
internal sealed class FakeMixer : IMixerSource
{
    readonly List<MixerEntry> _apps;
    MixerEntry _master;

    public FakeMixer(IReadOnlyList<MixerEntry> entries)
    {
        _master = entries.FirstOrDefault(IsMaster) ?? new MixerEntry(MixerNames.Everything, 50, false);
        _apps = entries.Where(e => !IsMaster(e)).ToList();
    }

    static bool IsMaster(MixerEntry e) => e.Name.Equals(MixerNames.Everything, StringComparison.OrdinalIgnoreCase);

    static bool IsSystem(MixerEntry e) => e.Name.Equals(MixerNames.SystemSounds, StringComparison.OrdinalIgnoreCase);

    static MixerSource Source(MixerEntry e, int index) => new()
    {
        ProcessId = (uint)(90000 + index),
        ProcessName = IsSystem(e) ? null : e.Name,
        Description = IsSystem(e) ? null : e.Name,
        IsSystemSounds = IsSystem(e),
        Active = true,
        Level = e.Percent / 100f,
        Muted = e.Muted,
        Peak = Math.Max(0f, 1f - index * 0.1f),
    };

    public void Start() { }
    public void Stop() { }

    public void Read(Action<MixerReading?> done) => done(ReadNow());

    public MixerReading? ReadNow() => new(_master.Percent / 100f, _master.Muted, _apps.Select(Source).ToList());

    public void SetLevel(string key, float level) => Change(key, e => e with { Percent = MixerBoard.Percent(level) });

    public void SetMuted(string key, bool muted) => Change(key, e => e with { Muted = muted });

    void Change(string key, Func<MixerEntry, MixerEntry> change)
    {
        if (key == MixerBoard.EverythingKey)
        {
            _master = change(_master);
            return;
        }
        for (int i = 0; i < _apps.Count; i++)
            if (MixerBoard.KeyOf(Source(_apps[i], i)) == key) _apps[i] = change(_apps[i]);
    }
}

internal static class MixerNative
{
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

    /// <summary>A process's exe path; works for most processes Process.MainModule can't open. Null when it can't be read.</summary>
    public static string? ProcessPath(uint pid)
    {
        if (pid == 0) return null;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var name = new StringBuilder(1024);
            uint size = (uint)name.Capacity;
            return QueryFullProcessImageName(handle, 0, name, ref size) ? name.ToString(0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
