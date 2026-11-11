using System.Globalization;
using System.Windows.Threading;
using Shammy.Core.Quick;

namespace Shammy.Modules.Health;

/// <summary>What the Health card keeps: the day its line was last seen, and the battery's wear read that day.</summary>
internal sealed class HealthStore
{
    public string? SeenDay { get; set; }
    public string? BatteryDay { get; set; }
    public long DesignMWh { get; set; }
    public long FullMWh { get; set; }
}

/// <summary>
/// The laptop's health: drives and their free space, a cleanup of temporary files, the Recycle Bin and old
/// downloads (each one asks "Sure?" first; downloads go to the Recycle Bin), and a daily check: battery wear,
/// days since the last restart, startup apps, updates waiting on a restart. Once a day the check's one line shows
/// at the top of the open notch until the card has been opened; it shows every day while something needs
/// attention (a nearly full drive, updates waiting, a worn battery).
/// Pipes: health-state, health-scan, health-measure, health-dry, health-clean.
/// </summary>
internal sealed class HealthModule : IShammyModule
{
    public const string CardId = "health";
    static readonly TimeSpan FirstScan = TimeSpan.FromSeconds(20), ScanEvery = TimeSpan.FromHours(6);

    ModuleContext _ctx = null!;
    HealthStore _store = new();
    DispatcherTimer? _scan;
    HealthView? _view;
    bool _dry, _scanning;
    string _last = "-";

    public string Name => "health";

    /// <summary>The last check; null before the first one.</summary>
    public HealthReport? Report { get; private set; }
    public IReadOnlyList<DriveSpace> Drives { get; private set; } = Array.Empty<DriveSpace>();

    /// <summary>What each cleanup would free (null while measuring or not yet measured).</summary>
    public Dictionary<CleanupKind, (long Bytes, int Files)?> Sizes { get; } = new();

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
            _ = MeasureAsync();
            return "started";
        });
        ctx.AddPipeCommand("health-dry", arg =>
        {
            _dry = arg.Trim().ToLowerInvariant() is not ("off" or "false" or "0");
            return "ok dry=" + (_dry ? "on" : "off");
        });
        ctx.AddPipeCommand("health-clean", arg => Enum.TryParse<CleanupKind>(arg.Trim(), true, out var kind)
            ? (_dry ? CleanAsync(kind).GetAwaiter().GetResult() : "error: only in a dry run from the pipe")
            : "error: health-clean TempFiles|RecycleBin|OldDownloads");
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

    GlanceItem? Glance()
    {
        if (Report is not { } r) return null;
        bool attention = HealthText.NeedsAttention(r);
        if (!attention && _store.SeenDay == Today) return null;
        return new GlanceItem(HealthText.Line(r), attention ? NotchPalette.Alarm : NotchPalette.Battery, attention ? 60 : 25, () => _ctx.OpenCard(CardId));
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

    // ---- Cleanup -----------------------------------------------------------------------------------------

    internal async Task MeasureAsync()
    {
        foreach (var kind in Enum.GetValues<CleanupKind>())
        {
            Sizes[kind] = null;
        }
        Changed?.Invoke();
        foreach (var kind in Enum.GetValues<CleanupKind>())
        {
            var k = kind;
            Sizes[k] = await Task.Run(() => HealthScan.Measure(k));
            Changed?.Invoke();
        }
    }

    /// <summary>Cleans one kind (the card has already asked "Sure?"); returns what happened, in words.</summary>
    internal async Task<string> CleanAsync(CleanupKind kind)
    {
        if (_dry)
        {
            var (bytes, files) = Sizes.TryGetValue(kind, out var s) && s is { } known ? known : await Task.Run(() => HealthScan.Measure(kind));
            return _last = $"would {Cleanup.Verb(kind).ToLowerInvariant()} {kind}: {StorageText.Size(bytes)} in {files} files";
        }
        var done = await Task.Run(() => HealthScan.Clean(kind));
        _ctx.Info($"Cleanup {kind}: {done.Files} files, {done.Bytes} bytes");
        _last = $"{kind}: {StorageText.Size(done.Bytes)} in {done.Files} files";
        Sizes[kind] = await Task.Run(() => HealthScan.Measure(kind));
        Drives = await Task.Run(HealthScan.Drives);
        Changed?.Invoke();
        return kind == CleanupKind.OldDownloads
            ? $"Moved {StorageText.Size(done.Bytes)} to the Recycle Bin"
            : $"Freed {StorageText.Size(done.Bytes)}";
    }

    string State()
    {
        string sizes = string.Join(" ", Enum.GetValues<CleanupKind>().Select(k =>
            $"{k}={(Sizes.TryGetValue(k, out var s) ? s is { } v ? $"{StorageText.Size(v.Bytes)}/{v.Files}" : "measuring" : "-")}"));
        return $"report={(Report is { } r ? "'" + HealthText.Line(r) + "'" : "none")} attention={(Report is { } a && HealthText.NeedsAttention(a))} " +
               $"seen={_store.SeenDay ?? "-"} glance={(Glance() is { } g ? "'" + g.Text + "'" : "none")} drives={Drives.Count} {sizes} dry={(_dry ? "on" : "off")} " +
               $"last='{_last}' card={(_view is { IsVisible: true } ? "open" : "closed")}";
    }
}
