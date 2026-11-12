using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Shammy.Core.Activities;
using Shammy.Core.Connect;
using Shammy.Core.Quick;
using Shammy.Infrastructure;
using Shammy.Modules.MeetingSystem;
using Shammy.Views.Cards;

namespace Shammy.Modules.Quick;

/// <summary>What the Laptop card remembers: Shammy's night light, and a quiet spell still running.</summary>
internal sealed class QuickStore
{
    public bool NightLight { get; set; }
    public DateTime? QuietUntilUtc { get; set; }
}

/// <summary>
/// The Laptop card: the laptop's controls in one place (Wi-Fi, Bluetooth, power mode, keep awake, mute mic,
/// quiet, night light, storage, lock, sleep, restart / shut down), reached from its chip, and the same things
/// typed or spoken ("keep awake for 2 hours", "lock my pc", "turn off wifi", "bluetooth chalu karo", "best
/// battery", "night light on", "quiet for an hour"). Restart and shut down are card-only and ask first.
/// Keep-awake always has an end time; while it runs the pet shows "Awake" with the time left and a Stop circle.
/// Quiet is Shammy's own do-not-disturb (it holds its pop-ups, an hour from the tile); night light is Shammy's own
/// warm tint over every screen, remembered across restarts. Neither opens Windows Settings.
/// Pipes: "quick-state", "quick-keepawake &lt;minutes|off&gt;", "quick-advance &lt;minutes&gt;", "quick-parse &lt;text&gt;",
/// "quick-run lock|sleep|nightlight|dnd|mic [--dry]", "quick-dry on|off", "quick-open", "quick-off", "quick-power".
/// </summary>
internal sealed class QuickModule : IShammyModule
{
    public const string CardId = "quick", ActivityId = "quick.keepawake";
    public const string PowerPage = "ms-settings:powersleep", StoragePage = "ms-settings:storagesense";

    // Just above music (20): a song steps aside for it, everything else comes first.
    const int Priority = 22;
    const string CupGlyph = "", LockGlyph = "", MoonGlyph = "", BoltGlyph = "", MicGlyph = "",
        SettingsGlyph = "", SunGlyph = "", QuietGlyph = "";

    /// <summary>
    /// Shammy's own quiet. QuietReason has no member for it yet, so it is a value of its own, far from the named
    /// ones; the state pipe's description reads it as "test" until QuietRules names it.
    /// </summary>
    const QuietReason QuietByUser = (QuietReason)40;

    /// <summary>The quiet tile's length.</summary>
    public const int QuietMinutes = 60;

    // Long enough for the notch to close (and a command's confirmation to be read) before the screen goes.
    static readonly TimeSpan LockDelay = TimeSpan.FromMilliseconds(600), SleepDelay = TimeSpan.FromMilliseconds(1400);

    readonly KeepAwakeClock _clock = new();
    // Quiet runs on the same kind of clock: never open-ended, a minute's wake-up at most.
    readonly KeepAwakeClock _quiet = new();
    ModuleContext _ctx = null!;
    QuickStore _store = new();
    DispatcherTimer? _tick, _pending, _quietTick;
    KeepAwakeContent? _content;
    NightLightOverlay? _night;
    MicMute? _mic;
    QuickView? _view;
    bool _held, _micTried, _dry;
    string _last = "-";

    public string Name => "quick";

    /// <summary>Keep-awake started, stopped or lost a minute, a switch changed, or quiet ticked: redraw the tiles.</summary>
    public event Action? Changed;

    public bool KeepAwakeOn => _clock.IsOn;
    public int MinutesLeft => _clock.MinutesLeft(DateTime.UtcNow);
    public int StartedMinutes => _clock.StartedMinutes;

    /// <summary>The length the tile starts with (an hour until another is picked).</summary>
    public int Pick { get; private set; } = KeepAwakeClock.DefaultMinutes;

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        _store = ctx.Load<QuickStore>();
        _tick = new DispatcherTimer(DispatcherPriority.Background, ctx.Ui);
        _tick.Tick += (_, _) => Tick();
        _quietTick = new DispatcherTimer(DispatcherPriority.Background, ctx.Ui);
        _quietTick.Tick += (_, _) => QuietTick();

        // Night light and a quiet spell carry over a restart of Shammy (or of the PC, for night light).
        _night = new NightLightOverlay(ctx.Ui);
        if (_store.NightLight && !_night.Show())
        {
            _store.NightLight = false;
            ctx.Save(_store);
        }
        if (_store.QuietUntilUtc is { } until && until > DateTime.UtcNow) BeginQuiet(until);
        else if (_store.QuietUntilUtc is not null)
        {
            _store.QuietUntilUtc = null;
            ctx.Save(_store);
        }

        ctx.AddCard(new NotchCard
        {
            Id = CardId,
            Title = "Laptop",
            Width = 176,
            Order = 90,
            Chip = true,
            // Opened on purpose from its chip; while keep-awake runs the pet already says so.
            IsRelevant = () => false,
            Compact = QuickView.BuildCompact(() => ctx.OpenCard(CardId)),
            Detail = () => _view = new QuickView(this),
        });
        // Fixed phrases only, and ahead of the launcher so "open laptop controls" isn't an app search.
        ctx.AddCommandFirst(new QuickTextCommand(this));

        ctx.AddPipeCommand("quick-state", _ => State());
        ctx.AddPipeCommand("quick-keepawake", KeepAwakeFromPipe);
        ctx.AddPipeCommand("quick-advance", Advance);
        ctx.AddPipeCommand("quick-parse", arg => Describe(QuickPhrases.Parse(arg)));
        ctx.AddPipeCommand("quick-run", RunFromPipe);
        ctx.AddPipeCommand("quick-dry", arg =>
        {
            _dry = arg.Trim().ToLowerInvariant() is not ("off" or "false" or "0");
            return "ok dry=" + (_dry ? "on" : "off");
        });
        ctx.AddPipeCommand("quick-open", _ => ctx.OpenCard(CardId) ? "ok" : "error: no quick card");
        ctx.AddPipeCommand("quick-off", _ => OffForTest());
        ctx.AddPipeCommand("quick-power", arg => arg.Trim() switch
        {
            "restart" => PowerOff(restart: true),
            "shutdown" => PowerOff(restart: false),
            "" => PowerModeNow?.ToString() ?? "none",
            var m when Enum.TryParse<PowerModeKind>(m, true, out var kind) => SetPowerMode(kind),
            _ => "error: quick-power [efficiency|balanced|performance|restart|shutdown]",
        });
    }

    // ---- Keep awake --------------------------------------------------------------------------------

    /// <summary>Starts (or restarts) keep-awake; returns the minutes it will run, or null if Windows refused.</summary>
    internal int? StartKeepAwake(int minutes)
    {
        if (!Hold(true)) return null;
        int runs = _clock.Start(DateTime.UtcNow, minutes);
        _content ??= new KeepAwakeContent(_clock, () => StopKeepAwake());
        if (_ctx.Get(ActivityId) is null)
            _ctx.Post(new Activity(ActivityId, ActivityKind.Custom, Priority, _content) { Click = () => _ctx.OpenCard(CardId) });
        _content.Refresh();
        Arm();
        Changed?.Invoke();
        _ctx.Info($"Keep awake on for {runs} min");
        return runs;
    }

    /// <summary>Stops it and lets the PC sleep again; false if it wasn't on.</summary>
    internal bool StopKeepAwake()
    {
        if (!_clock.IsOn) return false;
        _clock.Stop();
        End();
        _ctx.Info("Keep awake off");
        return true;
    }

    /// <summary>The tile: off starts the picked length, on stops it.</summary>
    internal void ToggleKeepAwake()
    {
        if (StopKeepAwake()) return;
        if (StartKeepAwake(Pick) is null) _ctx.ShowMessage(CupGlyph, "Couldn’t keep the PC awake", error: true);
    }

    /// <summary>The tile's length: remembered for the next start, and applied now if it is running.</summary>
    internal void PickLength(int minutes)
    {
        Pick = KeepAwakeClock.Clamp(minutes);
        if (_clock.IsOn) StartKeepAwake(Pick);
        else Changed?.Invoke();
    }

    void End()
    {
        Hold(false);
        _tick?.Stop();
        _ctx.Remove(ActivityId);
        _content = null;
        Changed?.Invoke();
    }

    void Tick()
    {
        if (_clock.Expire(DateTime.UtcNow))
        {
            End();
            _ctx.Info("Keep awake ended");
            if (!QuietState.IsQuiet) _ctx.ShowMessage(CupGlyph, "Keep awake ended");
            return;
        }
        if (!_clock.IsOn)
        {
            _tick?.Stop();
            return;
        }
        _content?.Refresh();
        Arm();
        Changed?.Invoke();
    }

    /// <summary>Looks again when the minutes left change or it ends: one wake-up a minute, nothing faster.</summary>
    void Arm()
    {
        if (_tick is null) return;
        _tick.Stop();
        _tick.Interval = _clock.NextCheck(DateTime.UtcNow);
        _tick.Start();
    }

    /// <summary>
    /// Asks Windows to keep the PC and the display on (or lets go). The request belongs to the thread that made
    /// it, so it is always made and cleared on the UI thread.
    /// </summary>
    bool Hold(bool on)
    {
        if (!_ctx.Ui.CheckAccess())
        {
            try { return _ctx.Ui.Invoke(() => Hold(on)); }
            catch (Exception ex)
            {
                _ctx.Warn("Reaching the UI thread for keep-awake failed", ex);
                return false;
            }
        }
        if (_held == on) return true;
        try
        {
            uint flags = on ? QuickNative.Continuous | QuickNative.SystemRequired | QuickNative.DisplayRequired : QuickNative.Continuous;
            if (QuickNative.SetThreadExecutionState(flags) == 0)
            {
                _ctx.Warn($"Windows refused keep-awake {(on ? "on" : "off")}");
                return false;
            }
            _held = on;
            return true;
        }
        catch (Exception ex)
        {
            _ctx.Warn("Keep-awake failed", ex);
            return false;
        }
    }

    // ---- Quiet (Shammy's own do-not-disturb) ---------------------------------------------------------

    public bool QuietOn => _quiet.IsOn;
    public int QuietMinutesLeft => _quiet.MinutesLeft(DateTime.UtcNow);

    /// <summary>Starts (or restarts) quiet for that long; returns the minutes it will run.</summary>
    internal int StartQuiet(int minutes)
    {
        int runs = KeepAwakeClock.Clamp(minutes);
        BeginQuiet(DateTime.UtcNow.AddMinutes(runs));
        _ctx.Info($"Quiet on for {runs} min");
        return runs;
    }

    void BeginQuiet(DateTime untilUtc)
    {
        // Ends exactly then, even when carried over a restart.
        int minutes = KeepAwakeClock.Clamp((int)Math.Ceiling((untilUtc - DateTime.UtcNow).TotalMinutes));
        _quiet.Start(untilUtc.AddMinutes(-minutes), minutes);
        QuietState.Set(QuietByUser, true);
        _store.QuietUntilUtc = _quiet.EndsUtc;
        _ctx.Save(_store);
        ArmQuiet();
        _last = "quiet on";
        Changed?.Invoke();
    }

    /// <summary>Ends quiet; false if it wasn't on.</summary>
    internal bool EndQuiet()
    {
        if (!_quiet.IsOn) return false;
        _quiet.Stop();
        Ended();
        _ctx.Info("Quiet off");
        return true;
    }

    void Ended()
    {
        _quietTick?.Stop();
        QuietState.Set(QuietByUser, false);
        _store.QuietUntilUtc = null;
        _ctx.Save(_store);
        _last = "quiet off";
        Changed?.Invoke();
    }

    /// <summary>The tile: off starts an hour, on ends it.</summary>
    internal void ToggleQuiet()
    {
        if (!EndQuiet()) StartQuiet(QuietMinutes);
    }

    void QuietTick()
    {
        if (_quiet.Expire(DateTime.UtcNow))
        {
            Ended();
            _ctx.Info("Quiet ended");
            return;
        }
        if (!_quiet.IsOn)
        {
            _quietTick?.Stop();
            return;
        }
        ArmQuiet();
        Changed?.Invoke();
    }

    void ArmQuiet()
    {
        if (_quietTick is null) return;
        _quietTick.Stop();
        _quietTick.Interval = _quiet.NextCheck(DateTime.UtcNow);
        _quietTick.Start();
    }

    // ---- Night light (Shammy's own warm tint) -----------------------------------------------------------

    public bool NightLightOn => _night?.IsOn == true;

    /// <summary>Turns the tint on or off and remembers it; false if the tint couldn't be put on the screens.</summary>
    internal bool SetNightLight(bool on)
    {
        if (_night is null) return false;
        if (on != _night.IsOn)
        {
            if (on && !_night.Show())
            {
                _ctx.Warn("Night light: no screen could be tinted");
                return false;
            }
            if (!on) _night.Hide();
            _ctx.Info($"Night light {(on ? "on" : "off")}");
        }
        if (_store.NightLight != on)
        {
            _store.NightLight = on;
            _ctx.Save(_store);
        }
        _last = "nightlight " + (on ? "on" : "off");
        Changed?.Invoke();
        return true;
    }

    internal void ToggleNightLight()
    {
        bool on = !NightLightOn;
        if (!SetNightLight(on)) _ctx.ShowMessage(SunGlyph, "Couldn’t turn on night light", error: true);
    }

    // ---- Wi-Fi, Bluetooth, power mode, restart and shut down ------------------------------------------

    const string WifiGlyph = "", BluetoothGlyph = "", PowerGlyph = "";

    /// <summary>The radios' states as last read (null: unknown or no radio); the card reads them when it opens.</summary>
    public bool? WifiOn { get; private set; }
    public bool? BluetoothOn { get; private set; }
    public PowerModeKind? PowerModeNow { get; private set; }

    /// <summary>False until the switches have been read once: the tiles say "Checking…" rather than "Not found".</summary>
    public bool SwitchesRead { get; private set; }

    /// <summary>Reads the switches again (the card is opening): radios off the UI thread, then a redraw.</summary>
    internal async Task RefreshSwitchesAsync()
    {
        PowerModeNow = LaptopSwitches.GetPowerMode();
        var wifi = LaptopSwitches.WifiAsync(null);
        var bt = Palette.BluetoothRadio.GetAsync();
        WifiOn = await wifi;
        BluetoothOn = await bt;
        SwitchesRead = true;
        Changed?.Invoke();
    }

    static string OnOff(bool on) => on ? "on" : "off";

    /// <summary>Turns Wi-Fi on or off and says what happened; Windows refusing is said, never swallowed.</summary>
    internal async Task<CommandResult> SetWifiAsync(bool on)
    {
        if (_dry)
        {
            _last = $"would switch wifi {OnOff(on)}";
            return new CommandResult(WifiGlyph, $"Would turn Wi-Fi {OnOff(on)}", "Test mode: nothing happened");
        }
        var now = await LaptopSwitches.WifiAsync(null);
        WifiOn = now;
        if (now is null)
        {
            Changed?.Invoke();
            return new CommandResult(WifiGlyph, "No Wi-Fi switch on this PC", IsError: true);
        }
        if (now != on) WifiOn = await LaptopSwitches.WifiAsync(on);
        Changed?.Invoke();
        if (WifiOn != on)
        {
            _ctx.Warn($"Windows refused Wi-Fi {OnOff(on)}");
            _last = $"error: wifi {OnOff(on)} refused";
            return new CommandResult(WifiGlyph, $"Couldn’t turn Wi-Fi {OnOff(on)}", IsError: true);
        }
        _last = "wifi " + OnOff(on);
        return now == on
            ? new CommandResult(WifiGlyph, $"Wi-Fi is already {OnOff(on)}")
            : new CommandResult(WifiGlyph, $"Wi-Fi is {OnOff(on)}");
    }

    /// <summary>Turns Bluetooth on or off and says what happened.</summary>
    internal async Task<CommandResult> SetBluetoothAsync(bool on)
    {
        if (_dry)
        {
            _last = $"would switch bluetooth {OnOff(on)}";
            return new CommandResult(BluetoothGlyph, $"Would turn Bluetooth {OnOff(on)}", "Test mode: nothing happened");
        }
        var now = await Palette.BluetoothRadio.GetAsync();
        BluetoothOn = now;
        if (now is null)
        {
            Changed?.Invoke();
            return new CommandResult(BluetoothGlyph, "No Bluetooth switch on this PC", IsError: true);
        }
        if (now != on)
        {
            if (await Palette.BluetoothRadio.SetAsync(on)) BluetoothOn = on;
            else BluetoothOn = await Palette.BluetoothRadio.GetAsync() ?? now;
        }
        Changed?.Invoke();
        if (BluetoothOn != on)
        {
            _ctx.Warn($"Windows refused Bluetooth {OnOff(on)}");
            _last = $"error: bluetooth {OnOff(on)} refused";
            return new CommandResult(BluetoothGlyph, $"Couldn’t turn Bluetooth {OnOff(on)}", IsError: true);
        }
        _last = "bluetooth " + OnOff(on);
        return now == on
            ? new CommandResult(BluetoothGlyph, $"Bluetooth is already {OnOff(on)}")
            : new CommandResult(BluetoothGlyph, $"Bluetooth is {OnOff(on)}");
    }

    internal async void ToggleWifi()
    {
        try
        {
            if (!SwitchesRead) await RefreshSwitchesAsync();
            if (WifiOn is not { } on)
            {
                _ctx.ShowMessage(WifiGlyph, "No Wi-Fi switch on this PC", error: true);
                return;
            }
            var said = await SetWifiAsync(!on);
            if (said.IsError) _ctx.ShowMessage(WifiGlyph, said.Title, error: true);
        }
        catch (Exception ex)
        {
            _ctx.Warn("Wi-Fi tile failed", ex);
            _ctx.ShowMessage(WifiGlyph, "Couldn’t switch Wi-Fi", error: true);
        }
    }

    internal async void ToggleBluetooth()
    {
        try
        {
            if (!SwitchesRead) await RefreshSwitchesAsync();
            if (BluetoothOn is not { } on)
            {
                _ctx.ShowMessage(BluetoothGlyph, "No Bluetooth switch on this PC", error: true);
                return;
            }
            var said = await SetBluetoothAsync(!on);
            if (said.IsError) _ctx.ShowMessage(BluetoothGlyph, said.Title, error: true);
        }
        catch (Exception ex)
        {
            _ctx.Warn("Bluetooth tile failed", ex);
            _ctx.ShowMessage(BluetoothGlyph, "Couldn’t switch Bluetooth", error: true);
        }
    }

    /// <summary>Balanced → Best performance → Best battery → Balanced; Settings where Windows has no slider.</summary>
    internal void CyclePowerMode()
    {
        if (!SwitchesRead) PowerModeNow = LaptopSwitches.GetPowerMode();
        if (PowerModeNow is not { } now)
        {
            OpenSettings(PowerPage);
            return;
        }
        var said = PowerModeResult(PowerMode.Next(now));
        if (said.IsError) _ctx.ShowMessage(BoltGlyph, said.Title, error: true);
    }

    /// <summary>Sets the power mode and says what happened.</summary>
    internal CommandResult PowerModeResult(PowerModeKind kind)
    {
        if (_dry)
        {
            _last = "would set power mode " + kind;
            return new CommandResult(BoltGlyph, $"Would switch to {PowerMode.Name(kind)}", "Test mode: nothing happened");
        }
        if (!LaptopSwitches.SetPowerMode(kind))
        {
            _last = "error: Windows refused the power mode";
            return new CommandResult(BoltGlyph, "Couldn’t change the power mode", "Windows said no; Settings › System › Power has it", IsError: true);
        }
        PowerModeNow = LaptopSwitches.GetPowerMode() ?? kind;
        Changed?.Invoke();
        _last = "power mode " + PowerModeNow;
        return new CommandResult(BoltGlyph, $"Power mode · {PowerMode.Name(PowerModeNow.Value)}");
    }

    /// <summary>The test pipe's power mode.</summary>
    internal string SetPowerMode(PowerModeKind kind)
    {
        var said = PowerModeResult(kind);
        return said.IsError ? "error: Windows refused the power mode" : _last;
    }

    /// <summary>"54 GB free of 238 GB" for the system drive.</summary>
    internal string StorageLine()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:" + System.IO.Path.DirectorySeparatorChar);
            return StorageText.Free(drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception) { return "Open settings"; }
    }

    /// <summary>The Storage tile: Shammy's health and cleanup card when it is there, else Windows' storage page.</summary>
    internal void OpenStorage()
    {
        if (!_ctx.OpenCard("health")) OpenSettings(StoragePage);
    }

    /// <summary>Test hook: the Laptop card's restart / shut down tile, first click.</summary>
    internal string OffForTest()
    {
        if (_view is not { IsVisible: true } v) return "error: the card isn't open";
        v.ClickOffForTest();
        return v.OffChoiceShown ? "offered restart and shut down" : "error: nothing offered";
    }

    /// <summary>Restart or shut down, after the card asked once more. In a dry run it only says what it would do.</summary>
    internal string PowerOff(bool restart)
    {
        if (_dry) return _last = restart ? "would restart" : "would shut down";
        StopKeepAwake();
        _ctx.Notch.Collapse();
        After(LockDelay, () =>
        {
            if (!LaptopSwitches.PowerOff(restart)) _ctx.ShowMessage(PowerGlyph, restart ? "Couldn’t restart" : "Couldn’t shut down", error: true);
        });
        return _last = restart ? "restart" : "shut down";
    }

    // ---- Lock, sleep, settings pages, microphone ----------------------------------------------------

    /// <summary>Locks the PC once the notch has closed. In a dry run it only says what it would do.</summary>
    internal string Lock(bool dry = false)
    {
        if (dry || _dry) return WouldDo("would lock: LockWorkStation", LockGlyph, "Would lock this PC", dry);
        _ctx.Notch.Collapse();
        After(LockDelay, () =>
        {
            try
            {
                if (!QuickNative.LockWorkStation()) Failed("Couldn’t lock this PC", LockGlyph, Marshal.GetLastWin32Error());
            }
            catch (Exception ex)
            {
                _ctx.Warn("Locking failed", ex);
                _ctx.ShowMessage(LockGlyph, "Couldn’t lock this PC", error: true);
            }
        });
        return _last = "lock";
    }

    /// <summary>
    /// Puts the PC to sleep once the notch has closed. A Modern Standby laptop (S0) sleeps by turning its screen
    /// off, where SetSuspendState does nothing; others suspend. In a dry run it only says what it would do.
    /// </summary>
    internal string Sleep(bool dry = false)
    {
        bool standby = LaptopSwitches.ModernStandby();
        if (dry || _dry)
        {
            string how = standby ? "screen off (Modern Standby)" : "SetSuspendState(hibernate: false)";
            return WouldDo($"would sleep: {how}{(_clock.IsOn ? ", after stopping keep-awake" : "")}", MoonGlyph, "Would put the PC to sleep", dry);
        }
        // Asking for sleep ends keep-awake: it must not still be holding the PC on when it wakes.
        StopKeepAwake();
        _ctx.Notch.Collapse();
        After(SleepDelay, () => Task.Run(() =>
        {
            // Off the UI thread: suspending only returns once the PC has woken up again.
            try
            {
                if (standby)
                {
                    if (!LaptopSwitches.DisplayOff())
                    {
                        int error = Marshal.GetLastWin32Error();
                        _ctx.Ui.BeginInvoke(() => Failed("Couldn’t put the PC to sleep", MoonGlyph, error));
                    }
                    return;
                }
                if (!QuickNative.SetSuspendState(false, false, false))
                {
                    int error = Marshal.GetLastWin32Error();
                    _ctx.Ui.BeginInvoke(() => Failed("Couldn’t put the PC to sleep", MoonGlyph, error));
                }
            }
            catch (Exception ex)
            {
                _ctx.Warn("Sleep failed", ex);
                _ctx.Ui.BeginInvoke(() => _ctx.ShowMessage(MoonGlyph, "Couldn’t put the PC to sleep", error: true));
            }
        }));
        return _last = standby ? "sleep (screen off)" : "sleep";
    }

    /// <summary>A dry run: remembers what would have happened, and says so in the notch when a tile or a command asked.</summary>
    string WouldDo(string what, string glyph, string message, bool fromPipe)
    {
        if (!fromPipe) _ctx.ShowMessage(glyph, message + " (test mode)");
        return _last = what;
    }

    void Failed(string message, string glyph, int error)
    {
        _ctx.Warn($"{message} (Windows error {error})");
        _ctx.ShowMessage(glyph, message, error: true);
    }

    /// <summary>One thing a moment from now; a second request replaces the first.</summary>
    void After(TimeSpan delay, Action run)
    {
        _pending?.Stop();
        var timer = new DispatcherTimer(DispatcherPriority.Normal, _ctx.Ui) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(_pending, timer)) _pending = null;
            run();
        };
        _pending = timer;
        timer.Start();
    }

    /// <summary>
    /// Opens one of the fixed Windows Settings pages the tiles fall back to (power, storage). Anything that goes
    /// wrong says "Couldn’t open Settings" in the notch.
    /// </summary>
    internal string OpenSettings(string page, bool dry = false)
    {
        if (page is not (PowerPage or StoragePage))
        {
            _ctx.Warn("Settings page not allowed: " + page);
            _ctx.ShowMessage(SettingsGlyph, "Couldn’t open Settings", error: true);
            return _last = "error: unknown page";
        }
        if (dry || _dry) return _last = "would open: " + page;
        bool opened;
        try { opened = _ctx.Open(page); }
        catch (Exception ex)
        {
            _ctx.Warn("Opening Settings failed", ex);
            opened = false;
        }
        if (!opened)
        {
            _ctx.ShowMessage(SettingsGlyph, "Couldn’t open Settings", error: true);
            return _last = "error: couldn't open " + page;
        }
        _ctx.Notch.Collapse();
        return _last = "opened " + page;
    }

    /// <summary>
    /// The same default microphone the "mute mic" command switches. Bound the first time the card or the pipe
    /// asks, so a PC that never opens the Laptop card never pays for it.
    /// </summary>
    MicMute? Mic()
    {
        if (_mic is not null || _micTried) return _mic;
        _micTried = true;
        try
        {
            var mic = new MicMute(_ctx.Ui, _ctx.Warn);
            mic.Changed += OnMicChanged;
            mic.Start();
            _mic = mic;
        }
        catch (Exception ex)
        {
            _ctx.Warn("Microphone unavailable for the Laptop card", ex);
        }
        return _mic;
    }

    void OnMicChanged() => Changed?.Invoke();

    /// <summary>True muted, false live, null when there is no microphone.</summary>
    internal bool? MicMuted => Mic() is { HasDevice: true } mic ? mic.Muted : null;

    internal void ToggleMic()
    {
        if (Mic()?.Toggle() is not bool muted)
        {
            _ctx.ShowMessage(MicGlyph, "No microphone found", error: true);
            return;
        }
        // The microphone's own change notice redraws the tile.
        _last = muted ? "mic muted" : "mic live";
    }

    // ---- Typed and spoken commands -----------------------------------------------------------------

    static string Until(int minutes) =>
        "Until " + DateTime.Now.AddMinutes(minutes).ToString(CardKit.H24 ? "HH:mm" : "h:mm tt", CultureInfo.InvariantCulture);

    async Task<CommandResult> RunAsync(QuickCommand? command)
    {
        if (command is null) return new CommandResult(BoltGlyph, "Say keep awake, lock or sleep my PC", IsError: true);
        switch (command.Kind)
        {
            case QuickKind.KeepAwake:
            {
                int asked = command.Minutes ?? KeepAwakeClock.DefaultMinutes;
                if (StartKeepAwake(asked) is not int runs) return new CommandResult(CupGlyph, "Couldn’t keep the PC awake", IsError: true);
                return new CommandResult(CupGlyph, $"Keeping awake for {KeepAwakeText.Long(runs)}",
                    asked > runs ? $"{KeepAwakeText.Long(runs)} is the longest it goes" : Until(runs));
            }
            case QuickKind.StopKeepAwake:
                return StopKeepAwake()
                    ? new CommandResult(CupGlyph, "Keep awake is off", "The PC can sleep again")
                    : new CommandResult(CupGlyph, "Keep awake wasn’t on");
            case QuickKind.Lock:
                return Lock().StartsWith("would", StringComparison.Ordinal)
                    ? new CommandResult(LockGlyph, "Would lock this PC", "Test mode: nothing happened")
                    : new CommandResult(LockGlyph, "Locking this PC");
            case QuickKind.Sleep:
                return Sleep().StartsWith("would", StringComparison.Ordinal)
                    ? new CommandResult(MoonGlyph, "Would put the PC to sleep", "Test mode: nothing happened")
                    : new CommandResult(MoonGlyph, "Going to sleep");
            case QuickKind.Wifi:
                return await SetWifiAsync(command.On ?? true);
            case QuickKind.Bluetooth:
                return await SetBluetoothAsync(command.On ?? true);
            case QuickKind.PowerMode:
                return PowerModeResult(command.Mode ?? PowerModeKind.Balanced);
            case QuickKind.NightLight:
            {
                bool on = command.On ?? true;
                if (!SetNightLight(on)) return new CommandResult(SunGlyph, "Couldn’t turn on night light", IsError: true);
                return on ? new CommandResult(SunGlyph, "Night light is on", "A warm tint over every screen") : new CommandResult(SunGlyph, "Night light is off");
            }
            case QuickKind.Quiet:
            {
                if (command.On == false)
                    return EndQuiet() ? new CommandResult(QuietGlyph, "Quiet is off", "Pop-ups are back") : new CommandResult(QuietGlyph, "Quiet wasn’t on");
                int runs = StartQuiet(command.Minutes ?? QuietMinutes);
                return new CommandResult(QuietGlyph, $"Quiet for {KeepAwakeText.Long(runs)}", $"{Until(runs)} · pop-ups wait");
            }
            case QuickKind.Open:
                // Opens once this confirmation has closed.
                _ctx.OpenCard(CardId);
                return new CommandResult(BoltGlyph, "Laptop", "Wi-Fi, power, lock and more");
            default:
                return new CommandResult(BoltGlyph, "Laptop");
        }
    }

    CommandPreview? Preview(QuickCommand? command)
    {
        if (command is null) return null;
        string Turn(string what) => $"Turn {what} {OnOff(command.On ?? true)}";
        switch (command.Kind)
        {
            case QuickKind.KeepAwake:
            {
                int runs = KeepAwakeClock.Clamp(command.Minutes ?? KeepAwakeClock.DefaultMinutes);
                return new CommandPreview(CupGlyph, $"Keep awake for {KeepAwakeText.Long(runs)}", Until(runs));
            }
            case QuickKind.StopKeepAwake:
                return new CommandPreview(CupGlyph, "Stop keeping awake", _clock.IsOn ? $"{KeepAwakeText.Long(Math.Max(1, MinutesLeft))} left" : "It isn’t on");
            case QuickKind.Lock: return new CommandPreview(LockGlyph, "Lock this PC");
            case QuickKind.Sleep: return new CommandPreview(MoonGlyph, "Put the PC to sleep");
            case QuickKind.Wifi: return new CommandPreview(WifiGlyph, Turn("Wi-Fi"));
            case QuickKind.Bluetooth: return new CommandPreview(BluetoothGlyph, Turn("Bluetooth"));
            case QuickKind.NightLight: return new CommandPreview(SunGlyph, Turn("night light"), "Shammy’s warm screen tint");
            case QuickKind.PowerMode: return new CommandPreview(BoltGlyph, $"Power mode · {PowerMode.Name(command.Mode ?? PowerModeKind.Balanced)}");
            case QuickKind.Quiet:
                return command.On == false
                    ? new CommandPreview(QuietGlyph, "End quiet", QuietOn ? $"{KeepAwakeText.Long(Math.Max(1, QuietMinutesLeft))} left" : "It isn’t on")
                    : new CommandPreview(QuietGlyph, $"Quiet for {KeepAwakeText.Long(KeepAwakeClock.Clamp(command.Minutes ?? QuietMinutes))}", "Shammy holds its pop-ups");
            case QuickKind.Open: return new CommandPreview(BoltGlyph, "Open Laptop", "Wi-Fi, power, lock and more");
            default: return null;
        }
    }

    sealed class QuickTextCommand(QuickModule module) : ITextCommand, ICommandPreview
    {
        public bool CanHandle(string text) => QuickPhrases.Parse(text) is not null;

        public Task<CommandResult> RunAsync(string text) => module.RunAsync(QuickPhrases.Parse(text));

        public CommandPreview? Preview(string text) => module.Preview(QuickPhrases.Parse(text));
    }

    // ---- Test pipe ---------------------------------------------------------------------------------

    static string Describe(QuickCommand? c) => c switch
    {
        null => "none",
        { Kind: QuickKind.KeepAwake, Minutes: int m } => string.Create(CultureInfo.InvariantCulture, $"keepawake minutes={m} runs={KeepAwakeClock.Clamp(m)}"),
        { Kind: QuickKind.KeepAwake } => string.Create(CultureInfo.InvariantCulture, $"keepawake minutes=default runs={KeepAwakeClock.DefaultMinutes}"),
        { Kind: QuickKind.StopKeepAwake } => "keepawake off",
        { Kind: QuickKind.Lock } => "lock",
        { Kind: QuickKind.Sleep } => "sleep",
        { Kind: QuickKind.Wifi } => "wifi " + OnOff(c.On ?? true),
        { Kind: QuickKind.Bluetooth } => "bluetooth " + OnOff(c.On ?? true),
        { Kind: QuickKind.NightLight } => "nightlight " + OnOff(c.On ?? true),
        { Kind: QuickKind.Quiet, On: false } => "quiet off",
        { Kind: QuickKind.Quiet } => string.Create(CultureInfo.InvariantCulture, $"quiet minutes={c.Minutes ?? QuietMinutes}"),
        { Kind: QuickKind.PowerMode } => "power " + c.Mode,
        _ => "open",
    };

    string State()
    {
        var now = DateTime.UtcNow;
        string mic = MicMuted switch { true => "muted", false => "live", null => "none" };
        string ends = _clock.EndsUtc is { } e ? e.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "-";
        string card = _view is { IsVisible: true } v ? "open " + v.Describe() : "closed";
        return string.Create(CultureInfo.InvariantCulture,
            $"keepawake={(_clock.IsOn ? "on" : "off")} minutes={_clock.MinutesLeft(now)} started={_clock.StartedMinutes} ends={ends} " +
            $"held={_held} activity={(_ctx.Get(ActivityId) is null ? "hidden" : "shown")} pick={Pick} mic={mic} dry={(_dry ? "on" : "off")} " +
            $"read={SwitchesRead} wifi={(WifiOn is { } w ? (w ? "on" : "off") : "none")} bluetooth={(BluetoothOn is { } b ? (b ? "on" : "off") : "none")} power={(PowerModeNow?.ToString() ?? "none")} " +
            $"nightlight={(NightLightOn ? "on" : "off")} screens={_night?.Screens ?? 0} quiet={(QuietOn ? QuietMinutesLeft + "min" : "off")} quietState={QuietState.IsQuiet} " +
            $"last=\"{_last}\" card={card}");
    }

    /// <summary>"quick-keepawake 60", "quick-keepawake off"; with nothing, what it is doing.</summary>
    string KeepAwakeFromPipe(string arg)
    {
        var a = arg.Trim().ToLowerInvariant();
        if (a.Length == 0) return State();
        if (a is "off" or "stop") return StopKeepAwake() ? "ok off" : "ok: wasn't on";
        if (!int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) || minutes < 1)
            return "error: usage quick-keepawake <minutes|off>";
        return StartKeepAwake(minutes) is int runs
            ? string.Create(CultureInfo.InvariantCulture, $"ok on minutes={runs}")
            : "error: Windows refused";
    }

    /// <summary>"quick-advance 20": as if 20 minutes had passed (fractions allowed), so the end can be watched without waiting.</summary>
    string Advance(string arg)
    {
        if (!double.TryParse(arg.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double minutes) || minutes <= 0)
            return "error: usage quick-advance <minutes>";
        if (!_clock.IsOn && !_quiet.IsOn) return "error: keep-awake and quiet are off";
        _clock.Advance(TimeSpan.FromMinutes(minutes));
        _quiet.Advance(TimeSpan.FromMinutes(minutes));
        Tick();
        QuietTick();
        return State();
    }

    /// <summary>
    /// "quick-run lock --dry", "quick-run sleep --dry": what it would do, without doing it. The pipe never really
    /// locks or sleeps, so those two refuse without --dry. "nightlight", "dnd" and "mic" switch for real without it.
    /// </summary>
    string RunFromPipe(string arg)
    {
        var words = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()).ToList();
        bool dry = words.RemoveAll(w => w is "--dry" or "dry") > 0;
        if (words.Count != 1) return "error: usage quick-run lock|sleep|nightlight|dnd|mic [--dry]";
        switch (words[0])
        {
            case "lock": return dry ? Lock(dry: true) : "error: the pipe never really locks; add --dry";
            case "sleep": return dry ? Sleep(dry: true) : "error: the pipe never really sleeps; add --dry";
            case "nightlight":
                if (dry) return $"would turn night light {OnOff(!NightLightOn)}";
                return SetNightLight(!NightLightOn) ? "nightlight=" + OnOff(NightLightOn) : "error: no screen could be tinted";
            case "dnd":
                if (dry) return QuietOn ? "would end quiet" : $"would start quiet for {QuietMinutes} min";
                ToggleQuiet();
                return "quiet=" + (QuietOn ? QuietMinutesLeft + "min" : "off") + " quietState=" + QuietState.IsQuiet;
            case "mic":
                if (dry) return MicMuted switch { true => "would unmute the mic", false => "would mute the mic", null => "no microphone" };
                ToggleMic();
                return "mic=" + (MicMuted switch { true => "muted", false => "live", null => "none" });
            default: return "error: usage quick-run lock|sleep|nightlight|dnd|mic [--dry]";
        }
    }

    public void Dispose()
    {
        _tick?.Stop();
        _pending?.Stop();
        _quietTick?.Stop();
        _clock.Stop();
        // The tint goes with the notch (it comes back on the next start if it was on).
        _night?.Dispose();
        // Never leave the PC held awake by a notch that has gone.
        if (_ctx is not null)
        {
            Hold(false);
            _ctx.Remove(ActivityId);
        }
        if (_mic is not null)
        {
            _mic.Changed -= OnMicChanged;
            _mic.Dispose();
        }
    }
}
