using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Core.Activities;
using Shammy.Core.Connect;
using Shammy.Core.Productivity;
using Shammy.Infrastructure;

namespace Shammy.Modules.Productivity;

/// <summary>
/// Timers, a stopwatch, pomodoro cycles, focus sessions, reminders and quick notes, all from typed or
/// spoken commands ("timer 25m", "remind me at 6pm to take medicine", "note: buy milk").
/// Running clocks live in the compact ears; reminders and notes persist and show on the Notes card.
/// </summary>
internal sealed class ProductivityModule : IShammyModule, ITextCommand, ICommandPreview, ITimerCardHost, Connect.INotesLink
{
    // Above downloads (40), below messages (50): a running timer yields to anything more urgent.
    const int ClockPriority = 45;
    const int AlertPriority = 85;
    const string AlertId = "productivity.alert";

    const string TimerGlyph = "\uE121", StopwatchGlyph = "\uE916", PomodoroGlyph = "\uE945", BreakGlyph = "\uE76E",
        FocusGlyph = "\uE708", BellGlyph = "\uEA8F", NoteGlyph = "\uE70B", PauseGlyph = "\uE769", PlayGlyph = "\uE768";

    static readonly TimeSpan AlertTime = TimeSpan.FromSeconds(8);
    // Settings: ReminderCardSeconds and SnoozeMinutes.
    TimeSpan CardTime => TimeSpan.FromSeconds(_ctx.Settings.ReminderCardSeconds);
    TimeSpan SnoozeTime => TimeSpan.FromMinutes(_ctx.Settings.SnoozeMinutes);

    readonly List<RunningClock> _clocks = new();
    readonly Queue<ReminderItem> _cardQueue = new();
    readonly FocusSession _focus = new();
    // Normal, not the default Background: a busy UI thread (animations, a flood of window messages) starves
    // Background timers, and a timer or alarm that doesn't ring is worse than one that ticks a little late.
    readonly DispatcherTimer _tick = new(DispatcherPriority.Normal);
    readonly DispatcherTimer _reminders = new(DispatcherPriority.Normal);
    readonly DispatcherTimer _glowOff = new(DispatcherPriority.Normal) { Interval = AlertTime };
    readonly DispatcherTimer _cardClose = new(DispatcherPriority.Normal);

    ModuleContext _ctx = null!;
    ProductivityStore _store = new();
    NotesCard _notes = null!;
    TimerCard _timerCard = null!;
    bool _timerCardRelevant;
    ReminderCard? _card;
    Alarms.AlarmController _alarms = null!;
    bool _glowing;
    int _nextId;

    public string Name => "productivity";

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        _store = ctx.Load<ProductivityStore>();
        // A hand-edited file can say "Reminders": null.
        _store.Reminders ??= new();
        _store.Notes ??= new();
        _store.Alarms ??= new();
        // Fired reminders stay listed for a week, then quietly go.
        _store.Reminders.RemoveAll(r => r.FiredAt is { } fired && fired < DateTime.Now.AddDays(-7)
                                        || r.DoneAt is { } done && done < DateTime.Now.AddDays(-7));

        ctx.Services.Notifications.Arrived += OnNotificationArrived;
        _tick.Tick += (_, _) => OnTick();
        _reminders.Tick += (_, _) => OnReminderTick();
        _glowOff.Tick += (_, _) => ClearGlow();
        _cardClose.Tick += (_, _) => CloseCard();

        _notes = new NotesCard(() => _store, ToggleDone, Delete, text => AddNote(text))
        {
            // Today's calendar sits with the day's notes and reminders.
            Events = () => Calendar.CalendarModule.Agenda,
            Join = JoinMeeting,
        };
        ctx.AddCard(new NotchCard
        {
            Id = "notes",
            Title = "Notes",
            Width = 176,
            Order = 45,
            Chip = true,
            IsRelevant = ReminderDueToday,
            Live = () => _notes.Live,
            Peek = _notes.Peek,
            Compact = _notes.Compact,
            Detail = _notes.BuildDetail,
        });
        _lastRelevant = ReminderDueToday();
        ctx.AddCommand(this);

        // v2: the timer card shows the live clocks (relevant while one runs).
        _timerCard = new TimerCard(this);
        _timerCard.Changed += UpdateCard;
        ctx.AddCard(new NotchCard
        {
            Id = "timer",
            Title = "Timers",
            Width = 176,
            Compact = _timerCard.Compact,
            Detail = _timerCard.BuildDetail,
            Order = 25,
            // The quick-start chips live in the detail, so it must open with no timer running (off the home row).
            Chip = true,
            IsRelevant = () => _timerCard.IsRelevant,
            Live = () => _timerCard.Live,
            Peek = _timerCard.Peek,
        });
        ctx.AddGlance("timer", _timerCard.Glance);
        Views.Cards.CardPipes.Register(ctx, "timer", _timerCard.DescribeCompact, _timerCard.DescribeDetail);

        ctx.AddPipeCommand("productivity-timer", PipeTimer);
        ctx.AddPipeCommand("productivity-remind-now", PipeRemindNow);
        ctx.AddPipeCommand("productivity-state", _ => State());
        ctx.AddPipeCommand("notes-open", _ => _ctx.OpenCard("notes") ? "ok" : "error: no notes card");
        ctx.AddPipeCommand("productivity-run", text =>
        {
            var r = Run(text, typed: false);
            return $"{(r.IsError ? "error" : "ok")}: {r.Title}{(r.Detail is null ? "" : " | " + r.Detail)}";
        });

        // Reminders that fell due while Shammy wasn't running fire shortly after start.
        _reminders.Interval = TimeSpan.FromSeconds(3);
        _reminders.Start();
        Connect.Links.Notes = this;

        // Real alarms (separate from reminders since v2): ringing, snooze, missed-after-sleep, the Alarms card.
        _alarms = new Alarms.AlarmController(ctx, () => _store, Save);
        _alarms.Start();
    }

    /// <summary>Only links the calendar parser recognised as Meet/Zoom/Teams are ever opened.</summary>
    void JoinMeeting(Shammy.Core.Calendar.CalendarOccurrence o)
    {
        if (o.Link is not { } link || Shammy.Core.Calendar.MeetingLinks.Classify(link) is null) return;
        if (!_ctx.Open(link)) _ctx.ShowMessage("", "Couldn't open the meeting link", error: true);
    }

    // ---- For other modules (Connect.INotesLink): save as note, remind me later, today's reminders ------

    IReadOnlyList<ReminderItem> Connect.INotesLink.Reminders => _store.Reminders;
    IReadOnlyList<NoteItem> Connect.INotesLink.Notes => _store.Notes;
    CommandResult Connect.INotesLink.SaveNote(string text) => AddNote(text);
    CommandResult Connect.INotesLink.AddReminder(DateTime due, string text) => AddReminder(due, text);

    public void Dispose()
    {
        if (Connect.Links.Notes == this) Connect.Links.Notes = null;
        _alarms?.Dispose();
        _tick.Stop();
        _reminders.Stop();
        _cardClose.Stop();
        if (_ctx is null) return;
        _ctx.Services.Notifications.Arrived -= OnNotificationArrived;
        foreach (var c in _clocks) _ctx.Remove(c.ActivityId);
        _clocks.Clear();
        _focus.Dispose();
        ClearGlow();
    }

    // ---- Commands --------------------------------------------------------------------------------

    public bool CanHandle(string text) => ProductivityParser.Parse(text, DateTime.Now) is not null;

    public Task<CommandResult> RunAsync(string text) => Task.FromResult(Run(text, typed: true));

    public CommandPreview? Preview(string text)
    {
        var c = ProductivityParser.Parse(text, DateTime.Now);
        var what = c is null ? null : Shammy.Core.Palette.PaletteQuery.DescribeProductivity(c, DateTime.Now);
        if (c is null || what is null) return null;
        var glyph = c.Action switch
        {
            ProductivityAction.Remind or ProductivityAction.ReminderNeedsTime => BellGlyph,
            ProductivityAction.Note or ProductivityAction.ShowNotes => NoteGlyph,
            ProductivityAction.StartFocus or ProductivityAction.StopFocus => FocusGlyph,
            ProductivityAction.StartPomodoro or ProductivityAction.StopPomodoro or ProductivityAction.SkipPomodoro => PomodoroGlyph,
            ProductivityAction.SetAlarm or ProductivityAction.ShowAlarms or ProductivityAction.DeleteAlarm or ProductivityAction.DisableAlarms
                or ProductivityAction.EnableAlarms or ProductivityAction.StopAlarm or ProductivityAction.SnoozeAlarm => Alarms.AlarmController.Glyph,
            ProductivityAction.StartStopwatch or ProductivityAction.StopStopwatch or ProductivityAction.PauseStopwatch
                or ProductivityAction.ResumeStopwatch => StopwatchGlyph,
            _ => TimerGlyph,
        };
        return new CommandPreview(glyph, what);
    }

    CommandResult Run(string text, bool typed)
    {
        if (ProductivityParser.Parse(text, DateTime.Now) is not { } c)
            return new CommandResult(TimerGlyph, "Not a timer, reminder or note", text, IsError: true);

        return c.Action switch
        {
            ProductivityAction.StartTimer => StartTimer(c.Duration, c.Text),
            ProductivityAction.StopTimer or ProductivityAction.PauseTimer or ProductivityAction.ResumeTimer => ControlTimers(c),
            ProductivityAction.StartStopwatch => StartStopwatch(),
            ProductivityAction.StopStopwatch => StopStopwatch(),
            ProductivityAction.PauseStopwatch => PauseClock(Find(ClockKind.Stopwatch), "Stopwatch", pause: true),
            ProductivityAction.ResumeStopwatch => PauseClock(Find(ClockKind.Stopwatch), "Stopwatch", pause: false),
            ProductivityAction.StartPomodoro => StartPomodoro(),
            ProductivityAction.StopPomodoro => StopPomodoro(),
            ProductivityAction.SkipPomodoro => SkipPomodoro(),
            ProductivityAction.StartFocus => StartFocus(c.Duration),
            ProductivityAction.StopFocus => StopFocus(),
            ProductivityAction.Remind => AddReminder(c.Due, c.Text),
            ProductivityAction.ReminderNeedsTime when c.Text == "Alarm" => new CommandResult(BellGlyph, "What time should it ring?",
                "Try \u201Calarm at 7 am every weekday\u201D", IsError: true),
            ProductivityAction.ReminderNeedsTime => new CommandResult(BellGlyph, "When should I remind you?",
                "Try \u201Cremind me in 20 minutes to \u2026\u201D", IsError: true),
            ProductivityAction.SetAlarm or ProductivityAction.ShowAlarms or ProductivityAction.DeleteAlarm or ProductivityAction.DisableAlarms
                or ProductivityAction.EnableAlarms or ProductivityAction.StopAlarm or ProductivityAction.SnoozeAlarm => _alarms.Run(c, typed),
            ProductivityAction.Note => AddNote(c.Text),
            ProductivityAction.ShowNotes => ShowNotes(typed),
            _ => new CommandResult(TimerGlyph, "Not supported yet", IsError: true),
        };
    }

    CommandResult StartTimer(TimeSpan duration, string label)
    {
        AddClock(ClockKind.Timer, label, TimerGlyph, duration);
        var ends = $"Ends {ClockText(DateTime.Now + duration)}";
        return new CommandResult(TimerGlyph, $"Timer \u00B7 {TimeFormat.Countdown(duration)}", label.Length > 0 ? $"{label} \u00B7 {ends}" : ends);
    }

    CommandResult ControlTimers(ProductivityCommand c)
    {
        // "pause timer" with only a pomodoro or focus session running means that one.
        var candidates = _clocks.Where(k => k.Kind == ClockKind.Timer).ToList();
        if (candidates.Count == 0 && c.Text.Length == 0) candidates = _clocks.Where(k => k.IsCountdown).ToList();
        if (c.Text.Length > 0)
        {
            candidates = candidates.Where(k => k.Label.Contains(c.Text, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count == 0) return new CommandResult(TimerGlyph, $"No timer called {c.Text}", IsError: true);
        }
        if (candidates.Count == 0) return new CommandResult(TimerGlyph, "No timer running", IsError: true);

        var now = DateTime.UtcNow;
        if (c.Action == ProductivityAction.StopTimer)
        {
            var stop = c.All ? candidates : new List<RunningClock> { candidates[^1] };
            foreach (var k in stop) RemoveClock(k);
            return stop.Count > 1
                ? new CommandResult(TimerGlyph, $"Stopped {stop.Count} timers")
                : new CommandResult(TimerGlyph, $"{ClockName(stop[0])} stopped", $"{stop[0].Display(now)} was left");
        }

        bool pause = c.Action == ProductivityAction.PauseTimer;
        // Pause the newest one that is running; resume the newest one that is paused.
        var pick = candidates.Where(k => k.Paused != pause).ToList();
        if (pick.Count == 0) return new CommandResult(TimerGlyph, pause ? "Already paused" : "Already running");
        var targets = c.All ? pick : new List<RunningClock> { pick[^1] };
        foreach (var k in targets) SetPaused(k, pause);
        var first = targets[^1];
        return new CommandResult(pause ? PauseGlyph : PlayGlyph,
            targets.Count > 1 ? $"{(pause ? "Paused" : "Resumed")} {targets.Count} timers" : $"{ClockName(first)} {(pause ? "paused" : "resumed")}",
            $"{first.Display(now)} left");
    }

    CommandResult StartStopwatch()
    {
        if (Find(ClockKind.Stopwatch) is { } existing)
        {
            if (existing.Paused) return PauseClock(existing, "Stopwatch", pause: false);
            return new CommandResult(StopwatchGlyph, $"Stopwatch \u00B7 {existing.Display(DateTime.UtcNow)}", "Already running");
        }
        AddClock(ClockKind.Stopwatch, "", StopwatchGlyph, TimeSpan.Zero);
        return new CommandResult(StopwatchGlyph, "Stopwatch started", "Say \u201Cstop stopwatch\u201D to stop it");
    }

    CommandResult StopStopwatch()
    {
        if (Find(ClockKind.Stopwatch) is not { } watch) return new CommandResult(StopwatchGlyph, "No stopwatch running", IsError: true);
        var time = watch.Display(DateTime.UtcNow);
        RemoveClock(watch);
        return new CommandResult(StopwatchGlyph, $"Stopwatch \u00B7 {time}", "Stopped");
    }

    CommandResult PauseClock(RunningClock? clock, string what, bool pause)
    {
        if (clock is null) return new CommandResult(StopwatchGlyph, $"No {what.ToLowerInvariant()} running", IsError: true);
        SetPaused(clock, pause);
        return new CommandResult(pause ? PauseGlyph : PlayGlyph, $"{what} {(pause ? "paused" : "resumed")}", clock.Display(DateTime.UtcNow));
    }

    CommandResult StartPomodoro()
    {
        if (Find(ClockKind.Pomodoro) is { } existing)
            return new CommandResult(PomodoroGlyph, $"Pomodoro \u00B7 {existing.Label}", $"{existing.Display(DateTime.UtcNow)} left");
        AddClock(ClockKind.Pomodoro, Pomodoro.Label(0), PomodoroGlyph, Pomodoro.FocusLength);
        PomodoroFocusSession(PomodoroPhase.Focus, Pomodoro.FocusLength);
        return new CommandResult(PomodoroGlyph, "Pomodoro started",
            $"{TimeFormat.Describe(Pomodoro.FocusLength)} focus, then a {TimeFormat.Describe(Pomodoro.ShortBreakLength)} break");
    }

    CommandResult StopPomodoro()
    {
        if (Find(ClockKind.Pomodoro) is not { } pomodoro) return new CommandResult(PomodoroGlyph, "No pomodoro running", IsError: true);
        int rounds = (pomodoro.PomodoroStep + 1) / 2;
        RemoveClock(pomodoro);
        return new CommandResult(PomodoroGlyph, "Pomodoro stopped", rounds == 1 ? "1 focus round done" : $"{rounds} focus rounds done");
    }

    CommandResult SkipPomodoro()
    {
        if (Find(ClockKind.Pomodoro) is not { } pomodoro) return new CommandResult(PomodoroGlyph, "No pomodoro running", IsError: true);
        NextPomodoroPhase(pomodoro);
        return new CommandResult(pomodoro.Glyph, $"Pomodoro \u00B7 {pomodoro.Label}", TimeFormat.Describe(pomodoro.Total));
    }

    // Notifications that arrived while a focus clock ran, counted as they come: the history only keeps the last
    // few dozen, so a long focus would undercount from it.
    int _focusArrived;
    readonly List<string> _focusApps = new();

    void OnNotificationArrived(Notifications.ShammyNotification n)
    {
        if (!_ctx.Ui.CheckAccess())
        {
            _ctx.Ui.BeginInvoke(() => OnNotificationArrived(n));
            return;
        }
        if (Find(ClockKind.Focus) is null) return;
        _focusArrived++;
        // A handful of app names is all the summary says.
        if (_focusApps.Count < 20 && !string.IsNullOrWhiteSpace(n.AppName)) _focusApps.Add(n.AppName);
    }

    /// <summary>"3 messages waited · WhatsApp, Gmail": the notifications that came in since focus began.</summary>
    string? FocusWaited() => Core.Productivity.FocusSummary.Waited(_focusArrived, _focusApps);

    CommandResult StartFocus(TimeSpan duration)
    {
        if (Find(ClockKind.Focus) is { } old) RemoveClock(old);
        _focusArrived = 0;
        _focusApps.Clear();
        // Optional music for the session (Settings › Time & units › Focus music).
        if (_ctx.Settings.FocusMusic && !_ctx.Services.Media.IsPlaying) HostLinks.RunText?.Invoke("play lofi focus music on youtube music");
        AddClock(ClockKind.Focus, "Focus", FocusGlyph, duration);
        bool windows = _focus.TryStart(DateTimeOffset.Now + duration);
        _ctx.Info($"Focus for {TimeFormat.Describe(duration)}; Windows focus session: {_focus.Status}");
        return new CommandResult(FocusGlyph, $"Focus \u00B7 {TimeFormat.Describe(duration)}",
            windows ? "Windows focus is on too" : $"Until {ClockText(DateTime.Now + duration)}");
    }

    CommandResult StopFocus()
    {
        if (Find(ClockKind.Focus) is not { } focus)
        {
            _focus.End();
            return new CommandResult(FocusGlyph, "No focus session", IsError: true);
        }
        var spent = focus.Elapsed(DateTime.UtcNow);
        var waited = FocusWaited();
        RemoveClock(focus);
        var focused = spent.TotalMinutes >= 1 ? $"Focused {TimeFormat.Describe(TimeSpan.FromMinutes(Math.Floor(spent.TotalMinutes)))}" : null;
        return new CommandResult(FocusGlyph, "Focus ended", focused is null ? waited : waited is null ? focused : $"{focused} · {waited}");
    }

    CommandResult AddReminder(DateTime due, string text)
    {
        var now = DateTime.Now;
        var item = new ReminderItem { Text = text, Due = due, Created = now };
        _store.Reminders.Add(item);
        Save();
        ScheduleReminders();
        return new CommandResult(BellGlyph, "Reminder set", $"{TimeFormat.When(due, now, Kit.CardKit.H24)} \u00B7 {item.Title}");
    }

    CommandResult AddNote(string text)
    {
        _store.Notes.Add(new NoteItem { Text = text, Created = DateTime.Now });
        Save();
        return new CommandResult(NoteGlyph, "Saved note", text);
    }

    CommandResult ShowNotes(bool typed)
    {
        // Waits for the command's confirmation card to clear, then opens the Notes detail in the notch.
        _ctx.OpenCard("notes");
        int upcoming = _store.Reminders.Count(r => !r.Fired), notes = _store.Notes.Count;
        return new CommandResult(NoteGlyph, "Notes",
            $"{notes} {(notes == 1 ? "note" : "notes")} \u00B7 {upcoming} upcoming {(upcoming == 1 ? "reminder" : "reminders")}");
    }

    // ---- Clocks ----------------------------------------------------------------------------------

    RunningClock AddClock(ClockKind kind, string label, string glyph, TimeSpan total)
    {
        var clock = new RunningClock(++_nextId, kind, label, glyph, total);
        _clocks.Add(clock);
        PostClock(clock);
        ScheduleTick();
        return clock;
    }

    void PostClock(RunningClock clock)
    {
        clock.Content.TogglePause = () => SetPaused(clock, !clock.Paused);
        _ctx.Post(new Activity(clock.ActivityId, ActivityKind.Custom, ClockPriority, clock.Content)
        {
            Click = () => SetPaused(clock, !clock.Paused),
        });
    }

    void RemoveClock(RunningClock clock)
    {
        _clocks.Remove(clock);
        _ctx.Remove(clock.ActivityId);
        if (clock.Kind == ClockKind.Focus) _focus.End();
        // A pomodoro's Windows focus session ends with it, unless a separate focus session still runs.
        if (clock.Kind == ClockKind.Pomodoro && Find(ClockKind.Focus) is null) _focus.End();
        ScheduleTick();
    }

    /// <summary>
    /// Pomodoro focus phases start a Windows focus session (Do Not Disturb) like "focus" does; breaks end it.
    /// A separate "focus" session that's running owns the Windows session and is left alone.
    /// </summary>
    void PomodoroFocusSession(PomodoroPhase phase, TimeSpan length)
    {
        if (Find(ClockKind.Focus) is not null) return;
        if (phase == PomodoroPhase.Focus)
        {
            _focus.TryStart(DateTimeOffset.Now + length);
            _ctx.Info($"Pomodoro focus; Windows focus session: {_focus.Status}");
        }
        else _focus.End();
    }

    /// <summary>A focus clock or a pomodoro's focus phase keeps the notch quiet (see QuietRules.FocusQuiet).</summary>
    void UpdateQuiet()
    {
        var pomodoro = Find(ClockKind.Pomodoro);
        PomodoroPhase? phase = pomodoro is null ? null : Pomodoro.PhaseAt(pomodoro.PomodoroStep);
        QuietState.Set(QuietReason.Focus, QuietRules.FocusQuiet(Find(ClockKind.Focus) is not null, phase));
    }

    void SetPaused(RunningClock clock, bool pause)
    {
        var now = DateTime.UtcNow;
        bool changed = clock.Paused != pause;
        if (pause) clock.Pause(now);
        else clock.Resume(now);
        clock.Content.Refresh(now);
        if (changed) WindowsFocusFollows(clock, pause);
        ScheduleTick();
    }

    /// <summary>
    /// Windows' focus session can't pause: pausing the clock that owns it ends it, and resuming starts it again
    /// for the time that's left. A focus clock owns it; a pomodoro does in a focus phase when no focus clock runs.
    /// </summary>
    void WindowsFocusFollows(RunningClock clock, bool paused)
    {
        bool owns = clock.Kind == ClockKind.Focus
                    || clock.Kind == ClockKind.Pomodoro && Find(ClockKind.Focus) is null && Pomodoro.PhaseAt(clock.PomodoroStep) == PomodoroPhase.Focus;
        if (!owns) return;
        if (paused) _focus.End();
        else if (clock.Remaining(DateTime.UtcNow) is var left && left > TimeSpan.Zero) _focus.TryStart(DateTimeOffset.Now + left);
        _ctx.Info($"{ClockName(clock)} {(paused ? "paused" : "resumed")}; Windows focus session: {_focus.Status}");
    }

    RunningClock? Find(ClockKind kind) => _clocks.LastOrDefault(c => c.Kind == kind);

    static string ClockName(RunningClock c) => c.Kind switch
    {
        ClockKind.Pomodoro => "Pomodoro",
        ClockKind.Focus => "Focus",
        _ => c.Label.Length > 0 ? $"Timer \u00B7 {c.Label}" : "Timer",
    };

    void OnTick()
    {
        var now = DateTime.UtcNow;
        foreach (var clock in _clocks.ToList())
        {
            if (clock.IsCountdown && !clock.Paused && clock.Remaining(now) <= TimeSpan.Zero)
            {
                Finish(clock);
            }
            else if (clock.Content.NeedsRebuild(now))
            {
                clock.Content = new ClockContent(clock);
                PostClock(clock);
            }
            else
            {
                clock.Content.Refresh(now);
            }
        }
        ScheduleTick();
    }

    // ---- Timer card (ITimerCardHost) -------------------------------------------------------------

    IReadOnlyList<RunningClock> ITimerCardHost.Clocks => _clocks;

    void ITimerCardHost.TogglePause(RunningClock clock)
    {
        if (_clocks.Contains(clock)) SetPaused(clock, !clock.Paused);
    }

    void ITimerCardHost.Stop(RunningClock clock)
    {
        if (_clocks.Contains(clock)) RemoveClock(clock);
    }

    void ITimerCardHost.Extend(RunningClock clock, TimeSpan more)
    {
        if (!_clocks.Contains(clock) || !clock.IsCountdown) return;
        clock.Extend(more);
        clock.Content.Refresh(DateTime.UtcNow);
        ScheduleTick();
    }

    void ITimerCardHost.QuickStart(TimeSpan duration) => StartTimer(duration, "");

    void UpdateCard()
    {
        if (_timerCard is null) return;
        _timerCard.Refresh();
        if (_timerCard.IsRelevant == _timerCardRelevant) return;
        _timerCardRelevant = _timerCard.IsRelevant;
        _ctx.CardsChanged();
    }

    /// <summary>Ticks land just after the next second boundary of whichever clock changes first; off when all are paused.</summary>
    void ScheduleTick()
    {
        _tick.Stop();
        // Every clock change and every tick passes through here, so the timer card and quiet mode follow along.
        UpdateCard();
        UpdateQuiet();
        var now = DateTime.UtcNow;
        var running = _clocks.Where(c => !c.Paused).ToList();
        if (running.Count == 0) return;
        _tick.Interval = running.Min(c => c.UntilNextSecond(now)) + TimeSpan.FromMilliseconds(15);
        _tick.Start();
    }

    void Finish(RunningClock clock)
    {
        switch (clock.Kind)
        {
            case ClockKind.Pomodoro:
                NextPomodoroPhase(clock);
                var phase = Pomodoro.PhaseAt(clock.PomodoroStep);
                var next = phase switch
                {
                    PomodoroPhase.Focus => "Back to focus",
                    PomodoroPhase.LongBreak => "Long break",
                    _ => "Break time",
                };
                Alert(clock.Glyph, $"{next} \u00B7 {TimeFormat.Describe(clock.Total)}", next, TimeFormat.Describe(clock.Total));
                break;
            case ClockKind.Focus:
                _timerCard.ShowDone("Focus");
                var waited = FocusWaited();
                RemoveClock(clock);
                Alert(FocusGlyph, $"Focus done \u00B7 {TimeFormat.Describe(clock.Total)}" + (waited is null ? "" : $" \u00B7 {waited}"), "Focus done",
                    waited is null ? TimeFormat.Describe(clock.Total) : waited);
                break;
            default:
                _timerCard.ShowDone(clock.Label);
                RemoveClock(clock);
                Alert(TimerGlyph, $"Timer done \u00B7 {(clock.Label.Length > 0 ? clock.Label : TimeFormat.Describe(clock.Total))}",
                    clock.Label.Length > 0 ? clock.Label : "Time's up", "0:00");
                break;
        }
    }

    void NextPomodoroPhase(RunningClock clock)
    {
        int step = clock.PomodoroStep + 1;
        var phase = Pomodoro.PhaseAt(step);
        clock.PomodoroStep = step;
        clock.Restart(Pomodoro.Length(phase), Pomodoro.Label(step), phase == PomodoroPhase.Focus ? PomodoroGlyph : BreakGlyph);
        // The label width changed, so the ears need building again.
        clock.Content = new ClockContent(clock);
        PostClock(clock);
        PomodoroFocusSession(phase, clock.Total);
        ScheduleTick();
    }

    // ---- Alerts ----------------------------------------------------------------------------------

    /// <param name="petLabel">What the pet's notch says ("Tea", "Break time"), with <paramref name="petValue"/> on the right.</param>
    void Alert(string glyph, string text, string? petLabel = null, string petValue = "")
    {
        // A clock you set running out is worth interrupting a fullscreen game or video for. The pet wears it
        // (jumping, confetti, an accent Dismiss circle); without a label it stays a plain message.
        object payload = petLabel is null
            ? new MessagePayload(glyph, text)
            : new TimerDoneContent(glyph, text, petLabel, petValue, () => _ctx.Remove(AlertId));
        _ctx.Post(new Activity(AlertId, petLabel is null ? ActivityKind.Message : ActivityKind.Custom, AlertPriority, payload, DateTime.UtcNow + AlertTime)
        {
            Click = () => _ctx.Remove(AlertId),
            BreaksFullscreen = true,
        });
        Glow();
        Chime();
        HostLinks.Say?.Invoke(Shammy.Core.OnlineVoice.SpokenPhrases.Clock(petLabel));
    }

    void Glow()
    {
        if (Application.Current.Resources["AccentBrush"] is SolidColorBrush accent) _ctx.SetGlow(accent.Color);
        _glowing = true;
        _glowOff.Stop();
        _glowOff.Start();
    }

    void ClearGlow()
    {
        _glowOff.Stop();
        if (!_glowing) return;
        _glowing = false;
        _ctx.SetGlow(null);
    }

    void Chime()
    {
        // Shammy's two-note chime instead of Windows' asterisk sound.
        NotchSound.Play(Shammy.Core.Audio.PingKind.Alert);
    }

    // ---- Reminders -------------------------------------------------------------------------------

    void ScheduleReminders()
    {
        _reminders.Stop();
        var next = _store.Reminders.Where(r => !r.Fired && !r.Done).Select(r => (DateTime?)r.Due).Min();
        // The Notes card is relevant on a day with an open reminder; a fired one still open stops counting at midnight.
        if (next is null && ReminderDueToday()) next = DateTime.Today.AddDays(1);
        if (next is not { } due) return;
        // Wake at least every 30 s: a DispatcherTimer doesn't count time the PC spent asleep.
        _reminders.Interval = TimeSpan.FromMilliseconds(Math.Clamp((due - DateTime.Now).TotalMilliseconds, 200, 30_000));
        _reminders.Start();
    }

    void OnReminderTick()
    {
        _reminders.Stop();
        var now = DateTime.Now;
        var due = _store.Reminders.Where(r => !r.Fired && !r.Done && r.Due <= now.AddMilliseconds(500)).OrderBy(r => r.Due).ToList();
        if (due.Count > 0)
        {
            foreach (var r in due) r.FiredAt = now;
            Save();
            foreach (var r in due) Fire(r);
        }
        else CheckRelevance();
        ScheduleReminders();
    }

    void Fire(ReminderItem reminder)
    {
        _cardQueue.Enqueue(reminder);
        if (_card is null) ShowNextCard();
    }

    void ShowNextCard()
    {
        while (_cardQueue.Count > 0)
        {
            var reminder = _cardQueue.Dequeue();
            if (!_store.Reminders.Contains(reminder)) continue; // deleted while it waited
            var card = new ReminderCard(reminder, () => Done(reminder), () => Snooze(reminder), _ctx.Settings.SnoozeMinutes);
            _card = card;
            bool seen = false;
            // The notch closes interactive cards itself on an outside click; notice that and move on.
            card.IsVisibleChanged += (_, _) =>
            {
                if (card.IsVisible) seen = true;
                else if (seen) After(TimeSpan.FromMilliseconds(400), () =>
                {
                    if (_card == card && !card.IsVisible) CloseCard();
                });
            };
            // Alarms are reminders too: a due one shows over a fullscreen app (without taking its focus).
            _ctx.ShowCard(card, ReminderCard.CardWidth, ReminderCard.CardHeight, interactive: true, breaksFullscreen: true);
            Glow();
            Chime();
            HostLinks.Say?.Invoke(Shammy.Core.OnlineVoice.SpokenPhrases.Reminder(reminder.Text));
            _cardClose.Stop();
            _cardClose.Interval = CardTime;
            _cardClose.Start();
            return;
        }
    }

    /// <summary>Closes our reminder card (if it's still the one showing) and brings on the next queued one.</summary>
    void CloseCard()
    {
        _cardClose.Stop();
        if (_card is not { } card) return;
        _card = null;
        // Another module may have replaced the card since; only close it if it's still ours.
        if (card.Parent is not null) _ctx.CloseCard();
        ClearGlow();
        if (_cardQueue.Count > 0) After(TimeSpan.FromMilliseconds(450), () =>
        {
            if (_card is null) ShowNextCard();
        });
    }

    void Done(ReminderItem reminder)
    {
        // Ticked off rather than deleted, so it counts on the Notes card's progress for today.
        reminder.DoneAt = DateTime.Now;
        Save();
        CloseCard();
    }

    void Snooze(ReminderItem reminder)
    {
        reminder.FiredAt = null;
        reminder.Due = DateTime.Now + SnoozeTime;
        Save();
        ScheduleReminders();
        CloseCard();
        _ctx.ShowMessage(BellGlyph, $"Snoozed until {ClockText(reminder.Due)}");
    }

    // ---- Notes card ------------------------------------------------------------------------------

    /// <summary>Ticks a note or reminder off, or back on. An unticked reminder that's still ahead fires as planned.</summary>
    void ToggleDone(string id)
    {
        var now = DateTime.Now;
        if (_store.Reminders.FirstOrDefault(r => r.Id == id) is { } r)
        {
            r.DoneAt = r.Done ? null : now;
            if (r.Done && _card?.Reminder == r) CloseCard();
        }
        else if (_store.Notes.FirstOrDefault(n => n.Id == id) is { } n)
        {
            n.DoneAt = n.Done ? null : now;
        }
        else return;
        Save();
        ScheduleReminders();
    }

    /// <summary>The Notes card earns the home row on a day with a reminder that isn't ticked off.</summary>
    bool ReminderDueToday()
    {
        var today = DateTime.Today;
        return _store.Reminders.Any(r => !r.Done && r.Due.Date == today);
    }

    bool _lastRelevant;

    void CheckRelevance()
    {
        bool relevant = ReminderDueToday();
        if (relevant == _lastRelevant) return;
        _lastRelevant = relevant;
        _ctx.CardsChanged();
    }

    void Delete(string id)
    {
        int removed = _store.Reminders.RemoveAll(r => r.Id == id) + _store.Notes.RemoveAll(n => n.Id == id);
        if (removed == 0) return;
        Save();
        ScheduleReminders();
        if (_card?.Reminder.Id == id) CloseCard();
    }

    void Save()
    {
        _ctx.Save(_store);
        _notes.Refresh();
        CheckRelevance();
    }

    // ---- Test pipe -------------------------------------------------------------------------------

    /// <summary>"productivity-timer 5 tea": a 5 second timer labelled tea.</summary>
    string PipeTimer(string arg)
    {
        var parts = arg.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds is <= 0 or > 86400)
            return "error: usage productivity-timer <seconds> [label]";
        var label = parts.Length > 1 ? parts[1] : "";
        var clock = AddClock(ClockKind.Timer, label, TimerGlyph, TimeSpan.FromSeconds(seconds));
        return $"ok timer#{clock.Id} {clock.Display(DateTime.UtcNow)}{(label.Length > 0 ? " " + label : "")}";
    }

    /// <summary>Adds a reminder that is already due, so its card shows straight away.</summary>
    string PipeRemindNow(string text)
    {
        var now = DateTime.Now;
        var item = new ReminderItem { Text = text.Trim(), Due = now, Created = now, FiredAt = now };
        _store.Reminders.Add(item);
        Save();
        Fire(item);
        return _card?.Reminder == item ? "ok card shown" : $"ok queued ({_cardQueue.Count} waiting)";
    }

    string State()
    {
        var now = DateTime.UtcNow;
        string Describe(RunningClock c) =>
            $"{c.Kind.ToString().ToLowerInvariant()}{(c.Label.Length > 0 ? $"[{c.Label}]" : "")}={c.Display(now)}{(c.Paused ? "(paused)" : "")}";
        int upcoming = _store.Reminders.Count(r => !r.Fired);
        return $"clocks={(_clocks.Count == 0 ? "-" : string.Join(",", _clocks.Select(Describe)))} " +
               $"reminders={upcoming} upcoming/{_store.Reminders.Count - upcoming} fired notes={_store.Notes.Count} done={_store.Notes.Count(n => n.Done) + _store.Reminders.Count(r => r.Done)} notesRelevant={ReminderDueToday()} " +
               $"card={(_card is null ? "-" : "open")} queued={_cardQueue.Count} glow={_glowing} " +
               $"focus={_focus.Status} tick={(_tick.IsEnabled ? $"{_tick.Interval.TotalMilliseconds:0}ms" : "off")}";
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    // A long timer can end tomorrow: "Ends 10:09 PM" then reads as tonight.
    static string ClockText(DateTime t) =>
        t.Date == DateTime.Now.Date ? Shammy.Core.Cards.ClockFormat.Time(t, Kit.CardKit.H24) : TimeFormat.When(t, DateTime.Now, Kit.CardKit.H24);

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
