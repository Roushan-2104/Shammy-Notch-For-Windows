using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Shammy.Core.Activities;
using Shammy.Core.Productivity;
using Shammy.Infrastructure;
using Shammy.Shell;

namespace Shammy.Modules.Productivity.Alarms;

/// <summary>
/// Real alarms: keeps them in the productivity store, wakes when one is due (and on resume from sleep), rings
/// (a looping, swelling sound and a card that breaks through fullscreen) until Stop or Snooze, snoozes an
/// unanswered alarm by itself, and reports alarms the PC slept through. The schedule itself is pure logic in
/// <see cref="AlarmSchedule"/>; the views (the ringing notch, the Alarms screen, the snoozed and stopped pills)
/// work against <see cref="IAlarmHost"/>.
/// </summary>
internal sealed class AlarmController : IAlarmHost, IDisposable
{
    public const string Glyph = "";
    const string RingId = "productivity.alarm", MissedId = "productivity.alarm-missed",
        SnoozedId = "productivity.alarm-snoozed", StoppedId = "productivity.alarm-stopped";
    // Above notifications (80) and reminders: an alarm is the one thing that must be seen.
    // The snoozed pill sits under messages (50) and over downloads (40): it lasts minutes, they come and go.
    const int RingPriority = 95, MissedPriority = 60, StoppedPriority = 52, SnoozedPriority = 45;

    // Wake at least this often: a DispatcherTimer doesn't count time the PC spent asleep.
    static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    readonly ModuleContext _ctx;
    readonly Func<ProductivityStore> _store;
    readonly Action _save;
    readonly DispatcherTimer _wake = new(DispatcherPriority.Normal);
    readonly AlarmSound _sound = new();
    readonly AlarmsCard _card;
    readonly AlarmTile _tile;
    AlarmRingView? _ringCard;
    SnoozedPill? _snoozedPill;
    StoppedPill? _stoppedPill;
    Alarm? _ringing;
    bool _relevant;
    bool _disposed;

    public AlarmController(ModuleContext ctx, Func<ProductivityStore> store, Action save)
    {
        _ctx = ctx;
        _store = store;
        _save = save;
        _wake.Tick += (_, _) => Check();
        _card = new AlarmsCard(this);
        _tile = new AlarmTile(this);
    }

    List<Alarm> List => _store().Alarms;
    public IReadOnlyList<Alarm> Alarms => List;
    public Alarm? Ringing => _ringing;
    public int DefaultSnoozeMinutes => _ctx.Settings.SnoozeMinutes;

    public void Start()
    {
        MigrateReminders();
        _ctx.AddCard(new NotchCard
        {
            Id = "alarms",
            Title = "Alarms",
            Width = 176,
            Order = 55,
            Chip = true,
            IsRelevant = IsRelevant,
            Compact = _tile.View,
            Live = () => _tile.Live,
            Peek = _tile.Peek,
            Detail = OpenDetail,
        });
        _ctx.AddGlance("alarm", Glance);
        Views.Cards.CardPipes.Register(_ctx, "alarms", _card.DescribeCompact, _card.DescribeDetail);
        _ctx.AddPipeCommand("alarm-state", _ => State());
        _ctx.AddPipeCommand("alarm-open", _ =>
        {
            ShowList();
            return "ok";
        });
        _ctx.AddPipeCommand("alarm-check", _ =>
        {
            Check();
            return State();
        });
        _ctx.AddPipeCommand("alarm-ring-now", PipeRingNow);
        _ctx.AddPipeCommand("alarm-stop", _ => _ringing is null ? "error: nothing ringing" : Do(Stop));
        _ctx.AddPipeCommand("alarm-snooze", arg =>
        {
            if (_ringing is null) return "error: nothing ringing";
            Snooze(int.TryParse(arg.Trim(), out var m) ? m : DefaultSnoozeMinutes);
            return State();
        });
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.TimeChanged += OnTimeChanged;
        // Alarms that fell due while Shammy wasn't running ring (or show as missed) shortly after start.
        _wake.Interval = TimeSpan.FromSeconds(2);
        _wake.Start();
    }

    public void Dispose()
    {
        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.TimeChanged -= OnTimeChanged;
        _wake.Stop();
        _sound.Dispose();
    }

    string Do(Action action)
    {
        action();
        return State();
    }

    // Both arrive on a system thread; resume can come a few seconds before the clock and network settle.
    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _ctx.Ui.BeginInvoke(() => Check());
    }

    void OnTimeChanged(object? sender, EventArgs e) => _ctx.Ui.BeginInvoke(() => Check());

    // ---- The schedule ----------------------------------------------------------------------------

    /// <summary>Rings what's due, snoozes what rang out, reports what was slept through, then sleeps until the next thing.</summary>
    void Check()
    {
        if (_disposed) return;
        _wake.Stop();
        var now = DateTime.UtcNow;
        var zone = TimeZoneInfo.Local;
        bool changed = false;
        foreach (var a in List.ToList())
        {
            var check = AlarmSchedule.Check(a, now, zone);
            switch (check.Decision)
            {
                case AlarmDecision.Ring:
                    // Two at once: the second waits its turn (it still rings within the catch-up window).
                    if (_ringing is not null && _ringing != a) break;
                    AlarmSchedule.StartRinging(a, check.OccurrenceUtc, now);
                    changed = true;
                    StartRinging(a);
                    break;
                case AlarmDecision.KeepRinging:
                    // Shammy restarted mid-ring: pick it up again.
                    if (_ringing is null) StartRinging(a);
                    break;
                case AlarmDecision.AutoSnooze:
                    AlarmSchedule.Snooze(a, now, AlarmSchedule.SnoozeLength(a, DefaultSnoozeMinutes), auto: true);
                    changed = true;
                    if (_ringing == a) StopRinging();
                    _ctx.ShowMessage(Glyph, $"Alarm snoozed until {ClockPreference.Format(a.SnoozedUntilUtc!.Value.ToLocalTime())}", seconds: 6);
                    break;
                case AlarmDecision.Missed:
                    AlarmSchedule.MarkMissed(a, check.OccurrenceUtc);
                    changed = true;
                    if (_ringing == a) StopRinging();
                    ShowMissed(a);
                    break;
            }
        }
        if (changed) _save();
        Schedule();
    }

    void Schedule()
    {
        _wake.Stop();
        Refresh();
        if (_disposed) return;
        var now = DateTime.UtcNow;
        if (AlarmSchedule.NextWakeUtc(List, now, TimeZoneInfo.Local) is not { } next) return;
        var wait = next - now;
        // A second alarm due while one rings waits its turn: look again every couple of seconds, not in a tight loop.
        var min = _ringing is null ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromSeconds(2);
        _wake.Interval = wait < min ? min : wait > MaxWait ? MaxWait : wait;
        _wake.Start();
    }

    public DateTime? UpcomingLocal(Alarm a) =>
        AlarmSchedule.UpcomingUtc(a, DateTime.UtcNow, TimeZoneInfo.Local) is { } utc ? utc.ToLocalTime() : null;

    // ---- Ringing ---------------------------------------------------------------------------------

    void StartRinging(Alarm a)
    {
        _ringing = a;
        _sound.Start(_ctx.Settings.NotchSoundVolume, _ctx.Settings.AlarmGentleWake);
        // The ring around the notch is the wall's accent, like the pulsing ring on the ringing screen.
        _ctx.SetGlow(Ui.Tokens.Accent.Color);
        _ctx.Remove(StoppedId);
        SyncSnoozedPill();
        // If the card gets covered (a search, another card), the pill brings it back.
        _ctx.Post(new Activity(RingId, ActivityKind.Message, RingPriority,
            new MessagePayload(Glyph, $"{AlarmText.Time(a)} · {(a.Label.Length > 0 ? a.Label : "Alarm")} · tap to stop"))
        {
            BreaksFullscreen = _ctx.Settings.AlarmBreaksFullscreen,
            Click = ShowRingCard,
        });
        ShowRingCard();
        HostLinks.Say?.Invoke(Shammy.Core.OnlineVoice.SpokenPhrases.Alarm(AlarmText.Time(a), a.Label));
        _ctx.Info($"Alarm ringing: {AlarmText.Time(a)} {a.Label}");
    }

    void ShowRingCard()
    {
        if (_ringing is not { } a) return;
        var card = new AlarmRingView(a, AlarmSchedule.SnoozeLength(a, DefaultSnoozeMinutes), Stop, Snooze);
        _ringCard = card;
        bool seen = false;
        card.IsVisibleChanged += (_, _) =>
        {
            if (card.IsVisible) seen = true;
            // Replaced by something else while still ringing: come back once the notch is free again.
            else if (seen && _ringing is not null && _ringCard == card) After(TimeSpan.FromSeconds(4), () =>
            {
                if (_ringing is not null && _ringCard == card && !card.IsVisible && !Busy(_ctx.Notch.Mode)) ShowRingCard();
            });
        };
        // Not interactive: it must not take focus (a game, a call), and an outside click mustn't silence it.
        _ctx.ShowCard(card, AlarmRingView.CardWidth, AlarmRingView.CardHeight, interactive: false,
            breaksFullscreen: _ctx.Settings.AlarmBreaksFullscreen);
    }

    static bool Busy(NotchMode mode) =>
        mode is NotchMode.Listening or NotchMode.Circling or NotchMode.Thinking or NotchMode.Typing or NotchMode.Answer or NotchMode.Result;

    void StopRinging()
    {
        _sound.Stop();
        _ringing = null;
        var card = _ringCard;
        _ringCard = null;
        if (card?.Parent is not null) _ctx.CloseCard();
        _ctx.Remove(RingId);
        _ctx.SetGlow(null);
    }

    public void Stop()
    {
        if (_ringing is not { } a) return;
        AlarmSchedule.Stop(a);
        a.MissedUtc = null;
        StopRinging();
        _save();
        Schedule();
        ShowStopped();
    }

    /// <summary>The pet stretches and waves beside "Morning · 24°" (the weather glance's temperature, else the time).</summary>
    void ShowStopped()
    {
        var now = DateTime.Now;
        _stoppedPill = new StoppedPill(AlarmText.Greeting(now), AlarmText.Temperature(HostLinks.WeatherGlance()) ?? ClockPreference.Format(now));
        _ctx.Post(new Activity(StoppedId, ActivityKind.Custom, StoppedPriority, _stoppedPill, DateTime.UtcNow.AddSeconds(5)));
    }

    public void Snooze(int minutes)
    {
        if (_ringing is not { } a) return;
        AlarmSchedule.Snooze(a, DateTime.UtcNow, Math.Clamp(minutes, 1, 60), auto: false);
        StopRinging();
        _save();
        // Schedule() refreshes, which puts up the snoozed pill (the pet dozing, the countdown).
        Schedule();
    }

    /// <summary>The snoozed pill shows while any alarm is snoozed (and none rings); it survives a restart.</summary>
    void SyncSnoozedPill()
    {
        var snoozed = _ringing is null ? List.Where(a => a.IsSnoozed && a.Enabled).OrderBy(a => a.SnoozedUntilUtc).FirstOrDefault() : null;
        if (snoozed is null)
        {
            if (_snoozedPill is not null) _ctx.Remove(SnoozedId);
            _snoozedPill = null;
            return;
        }
        if (_snoozedPill?.Alarm == snoozed && _ctx.Get(SnoozedId) is not null) return;
        _snoozedPill = new SnoozedPill(snoozed, () => TurnOffSnoozed(snoozed));
        _ctx.Post(new Activity(SnoozedId, ActivityKind.Custom, SnoozedPriority, _snoozedPill) { Click = ShowList });
    }

    // ---- The Alarms screen ------------------------------------------------------------------------

    public Alarm Add(int hour, int minute, AlarmDays days, string label)
    {
        Add(new AlarmSpec(hour, minute, days, null, label.Trim()));
        return List.Last(a => a.Hour == hour && a.Minute == minute && a.Days == days);
    }

    public void Edit(Alarm a, int hour, int minute, AlarmDays days, string label)
    {
        if (!List.Contains(a)) return;
        if (_ringing == a) StopRinging();
        a.Hour = Math.Clamp(hour, 0, 23);
        a.Minute = Math.Clamp(minute, 0, 59);
        a.Days = days;
        a.Label = label.Trim();
        // A one-off rings the next time the clock shows its time.
        a.Date = null;
        a.Enabled = true;
        AlarmSchedule.Reset(a, DateTime.UtcNow);
        _save();
        Schedule();
    }

    public void SetDays(Alarm a, AlarmDays days)
    {
        if (!List.Contains(a) || a.Days == days) return;
        a.Days = days;
        if (days != AlarmDays.None) a.Date = null;
        // Days chosen on an alarm that's off mean "use it": turn it on, counting from now.
        if (a.Enabled && !a.IsRinging && !a.IsSnoozed) AlarmSchedule.Reset(a, DateTime.UtcNow);
        else if (!a.Enabled) SetEnabledQuiet(a);
        _save();
        Schedule();
    }

    public void TurnOffSnoozed(Alarm a)
    {
        if (!List.Contains(a) || !a.IsSnoozed) return;
        AlarmSchedule.Stop(a);
        _save();
        Schedule();
        _ctx.ShowMessage(Glyph, a.Repeats ? $"{AlarmText.Time(a)} is off for today" : $"{AlarmText.Time(a)} turned off", seconds: 2.5);
    }

    public void SetSnoozeMinutes(int minutes) => SetSetting(nameof(AppSettings.SnoozeMinutes), minutes);

    public bool GentleWake
    {
        get => _ctx.Settings.AlarmGentleWake;
        set => SetSetting(nameof(AppSettings.AlarmGentleWake), value);
    }

    public bool BreaksFullscreen
    {
        get => _ctx.Settings.AlarmBreaksFullscreen;
        set => SetSetting(nameof(AppSettings.AlarmBreaksFullscreen), value);
    }

    void SetSetting(string name, object value)
    {
        if (Settings.SettingsCatalog.Find(name) is { } info) Settings.SettingsCatalog.Set(_ctx.Settings, info, value);
    }

    void ShowMissed(Alarm a)
    {
        _ctx.Post(new Activity(MissedId, ActivityKind.Message, MissedPriority, new MessagePayload(Glyph, AlarmText.Missed(a), true),
            DateTime.UtcNow.AddSeconds(15))
        {
            Click = ShowList,
        });
        NotchSound.Play(Shammy.Core.Audio.PingKind.Alert);
        _ctx.Info($"Alarm missed: {AlarmText.Time(a)}");
    }

    // ---- Changes from the card and commands --------------------------------------------------------

    public void SetEnabled(Alarm a, bool on)
    {
        if (!List.Contains(a)) return;
        if (_ringing == a && !on) StopRinging();
        a.Enabled = on;
        if (on)
        {
            // A one-off whose day has gone rings the next time the clock shows its time, like a phone's.
            if (!a.Repeats && a.Date is { } d && d.Date.Add(a.TimeOfDay) <= DateTime.Now) a.Date = null;
            AlarmSchedule.Reset(a, DateTime.UtcNow);
        }
        else
        {
            a.SnoozedUntilUtc = null;
            a.RingingSinceUtc = null;
        }
        _save();
        Schedule();
    }

    public void Delete(Alarm a)
    {
        if (_ringing == a) StopRinging();
        if (!List.Remove(a)) return;
        _save();
        Schedule();
    }

    void ShowList()
    {
        foreach (var a in List) a.MissedUtc = null;
        _ctx.Remove(MissedId);
        _ctx.OpenCard("alarms");
        _save();
        Refresh();
    }

    FrameworkElement OpenDetail()
    {
        // Looking at the list is seeing the missed alarm.
        if (List.Any(a => a.MissedUtc is not null))
        {
            foreach (var a in List) a.MissedUtc = null;
            _ctx.Remove(MissedId);
            _save();
            _ctx.Ui.BeginInvoke(Refresh);
        }
        return _card.BuildDetail();
    }

    /// <summary>Runs an alarm command from the palette or voice.</summary>
    public CommandResult Run(ProductivityCommand c, bool typed)
    {
        switch (c.Action)
        {
            case ProductivityAction.SetAlarm when c.Alarm is { } spec:
                return Add(spec);
            case ProductivityAction.ShowAlarms:
                ShowList();
                int on = List.Count(a => a.Enabled);
                var next = List.Select(UpcomingLocal).Where(t => t is not null).Min();
                return new CommandResult(Glyph, "Alarms", List.Count == 0 ? "No alarms yet"
                    : $"{on} on{(next is { } n ? $" · next {ClockPreference.Format(n)}" : "")}");
            case ProductivityAction.StopAlarm:
                if (_ringing is not null)
                {
                    Stop();
                    return new CommandResult(Glyph, "Alarm stopped");
                }
                if (List.FirstOrDefault(a => a.IsSnoozed) is { } snoozed)
                {
                    AlarmSchedule.Stop(snoozed);
                    _save();
                    Schedule();
                    return new CommandResult(Glyph, "Snoozed alarm stopped", AlarmText.Time(snoozed));
                }
                return new CommandResult(Glyph, "No alarm is ringing", IsError: true);
            case ProductivityAction.SnoozeAlarm:
                if (_ringing is null) return new CommandResult(Glyph, "No alarm is ringing", IsError: true);
                int minutes = c.Duration > TimeSpan.Zero ? (int)Math.Ceiling(c.Duration.TotalMinutes) : AlarmSchedule.SnoozeLength(_ringing, DefaultSnoozeMinutes);
                Snooze(minutes);
                return new CommandResult(Glyph, $"Snoozed {minutes} min");
            case ProductivityAction.DeleteAlarm or ProductivityAction.DisableAlarms or ProductivityAction.EnableAlarms:
                return Manage(c);
        }
        return new CommandResult(Glyph, "Not supported yet", IsError: true);
    }

    CommandResult Add(AlarmSpec spec)
    {
        var nowUtc = DateTime.UtcNow;
        // The same alarm again turns the existing one on instead of making a twin.
        var same = List.FirstOrDefault(a => a.Hour == spec.Hour && a.Minute == spec.Minute && a.Days == spec.Days
                                            && a.Date?.Date == spec.Date?.Date && string.Equals(a.Label, spec.Label, StringComparison.OrdinalIgnoreCase));
        var alarm = same ?? new Alarm
        {
            Hour = spec.Hour,
            Minute = spec.Minute,
            Days = spec.Days,
            Date = spec.Date?.Date,
            Label = spec.Label,
            Created = DateTime.Now,
        };
        alarm.Enabled = true;
        AlarmSchedule.Reset(alarm, nowUtc);
        if (same is null) List.Add(alarm);
        _save();
        Schedule();
        var at = UpcomingLocal(alarm);
        var detail = at is { } t ? $"{AlarmText.Repeat(alarm, DateTime.Now, t)} · {AlarmText.Until(t - DateTime.Now)}" : AlarmText.Repeat(alarm.Days);
        if (alarm.Label.Length > 0) detail = $"{alarm.Label} · {detail}";
        return new CommandResult(Glyph, $"Alarm · {AlarmText.Time(alarm)}", detail);
    }

    CommandResult Manage(ProductivityCommand c)
    {
        List<Alarm> targets;
        if (c.All) targets = List.ToList();
        else if (c.Target is { } t) targets = List.Where(t.Matches).ToList();
        else if (c.Text.Length > 0) targets = List.Where(a => a.Label.Contains(c.Text, StringComparison.OrdinalIgnoreCase)).ToList();
        else if (c.Action == ProductivityAction.DisableAlarms && _ringing is not null)
        {
            // "turn off the alarm" while it rings means stop it.
            Stop();
            return new CommandResult(Glyph, "Alarm stopped");
        }
        else
        {
            var pool = c.Action == ProductivityAction.EnableAlarms ? List.Where(a => !a.Enabled) : c.Action == ProductivityAction.DisableAlarms ? List.Where(a => a.Enabled) : List;
            targets = pool.ToList();
            if (targets.Count > 1)
                return new CommandResult(Glyph, "Which alarm?", "Say the time, like “delete the 7 am alarm”", IsError: true);
        }
        if (targets.Count == 0)
        {
            string which = c.Target is { } tt ? (tt.Exact ? ClockPreference.Format(tt.Hour, tt.Minute) : $"{tt.Hour}:{tt.Minute:00}") : c.Text;
            return new CommandResult(Glyph, which.Length > 0 ? $"No alarm at {which}" : "No alarms", IsError: true);
        }

        string verb;
        switch (c.Action)
        {
            case ProductivityAction.DeleteAlarm:
                foreach (var a in targets)
                {
                    if (_ringing == a) StopRinging();
                    List.Remove(a);
                }
                verb = "Deleted";
                break;
            case ProductivityAction.DisableAlarms:
                foreach (var a in targets)
                {
                    if (_ringing == a) StopRinging();
                    a.Enabled = false;
                    a.SnoozedUntilUtc = null;
                    a.RingingSinceUtc = null;
                }
                verb = "Turned off";
                break;
            default:
                foreach (var a in targets.Where(a => !a.Enabled)) SetEnabledQuiet(a);
                verb = "Turned on";
                break;
        }
        _save();
        Schedule();
        return targets.Count == 1
            ? new CommandResult(Glyph, $"{verb} alarm {AlarmText.Time(targets[0])}", targets[0].Label.Length > 0 ? targets[0].Label : null)
            : new CommandResult(Glyph, $"{verb} {targets.Count} alarms");
    }

    void SetEnabledQuiet(Alarm a)
    {
        a.Enabled = true;
        if (!a.Repeats && a.Date is { } d && d.Date.Add(a.TimeOfDay) <= DateTime.Now) a.Date = null;
        AlarmSchedule.Reset(a, DateTime.UtcNow);
    }

    // ---- Migration -------------------------------------------------------------------------------

    /// <summary>
    /// Before real alarms, "set an alarm for 7" made a reminder titled "Alarm". Turn those into alarms (one-off, on
    /// its date; already fired ones come over switched off) and take them off the reminders list.
    /// </summary>
    void MigrateReminders()
    {
        var store = _store();
        var old = store.Reminders.Where(r => !r.Done && string.Equals(r.Text.Trim(), "Alarm", StringComparison.OrdinalIgnoreCase)).ToList();
        if (old.Count == 0) return;
        foreach (var r in old)
        {
            var alarm = new Alarm
            {
                Hour = r.Due.Hour,
                Minute = r.Due.Minute,
                Date = r.Due.Date,
                Created = r.Created,
                Enabled = !r.Fired,
                SinceUtc = r.Created == default ? DateTime.UtcNow : AlarmSchedule.ToUtc(r.Created, TimeZoneInfo.Local),
            };
            if (r.Fired) alarm.LastOccurrenceUtc = AlarmSchedule.ToUtc(r.Due, TimeZoneInfo.Local);
            store.Alarms.Add(alarm);
            store.Reminders.Remove(r);
        }
        _ctx.Info($"Moved {old.Count} alarm reminder(s) to alarms");
        _save();
    }

    // ---- Card, glance --------------------------------------------------------------------------------

    bool IsRelevant() => _ringing is not null || List.Any(a => a.MissedUtc is not null || a.IsSnoozed);

    void Refresh()
    {
        SyncSnoozedPill();
        _card.Refresh();
        _tile.Refresh();
        bool relevant = IsRelevant();
        if (relevant == _relevant) return;
        _relevant = relevant;
        _ctx.CardsChanged();
    }

    GlanceItem? Glance()
    {
        if (_ringing is { } r) return new GlanceItem($"Alarm {AlarmText.Time(r)}", NotchPalette.Alarm, 100, ShowRingCard);
        if (List.FirstOrDefault(a => a.MissedUtc is not null) is { } m) return new GlanceItem($"Missed {AlarmText.Time(m)}", NotchPalette.Alarm, 30, ShowList);
        // Tonight's or tomorrow morning's alarm, within 12 hours.
        var now = DateTime.Now;
        var next = List.Select(UpcomingLocal).Where(t => t is { } at && at - now < TimeSpan.FromHours(12)).Min();
        return next is { } n ? new GlanceItem($"Alarm {ClockPreference.Format(n)}", NotchPalette.Alarm, 5, ShowList) : null;
    }

    // ---- Test pipes ------------------------------------------------------------------------------

    /// <summary>
    /// "alarm-ring-now [label]" adds a one-off alarm that is due now, so it rings straight away;
    /// "alarm-ring-now late 15" makes one 15 minutes overdue (missed after sleep; 5 would still ring).
    /// </summary>
    string PipeRingNow(string arg)
    {
        var parts = arg.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        int late = 0;
        string label = arg.Trim();
        if (parts.Length > 0 && parts[0] == "late")
        {
            if (parts.Length < 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out late) || late is < 0 or > 600)
                return "error: usage alarm-ring-now late <minutes>";
            label = $"Test ({late} min late)";
        }
        var due = DateTime.Now.AddMinutes(-late);
        var alarm = new Alarm
        {
            Hour = due.Hour,
            Minute = due.Minute,
            Date = due.Date,
            Label = label.Length > 0 ? label : "Test alarm",
            Created = DateTime.Now,
            SinceUtc = AlarmSchedule.ToUtc(due.AddMinutes(-1), TimeZoneInfo.Local),
        };
        List.Add(alarm);
        _save();
        Check();
        return State();
    }

    string State()
    {
        var now = DateTime.Now;
        string Describe(Alarm a)
        {
            var up = UpcomingLocal(a);
            return $"[{a.Id[..6]} {AlarmText.Time(a, use24Hour: true)} {AlarmText.Repeat(a.Days).Replace(' ', '_')}" +
                   $"{(a.Date is { } d ? " " + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "")}" +
                   $"{(a.Label.Length > 0 ? $" '{a.Label}'" : "")} {(a.Enabled ? "on" : "off")}" +
                   $"{(up is { } u ? $" next={u:yyyy-MM-dd HH:mm}" : "")}" +
                   $"{(a.IsRinging ? " RINGING" : "")}{(a.SnoozedUntilUtc is { } s ? $" snoozed={s.ToLocalTime():HH:mm:ss}" : "")}" +
                   $"{(a.AutoSnoozes > 0 ? $" auto={a.AutoSnoozes}" : "")}{(a.MissedUtc is not null ? " MISSED" : "")}]";
        }
        return $"alarms={List.Count} ringing={(_ringing is { } r ? AlarmText.Time(r, use24Hour: true) : "-")} sound={_sound.IsPlaying} " +
               $"card={(_ringCard?.IsVisible == true ? "shown" : _ringCard is null ? "-" : "hidden")} " +
               $"pill={(_ctx.Get(SnoozedId) is not null ? _snoozedPill?.Describe() : _ctx.Get(StoppedId) is not null ? _stoppedPill?.Describe() : "-")} " +
               $"snooze={DefaultSnoozeMinutes} gentle={GentleWake} breaks={BreaksFullscreen} " +
               $"wake={(_wake.IsEnabled ? $"{_wake.Interval.TotalSeconds:0.#}s" : "off")} now={now:HH:mm:ss} " +
               string.Join(" ", List.Select(Describe));
    }

    static void After(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }
}
