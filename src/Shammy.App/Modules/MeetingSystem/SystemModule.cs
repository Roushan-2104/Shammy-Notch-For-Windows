using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using Shammy.Core.MeetingSystem;

namespace Shammy.Modules.MeetingSystem;

/// <summary>
/// System monitor: the v2 "System" card (laptop battery, Bluetooth headsets' battery, CPU and memory),
/// refreshed every second only while it is on screen, plus a cheap 10 s background sample that raises
/// alerts for runaway apps, full memory and fast battery drain.
/// </summary>
internal sealed class SystemModule : IShammyModule
{
    const double MemoryAlertPercent = 90, DrainAlertPerHour = 25;

    /// <summary>
    /// An app keeping one and a half cores busy for 30 s is the runaway worth flagging (a single busy video
    /// tab stays under it). As a share of the whole machine that's under 19% on 8 cores; the old "80% of the
    /// machine" rule never fired there. Capped at 80% so small machines behave as before.
    /// </summary>
    static readonly double HogPercent = Math.Min(80, 150.0 / Environment.ProcessorCount);

    /// <summary>
    /// While the card isn't showing, the (costlier) walk over every process only happens when the whole machine is
    /// at least this busy: a runaway worth a heads-up then shows long before anything is missed, and a quiet PC
    /// with a busy browser tab isn't scanned every few seconds.
    /// </summary>
    static readonly double HiddenScanPercent = Math.Max(50, HogPercent);

    static readonly TimeSpan VisibleInterval = TimeSpan.FromSeconds(1);
    static readonly TimeSpan HiddenInterval = TimeSpan.FromSeconds(30);
    static readonly TimeSpan BatteryStep = TimeSpan.FromMinutes(30);
    static readonly TimeSpan HeadsetRefresh = TimeSpan.FromSeconds(60);
    const int BatteryPoints = 12;

    /// <summary>The battery level every half hour since Shammy started (the detail's last-6-hours bars).</summary>
    readonly List<(DateTime At, int Percent)> _batteryHistory = new();
    readonly DrainEstimator _drain = new();
    readonly TimeSmoother _reportedDrain = new(TimeSpan.FromMinutes(2));
    readonly TimeSmoother _reportedCharge = new(TimeSpan.FromMinutes(2));
    readonly AlertCooldown _cooldown = new(TimeSpan.FromMinutes(10));
    readonly SustainedCondition _hog = new(TimeSpan.FromSeconds(30));
    readonly SustainedCondition _fastDrain = new(TimeSpan.FromMinutes(2));

    ModuleContext? _ctx;
    SystemSampler? _sampler;
    SystemCard? _card;
    DispatcherTimer? _timer;
    SystemSample? _last;
    double? _drainRate, _chargeRate;
    bool _sampling, _relevant;
    DateTime _headsetsReadUtc;

    Shammy.SystemInfo.BluetoothService? Bluetooth => _ctx?.Services.Bluetooth;

    public string Name => "system";

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        _sampler = new SystemSampler(ctx.Warn);
        // v2: the System card replaces the System tab; it also gets a chip, since people open it on purpose.
        _card = new SystemCard();
        _card.VisibilityChanged += OnVisibilityChanged;
        ctx.AddCard(new NotchCard
        {
            Id = "battery",
            Title = "System",
            Width = 176,
            Compact = _card.Compact,
            Detail = _card.BuildDetail,
            Order = 50,
            Chip = true,
            IsRelevant = () => _relevant,
        });
        Views.Cards.CardPipes.Register(ctx, "battery", _card.DescribeCompact, _card.DescribeDetail);
        ctx.AddPipeCommand("bt-battery", _ => Bluetooth?.Describe() ?? "error: Bluetooth unavailable");
        if (Bluetooth is { } bt) bt.Changed += Render;
        // Know right away whether the card belongs on the home row (a laptop, or a headset connected).
        _ = SampleAsync();

        _timer = new DispatcherTimer(HiddenInterval, DispatcherPriority.Background, (_, _) => _ = SampleAsync(), ctx.Ui);
        _timer.Start();

        ctx.AddCommand(new SystemCommand(this));
        ctx.AddPipeCommand("system-state", _ => State());
        ctx.AddPipeCommand("system-open", _ => OpenDetail() ? "ok" : "error: no system card");
    }

    void OnVisibilityChanged()
    {
        if (_card is null || _timer is null) return;
        bool visible = _card.IsShowing;
        _timer.Interval = visible ? VisibleInterval : HiddenInterval;
        if (!visible) return;
        if (_last is not null) Render();
        _ = SampleAsync();
    }

    /// <summary>Headset levels change slowly and reading them walks the device tree: once a minute, only while shown.</summary>
    void RefreshHeadsets(DateTime nowUtc)
    {
        if (Bluetooth is not { } bt || bt.AudioDevices.Count == 0 || nowUtc - _headsetsReadUtc < HeadsetRefresh) return;
        _headsetsReadUtc = nowUtc;
        _ = bt.RefreshBatteryAsync();
    }

    async Task SampleAsync()
    {
        if (_sampling || _sampler is null || _card is null) return;
        _sampling = true;
        try
        {
            var sampler = _sampler;
            bool visible = _card.IsShowing;
            if (visible) RefreshHeadsets(DateTime.UtcNow);
            var sample = await Task.Run(() => sampler.Sample(visible, visible ? HogPercent : HiddenScanPercent));
            Apply(sample);
        }
        catch (Exception ex)
        {
            _ctx?.Warn("System sample failed", ex);
        }
        finally
        {
            _sampling = false;
        }
    }

    void Apply(SystemSample s)
    {
        _last = s;
        if (s.HasBattery && (_batteryHistory.Count == 0 || s.At - _batteryHistory[^1].At >= BatteryStep))
        {
            _batteryHistory.Add((s.At, s.BatteryPercent));
            if (_batteryHistory.Count > BatteryPoints) _batteryHistory.RemoveAt(0);
        }

        if (s.HasBattery && !s.PluggedIn)
        {
            _drain.Add(s.At, s.BatteryPercent, false);
            // The battery's own power reading reacts in seconds; whole percentages need many minutes.
            double? reported = s.ReportedDrain is double r ? _reportedDrain.Add(r, s.At) : null;
            _drainRate = reported ?? _drain.RatePerHour(s.At);
        }
        else
        {
            _drain.Reset();
            _reportedDrain.Reset();
            _drainRate = null;
        }
        // While charging the same report runs the other way (negative drain): that gives "Full by".
        _chargeRate = s.Charging && s.ReportedDrain is double c && c < 0 ? _reportedCharge.Add(-c, s.At) : null;
        if (!s.Charging) _reportedCharge.Reset();

        CheckAlerts(s);
        Render();
    }

    void Render()
    {
        if (_card is null || _ctx is null || _last is not { } s) return;
        var headsets = Bluetooth?.AudioDevices ?? Array.Empty<Shammy.SystemInfo.BluetoothAudioDevice>();
        bool relevant = _card.IsRelevant(s.HasBattery, headsets.Count);
        if (relevant != _relevant)
        {
            _relevant = relevant;
            _ctx.CardsChanged();
        }
        var left = s.HasBattery && !s.PluggedIn ? DrainEstimator.Remaining(s.BatteryPercent, _drainRate) : null;
        var full = s.Charging ? Shammy.Core.Cards.PowerText.FullAt(s.BatteryPercent, _chargeRate, DateTime.Now) : null;
        _card.Show(new SystemCardData(s, left, full, _batteryHistory.ToList(), headsets));
    }

    void CheckAlerts(SystemSample s)
    {
        if (_ctx is null) return;
        var now = s.At;
        var hog = s.Top is { } top && top.Percent >= HogPercent ? top.Name : null;
        if (_hog.Update(hog, now) && _cooldown.TryFire("cpu", now))
            _ctx.ShowMessage("", $"{AlertName(hog!)} is using a lot of CPU", seconds: 4);

        if (s.MemoryLoad >= MemoryAlertPercent && _cooldown.TryFire("memory", now))
            _ctx.ShowMessage("", $"Memory is {s.MemoryLoad}% full", seconds: 4);

        bool fast = s.HasBattery && !s.PluggedIn && _drainRate > DrainAlertPerHour;
        if (_fastDrain.Update(fast, now) && _cooldown.TryFire("battery", now))
            _ctx.ShowMessage("", $"Battery draining fast ({SystemFormat.Rate(_drainRate ?? 0)})", seconds: 4);
    }

    /// <summary>"Visual Studio Code" rather than "Code" when the exe says so.</summary>
    static string AlertName(string processName)
    {
        string? path = null, description = null;
        try
        {
            var processes = Process.GetProcessesByName(processName);
            foreach (var p in processes)
            {
                try { path ??= p.MainModule?.FileName; }
                catch (Exception) { }
                p.Dispose();
            }
            if (path is not null) description = FileVersionInfo.GetVersionInfo(path).FileDescription;
        }
        catch (Exception)
        {
            // Elevated processes hide their path; the process name is still fine.
        }
        return AppNames.Friendly(path ?? processName, null, description);
    }

    async Task<CommandResult> SummaryAsync(string text)
    {
        if (_sampler is null) return new CommandResult("", "System monitor isn't running", IsError: true);
        var s = _last;
        if (s is null)
        {
            var sampler = _sampler;
            s = await Task.Run(() => sampler.Sample(false));
            Apply(s);
        }
        // The host shows this result in the notch, then collapses it ~2.6 s later; the System detail opens after.
        OpenDetail();

        string cpuMemory = $"CPU {SystemFormat.Percent(s.Cpu)} · Memory {s.MemoryLoad}%";
        string battery = BatteryLine(s);
        return text.Contains("battery", StringComparison.OrdinalIgnoreCase)
            ? new CommandResult("", battery, cpuMemory)
            : new CommandResult("", cpuMemory, s.Top is { } t ? $"{AppNames.Friendly(t.Name)} is busiest · {battery}" : battery);
    }

    string BatteryLine(SystemSample s)
    {
        if (!s.HasBattery) return "No battery";
        if (s.PluggedIn) return $"Battery {s.BatteryPercent}%, {(s.Charging ? "charging" : "plugged in")}";
        return DrainEstimator.Remaining(s.BatteryPercent, _drainRate) is TimeSpan left
            ? $"Battery {s.BatteryPercent}%, {SystemFormat.Duration(left)} left"
            : $"Battery {s.BatteryPercent}%";
    }

    // OpenCard waits for a command's confirmation card and holds the detail until it's been looked at.
    bool OpenDetail() => _ctx?.OpenCard("battery") == true;

    string State()
    {
        if (_sampler is null) return "error: not started";
        var sampler = _sampler;
        // Two readings a moment apart so the busiest app has a delta to measure.
        var s = Task.Run(() =>
        {
            sampler.Sample(true);
            Thread.Sleep(400);
            return sampler.Sample(true);
        }).GetAwaiter().GetResult();
        Apply(s);

        var inv = CultureInfo.InvariantCulture;
        string battery = s.HasBattery ? $"{s.BatteryPercent}%" : "none";
        string drain = _drainRate is double r ? SystemFormat.Rate(r) : "-";
        string left = DrainEstimator.Remaining(s.BatteryPercent, _drainRate) is TimeSpan t && !s.PluggedIn ? SystemFormat.Duration(t) : "-";
        string top = s.Top is { } p ? $"{p.Name} {p.Percent.ToString("0.0", inv)}%" : "-";
        return $"cpu={s.Cpu.ToString("0.0", inv)}% ram={s.MemoryLoad}% ({SystemFormat.Gigabytes(s.UsedMemory)}/{SystemFormat.Gigabytes(s.TotalMemory)} GB) " +
               $"battery={battery} plugged={s.PluggedIn} charging={s.Charging} drain={drain} remaining={left} top={top} " +
               $"batteryHistory={_batteryHistory.Count} visible={_card?.IsShowing == true}";
    }

    public void Dispose()
    {
        _timer?.Stop();
        if (Bluetooth is { } bt) bt.Changed -= Render;
    }

    sealed class SystemCommand : ITextCommand
    {
        readonly SystemModule _owner;

        public SystemCommand(SystemModule owner) => _owner = owner;

        public bool CanHandle(string text) => Phrases.IsSystemQuery(text);

        public Task<CommandResult> RunAsync(string text) => _owner.SummaryAsync(text);
    }
}
