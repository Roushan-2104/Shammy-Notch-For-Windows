using System.Globalization;
using System.Windows.Threading;
using Shammy.Core.Quick;
using Shammy.Modules.Quick;

namespace Shammy.Modules.Health;

/// <summary>
/// What the Health card keeps: the day its line was last seen, the battery's wear read that day, the wear last
/// told (and the day it was), and how long the daily line is hidden.
/// </summary>
internal sealed class HealthStore
{
    public string? SeenDay { get; set; }
    public string? BatteryDay { get; set; }
    public long DesignMWh { get; set; }
    public long FullMWh { get; set; }
    public int? BatteryToldAt { get; set; }
    public string? BatteryToldDay { get; set; }
    public DateTime? HiddenUntil { get; set; }
}

/// <summary>
/// The laptop's health: drives and their free space, a cleanup of temporary files, the Recycle Bin and old
/// downloads (each one asks "Sure?" first; downloads go to the Recycle Bin, a whole file or folder at a time), and
/// a daily check: battery wear, days since the last restart, startup apps, updates waiting on a restart. Once a
/// day the check's one line shows at the top of the open notch until the card has been opened; it shows every day
/// while something needs attention (a nearly full drive, updates waiting), and a worn battery is told once, not
/// daily. The line can be hidden for a week. The card offers a restart (asked twice) when updates wait or the PC
/// hasn't restarted in a week. "health", "clean up my pc", "storage" or "disk space" open it.
/// Pipes: health-state, health-scan, health-measure, health-dry, health-clean, health-hide.
/// </summary>
internal sealed class HealthModule : IShammyModule
{
    public const string CardId = "health";
    const string Glyph = "\uE9D9", PowerGlyph = "\uE7E8";
    static readonly TimeSpan FirstScan = TimeSpan.FromSeconds(20), ScanEvery = TimeSpan.FromHours(6);
    static readonly TimeSpan MeasureAgainAfter = TimeSpan.FromSeconds(60), ResultShown = TimeSpan.FromMinutes(30);
    static readonly TimeSpan RestartDelay = TimeSpan.FromMilliseconds(600);

    ModuleContext _ctx = null!;
    HealthStore _store = new();
    DispatcherTimer? _scan;
    HealthView? _view;
    bool _dry, _scanning, _measuring;
    DateTime _measuredAt = DateTime.MinValue;
    string _last = "-";

    public string Name => "health";

    /// <summary>The last check; null before the first one.</summary>
    public HealthReport? Report { get; private set; }
    public IReadOnlyList<DriveSpace> Drives { get; private set; } = Array.Empty<DriveSpace>();

    /// <summary>What each cleanup would free (null while measuring or not yet measured).</summary>
    public Dictionary<CleanupKind, (long Bytes, int Files)?> Sizes { get; } = new();

    /// <summary>What each cleanup last did, in words, kept here so a redrawn card still shows it.</summary>
    readonly Dictionary<CleanupKind, (string Text, bool Failed, DateTime At)> _results = new();

    /// <summary>Cleanups running now.</summary>
    readonly HashSet<CleanupKind> _working = new();

    /// <summary>Something new to show: the card redraws.</summary>
    public event Action? Changed;

    static string Today => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        _store = ctx.Load<HealthStore>();
        ctx.AddCard(new NotchCard
        {
            Id = CardId,
            Title = "Health",
            Width = 176,
            Order = 91,
            Chip = true,
            IsRelevant = () => false,
            Compact = HealthView.BuildCompact(() => ctx.OpenCard(CardId)),
            Detail = () => _view = new HealthView(this),
        });
        ctx.AddGlance("health", Glance);
        // Fixed phrases only, ahead of the launcher so "storage" isn't an app search.
        ctx.AddCommandFirst(new OpenCommand(this));
        _scan = new DispatcherTimer(FirstScan, DispatcherPriority.Background, (_, _) =>
        {
            _scan!.Interval = ScanEvery;
            _ = ScanAsync();
        }, ctx.Ui);
        _scan.Start();

        ctx.AddPipeCommand("health-state", _ => State());
        ctx.AddPipeCommand("health-scan", arg =>
        {
            _ = ScanAsync();
            return "started";
        });
        ctx.AddPipeCommand("health-measure", arg =>
        {
            _ = MeasureAsync(force: true);
            return "started";
        });
        ctx.AddPipeCommand("health-dry", arg =>
        {
            _dry = arg.Trim().ToLowerInvariant() is not ("off" or "false" or "0");
            return "ok dry=" + (_dry ? "on" : "off");
        });
        // Only ever a dry run from the pipe, and answered without waiting on the UI thread it runs on.
        ctx.AddPipeCommand("health-clean", arg => Enum.TryParse<CleanupKind>(arg.Trim(), true, out var kind)
            ? (_dry ? WouldClean(kind) : "error: only in a dry run from the pipe")
            : "error: health-clean TempFiles|RecycleBin|OldDownloads");
        ctx.AddPipeCommand("health-hide", arg =>
        {
            if (arg.Trim().ToLowerInvariant() is "off" or "show")
            {
                _store.HiddenUntil = null;
                _ctx.Save(_store);
                return "ok shown";
            }
            HideForAWeek();
            return "ok hidden until " + _store.HiddenUntil?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        });
    }

    public void Dispose() => _scan?.Stop();

    // ---- The daily check ------------------------------------------------------------------------------

    internal async Task ScanAsync()
    {
        if (_scanning) return;
        _scanning = true;
        try
        {
            var drives = await Task.Run(HealthScan.Drives);
            var (startup, updates) = await Task.Run(() => (HealthScan.StartupApps(), HealthScan.RestartForUpdates()));
            // The battery report takes a second or two: once a day is plenty.
            if (_store.BatteryDay != Today && await HealthScan.BatteryAsync() is var wear)
            {
                _store.BatteryDay = Today;
                (_store.DesignMWh, _store.FullMWh) = wear is null ? (0, 0) : (wear.DesignMWh, wear.FullMWh);
                _ctx.Save(_store);
            }
            var battery = _store.DesignMWh > 0 ? new BatteryWear(_store.DesignMWh, _store.FullMWh) : null;
            // Wear doesn't heal: it is told the day it is first seen (and again only if it gets much worse).
            if (battery is { } b && HealthText.BatteryNews(b.Percent, _store.BatteryToldAt))
            {
                _store.BatteryToldAt = b.Percent;
                _store.BatteryToldDay = Today;
                _ctx.Save(_store);
            }
            var root = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\');
            Drives = drives;
            Report = new HealthReport(drives.FirstOrDefault(d => string.Equals(d.Name, root, StringComparison.OrdinalIgnoreCase)),
                HealthScan.Uptime, startup, battery, updates);
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _ctx.Warn("The health check failed", ex);
        }
        finally
        {
            _scanning = false;
        }
    }

    bool BatteryNewsToday => _store.BatteryToldDay == Today;

    /// <summary>Whether the check found something that keeps its line showing every day.</summary>
    internal bool NeedsAttention => Report is { } r && HealthText.NeedsAttention(r, BatteryNewsToday);

    /// <summary>The line is hidden for a while (the card's "Hide for a week").</summary>
    internal bool LineHidden => HealthText.Hidden(DateTime.Now, _store.HiddenUntil);

    /// <summary>Whether the card offers a restart: updates are waiting, or it's been a week since the last one.</summary>
    internal bool OffersRestart => Report is { } r && (r.RestartForUpdates || HealthText.LongUptime(r.Uptime));

    GlanceItem? Glance()
    {
        if (Report is not { } r || LineHidden) return null;
        bool attention = NeedsAttention;
        if (!attention && _store.SeenDay == Today) return null;
        return new GlanceItem(HealthText.Line(r), attention ? NotchPalette.Alarm : NotchPalette.Battery, attention ? 60 : 25, () => _ctx.OpenCard(CardId));
    }

    /// <summary>Hides the daily line for a week.</summary>
    internal void HideForAWeek()
    {
        _store.HiddenUntil = DateTime.Now + HealthText.HideFor;
        _ctx.Save(_store);
        _ctx.Info("Health line hidden for a week");
        Changed?.Invoke();
    }

    /// <summary>The card opened: today's line has been seen; measure what cleanup would free.</summary>
    internal void Opened()
    {
        if (_store.SeenDay != Today)
        {
            _store.SeenDay = Today;
            _ctx.Save(_store);
        }
        if (Report is null) _ = ScanAsync();
        _ = MeasureAsync();
    }

    // ---- Restart -----------------------------------------------------------------------------------------

    /// <summary>Restarts the PC (the card has already asked twice), as the Laptop card does. A dry run only says so.</summary>
    internal string Restart()
    {
        if (_dry) return _last = "would restart";
        _ctx.Notch.Collapse();
        var timer = new DispatcherTimer(RestartDelay, DispatcherPriority.Normal, (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            if (!LaptopSwitches.PowerOff(restart: true)) _ctx.ShowMessage(PowerGlyph, "Couldn’t restart", error: true);
        }, _ctx.Ui);
        timer.Start();
        return _last = "restart";
    }

    // ---- Cleanup -----------------------------------------------------------------------------------------

    /// <summary>
    /// Measures what each cleanup would free. Not while a measurement runs, and not again within a minute of the
    /// last one unless <paramref name="force"/>d (a cleanup re-measures its own kind).
    /// </summary>
    internal async Task MeasureAsync(bool force = false)
    {
        if (_measuring || !force && DateTime.UtcNow - _measuredAt < MeasureAgainAfter) return;
        _measuring = true;
        try
        {
            foreach (var kind in Enum.GetValues<CleanupKind>()) Sizes[kind] = null;
            Changed?.Invoke();
            foreach (var kind in Enum.GetValues<CleanupKind>())
            {
                var k = kind;
                Sizes[k] = await Task.Run(() => HealthScan.Measure(k));
                Changed?.Invoke();
            }
            _measuredAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _ctx.Warn("Measuring the cleanup failed", ex);
        }
        finally
        {
            _measuring = false;
        }
    }

    internal bool IsWorking(CleanupKind kind) => _working.Contains(kind);

    /// <summary>What the last cleanup of this kind did (for the last half hour); null when nothing to say.</summary>
    internal (string Text, bool Failed)? Result(CleanupKind kind) =>
        _results.TryGetValue(kind, out var r) && DateTime.UtcNow - r.At < ResultShown ? (r.Text, r.Failed) : null;

    /// <summary>Cleans one kind (the card has already asked "Sure?"); the result is kept for the card to show.</summary>
    internal async Task CleanAsync(CleanupKind kind)
    {
        if (!_working.Add(kind)) return;
        Changed?.Invoke();
        try
        {
            if (_dry)
            {
                _results[kind] = ("Test mode: " + WouldClean(kind), false, DateTime.UtcNow);
                return;
            }
            var done = await Task.Run(() => HealthScan.Clean(kind));
            _ctx.Info($"Cleanup {kind}: {done.Done} done, {done.Failed} not, {done.Bytes} bytes");
            _last = $"{kind}: {StorageText.Size(done.Bytes)} done={done.Done} failed={done.Failed}";
            _results[kind] = (Cleanup.Said(kind, done), Cleanup.Failed(done), DateTime.UtcNow);
            Sizes[kind] = await Task.Run(() => HealthScan.Measure(kind));
            Drives = await Task.Run(HealthScan.Drives);
        }
        catch (Exception ex)
        {
            _ctx.Warn($"Cleanup {kind} failed", ex);
            _results[kind] = ("Something went wrong; nothing more was cleaned", true, DateTime.UtcNow);
        }
        finally
        {
            _working.Remove(kind);
            Changed?.Invoke();
        }
    }

    /// <summary>A dry run, answered at once (no waiting on another thread): what a cleanup would do.</summary>
    string WouldClean(CleanupKind kind)
    {
        var (bytes, files) = Sizes.TryGetValue(kind, out var s) && s is { } known ? known : HealthScan.Measure(kind);
        return _last = $"would {Cleanup.Verb(kind).ToLowerInvariant()} {kind}: {StorageText.Size(bytes)} in {Cleanup.Count(kind, files)}";
    }

    string State()
    {
        string sizes = string.Join(" ", Enum.GetValues<CleanupKind>().Select(k =>
            $"{k}={(Sizes.TryGetValue(k, out var s) ? s is { } v ? $"{StorageText.Size(v.Bytes)}/{v.Files}" : "measuring" : "-")}"));
        string results = string.Join(" ", Enum.GetValues<CleanupKind>().Select(k => Result(k) is { } r ? $"{k}:'{r.Text}'" : null).Where(t => t is not null));
        return $"report={(Report is { } r ? "'" + HealthText.Line(r) + "'" : "none")} attention={NeedsAttention} batteryNews={BatteryNewsToday} " +
               $"seen={_store.SeenDay ?? "-"} hidden={(LineHidden ? _store.HiddenUntil?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "no")} " +
               $"glance={(Glance() is { } g ? "'" + g.Text + "'" : "none")} restart={OffersRestart} drives={Drives.Count} {sizes} " +
               $"measuring={_measuring} results=[{results}] dry={(_dry ? "on" : "off")} last='{_last}' card={(_view is { IsVisible: true } ? "open" : "closed")}";
    }

    /// <summary>"health", "open health", "clean up my pc", "storage", "disk space": opens the card.</summary>
    sealed class OpenCommand(HealthModule module) : ITextCommand, ICommandPreview
    {
        public bool CanHandle(string text) => HealthPhrases.IsOpen(text);

        public Task<CommandResult> RunAsync(string text)
        {
            // Opens once this confirmation has closed.
            module._ctx.OpenCard(CardId);
            return Task.FromResult(new CommandResult(Glyph, "Health", "Storage, cleanup and checks"));
        }

        public CommandPreview? Preview(string text) => new(Glyph, "Open Health", "Storage, cleanup and checks");
    }
}
