using System.Net.Http;
using System.Windows.Threading;
using Shammy.Core.Updates;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Shammy.Infrastructure;

/// <summary>
/// Automatic updates from the GitHub Releases page, via Velopack. Checks shortly after start and then
/// every few hours, downloads quietly in the background (usually just a small delta), then offers a
/// restart. An update nobody clicks on is applied the next time Shammy exits. Copies that weren't
/// installed with Setup (e.g. a dev build) never update themselves.
/// A check has time limits, so it always ends: a stalled connection reports a failure and tries again soon.
/// </summary>
internal sealed class UpdateService : IDisposable
{
    const string RepoUrl = "https://github.com/Roushan-2104/Shammy-Notch-For-Windows";
    static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(45);
    // Generous: on a slow connection one small request to GitHub can take over 20 s, and a check makes a few.
    static readonly TimeSpan CheckLimit = TimeSpan.FromMinutes(2);

    /// <summary>A download may take a while on a slow line, but it has to keep moving.</summary>
    static readonly TimeSpan DownloadStall = TestSeconds("SHAMMY_UPDATE_STALL_SECONDS") ?? TimeSpan.FromMinutes(3);

    readonly Dispatcher _ui;
    readonly UpdateManager _manager;
    readonly DispatcherTimer _timer;
    readonly string _source;
    VelopackAsset? _ready;

    public UpdateService(Dispatcher ui)
    {
        _ui = ui;
        // Test builds can point at a local folder of packages to rehearse an update end to end, or at a source that misbehaves.
        var testFeed = CommandServer.IsEnabled ? Environment.GetEnvironmentVariable("SHAMMY_UPDATE_SOURCE") : null;
        var testFault = CommandServer.IsEnabled ? Environment.GetEnvironmentVariable("SHAMMY_UPDATE_FAULT") : null;
        IUpdateSource source = Directory.Exists(testFeed)
            ? new SimpleFileSource(new DirectoryInfo(testFeed))
            : new GithubSource(RepoUrl, null, false, new FastDownloader());
        _source = source is SimpleFileSource ? "local test feed" : "GitHub";
        if (!string.IsNullOrEmpty(testFault)) source = new FaultySource(source, testFault);
        _manager = new UpdateManager(source);
        _timer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = FirstCheck };
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = UpdateText.NormalInterval;
            _ = CheckAsync();
        };
    }

    /// <summary>Raised on the UI thread with the new version once it's downloaded and ready.</summary>
    public event Action<string>? Ready;

    /// <summary>Raised on the UI thread whenever <see cref="LastResult"/> changes.</summary>
    public event Action? Changed;

    public bool IsInstalled => _manager.IsInstalled;
    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "?";
    public string? ReadyVersion => _ready?.Version.ToString();

    /// <summary>Where the checker stands, in <see cref="UpdateText"/>'s words.</summary>
    public string LastResult { get; private set; } = UpdateText.NotChecked;

    public void Start()
    {
        if (!IsInstalled)
        {
            LastResult = UpdateText.NotInstalled;
            Log.Info("Updates off: this copy wasn't installed with Setup");
            return;
        }
        Log.Info($"Updates on: version {CurrentVersion}, checking {_source}");
        _timer.Start();
    }

    /// <summary>
    /// Checks now; returns a short description of what happened. A check already running (the background
    /// one) is shared, so "Check for updates" never reports a stale "up to date" while downloading.
    /// </summary>
    public Task<string> CheckAsync()
    {
        if (!IsInstalled) return Task.FromResult(LastResult);
        lock (_gate)
        {
            if (_inFlight is { IsCompleted: false } running) return running;
            return _inFlight = Task.Run(CheckCoreAsync);
        }
    }

    readonly object _gate = new();
    Task<string>? _inFlight;

    async Task<string> CheckCoreAsync()
    {
        if (_ready is not null) return Report(UpdateText.Ready(_ready.Version.ToString()));
        Report(UpdateText.Checking);
        string result;
        try
        {
            // Velopack's own request timeout is many minutes, and this call takes no cancellation token.
            var info = await _manager.CheckForUpdatesAsync().WaitAsync(CheckLimit);
            result = info is null ? UpdateText.UpToDate : await DownloadAsync(info);
        }
        catch (TimeoutException)
        {
            Log.Warn($"Update check gave up after {CheckLimit.TotalSeconds:0} s");
            result = UpdateText.Failed("timed out");
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"Update download stalled for {DownloadStall.TotalSeconds:0} s");
            result = UpdateText.Failed("download stalled");
        }
        catch (Exception ex)
        {
            // Offline, GitHub rate limits, a release mid-upload.
            Log.Warn("Update check failed", ex);
            result = UpdateText.Failed(ex.GetType().Name);
        }
        Report(result);
        _ = _ui.BeginInvoke(() =>
        {
            if (_timer.IsEnabled) _timer.Interval = UpdateText.NextCheck(result);
        });
        return result;
    }

    async Task<string> DownloadAsync(UpdateInfo info)
    {
        var version = info.TargetFullRelease.Version.ToString();
        Log.Info($"Update {version} found; downloading");
        Report(UpdateText.Downloading(version, 0));
        using var stall = new CancellationTokenSource(DownloadStall);
        bool finished = false;
        try
        {
            await _manager.DownloadUpdatesAsync(info, percent =>
            {
                if (Volatile.Read(ref finished)) return;
                stall.CancelAfter(DownloadStall);
                Report(UpdateText.Downloading(version, percent));
            }, stall.Token);
        }
        finally
        {
            Volatile.Write(ref finished, true);
        }
        _ready = info.TargetFullRelease;
        Log.Info($"Update {version} downloaded");
        _ = _ui.BeginInvoke(() => Ready?.Invoke(version));
        return UpdateText.Ready(version);
    }

    string Report(string result)
    {
        if (result == LastResult) return result;
        LastResult = result;
        _ = _ui.BeginInvoke(() => Changed?.Invoke());
        return result;
    }

    /// <summary>Restart into the downloaded update now. The caller shuts the app down right after.</summary>
    public bool RestartIntoUpdate()
    {
        if (_ready is null) return false;
        Log.Info($"Restarting into update {_ready.Version}");
        _manager.WaitExitThenApplyUpdates(_ready, silent: true, restart: true);
        _ready = null;
        return true;
    }

    public void Dispose()
    {
        _timer.Stop();
        if (_ready is null) return;
        // Nobody clicked "restart": apply it now that Shammy is exiting anyway.
        try { _manager.WaitExitThenApplyUpdates(_ready, silent: true, restart: false); }
        catch (Exception ex) { Log.Warn("Applying the update on exit failed", ex); }
    }

    static TimeSpan? TestSeconds(string variable) =>
        CommandServer.IsEnabled && int.TryParse(Environment.GetEnvironmentVariable(variable), out var seconds) ? TimeSpan.FromSeconds(seconds) : null;

    /// <summary>Test pipe only (SHAMMY_UPDATE_FAULT): "hang" never answers, "fail" errors, "stall" never finishes a download.</summary>
    sealed class FaultySource(IUpdateSource inner, string fault) : IUpdateSource
    {
        public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        {
            if (fault == "hang") await Task.Delay(Timeout.Infinite);
            if (fault == "fail") throw new HttpRequestException("test failure");
            return await inner.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease);
        }

        public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
        {
            if (fault == "stall") await Task.Delay(Timeout.Infinite, cancelToken);
            await inner.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken);
        }
    }
}
