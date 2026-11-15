using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Windows.Threading;
using Microsoft.Win32;
using Shammy.Core.Activities;
using Shammy.Core.Calendar;
using Shammy.Core.Cards;
using Shammy.Views.Cards;

namespace Shammy.Modules.Calendar;

/// <summary>What survives a restart, DPAPI-encrypted by <see cref="ModuleContext.Save"/>: the secret address and the last agenda.</summary>
internal sealed class CalendarStore
{
    /// <summary>The private iCal address. It is a password in all but name: never logged, never shown back.</summary>
    public string? Url { get; set; }
    public List<CalendarOccurrence> Cache { get; set; } = new();
    public DateTime? FetchedAt { get; set; }
}

/// <summary>
/// The user's Google Calendar, read from its secret iCal address (no sign-in, no API key): a card with the
/// week and the next meeting, a glance ("Standup 10:30"), and a pill with Join five minutes before a call.
/// Fetches every 10 minutes and after waking or reconnecting; the parsed agenda is cached so the card works
/// offline and right after start.
/// </summary>
internal sealed class CalendarModule : IShammyModule, Connect.ICalendarLink
{
    const string PillId = "calendar.meeting";
    // Below Bluetooth (60) and notifications, above messages (50): a call about to start beats most pills.
    const int PillPriority = 58;
    static readonly TimeSpan FetchEvery = TimeSpan.FromMinutes(10);

    static readonly Lazy<HttpClient> Http = new(() =>
    {
        // Its own client: a calendar with years of history can be several MB, past the shared 4 MB cap.
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 32 * 1024 * 1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Shammy/0.4 (calendar)");
        return http;
    });

    ModuleContext _ctx = null!;
    CalendarStore _store = new();
    CalendarCard _card = null!;
    NotchCard _notchCard = null!;
    readonly DispatcherTimer _fetch = new(DispatcherPriority.Background);
    readonly DispatcherTimer _soon = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
    readonly DispatcherTimer _tick = new(DispatcherPriority.Normal);
    MeetingPill? _pill;
    string? _pillKey;
    bool _fetching, _lastRelevant;
    string? _lastError;
    CalendarSetup? _setup;

    public string Name => "calendar";

    /// <summary>The current agenda, for other cards (the mirror's "Ready for your 10:30 Standup").</summary>
    internal static IReadOnlyList<CalendarOccurrence> Agenda { get; private set; } = Array.Empty<CalendarOccurrence>();

    internal IReadOnlyList<CalendarOccurrence> Items => _store.Cache;
    internal bool Connected => _store.Url is not null;
    internal DateTime? FetchedAt => _store.FetchedAt;
    internal string? LastError => _lastError;

    /// <summary>"Couldn't refresh · last updated 10:20 AM" while the last fetch of a connected calendar failed; else null.</summary>
    internal string? SyncProblem => Connected ? CalendarText.SyncProblem(_lastError, _store.FetchedAt, DateTime.Now, CardKit.H24) : null;

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        _store = ctx.Load<CalendarStore>();
        _store.Cache ??= new();
        if (_store.Url is { } saved && CalendarUrl.Normalize(saved) is null) _store.Url = null;
        Agenda = _store.Cache;

        _card = new CalendarCard(this);
        _notchCard = new NotchCard
        {
            Id = "calendar",
            Title = "Calendar",
            Width = 260,
            Order = 15,
            Chip = true,
            Compact = _card.Compact,
            Detail = _card.BuildDetail,
            IsRelevant = () => Connected && CalendarAgenda.IsRelevant(_store.Cache, DateTime.Now),
            Live = () => _card.Live,
            Peek = _card.Peek,
        };
        ctx.AddCard(_notchCard);
        Connect.Links.Calendar = this;
        ctx.AddGlance("calendar", Glance);
        // "calendar", "my meetings", "what's next": before the launcher, which would open Windows' Calendar app.
        ctx.AddCommandFirst(new CalendarCommand(this));

        // One or the other, never both: connecting again while connected would only replace the address.
        ctx.AddTrayItem("Connect Google Calendar…", OpenSetup, visible: () => !Connected);
        ctx.AddTrayItem("Disconnect calendar", Disconnect, visible: () => Connected);

        ctx.AddPipeCommand("cal-state", _ => State());
        ctx.AddPipeCommand("cal-load-ics", LoadIcsFile);
        ctx.AddPipeCommand("cal-pill-test", _ => PillTest());
        ctx.AddPipeCommand("cal-open", _ => ShowDetail() ? "ok" : "error: no calendar card");

        _fetch.Interval = FetchEvery;
        _fetch.Tick += (_, _) => _ = FetchAsync();
        _soon.Tick += (_, _) =>
        {
            _soon.Stop();
            _ = FetchAsync();
        };
        _tick.Tick += (_, _) => OnTick();
        _card.Compact.IsVisibleChanged += (_, _) =>
        {
            if (_card.Compact.IsVisible) OnTick();
        };

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;

        if (Connected)
        {
            _fetch.Start();
            // Let startup settle; the cached agenda shows meanwhile.
            _soon.Interval = TimeSpan.FromSeconds(4);
            _soon.Start();
        }
        OnTick();
    }

    // Settings → Calendar shows the connection and connects or disconnects through these.
    bool Connect.ICalendarLink.Connected => Connected;
    string? Connect.ICalendarLink.SyncProblem => SyncProblem;
    DateTime? Connect.ICalendarLink.FetchedAt => FetchedAt;
    void Connect.ICalendarLink.OpenSetup() => OpenSetup();
    void Connect.ICalendarLink.Disconnect() => Disconnect();
    void Connect.ICalendarLink.Join(CalendarOccurrence o) => Join(o);

    public void Dispose()
    {
        if (Connect.Links.Calendar == this) Connect.Links.Calendar = null;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _fetch.Stop();
        _soon.Stop();
        _tick.Stop();
    }

    // ---- Fetching --------------------------------------------------------------------------------

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _ctx.Ui.BeginInvoke(FetchSoon);
    }

    void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) _ctx.Ui.BeginInvoke(FetchSoon);
    }

    void FetchSoon()
    {
        if (!Connected) return;
        _soon.Stop();
        _soon.Interval = TimeSpan.FromSeconds(5);
        _soon.Start();
        // The PC may have slept through meeting starts; work the pill out again now.
        OnTick();
    }

    /// <summary>Downloads and parses the feed. Returns null on success, else a user-facing error.</summary>
    internal async Task<string?> FetchAsync(string? url = null)
    {
        url ??= _store.Url;
        if (url is null || _fetching) return url is null ? "Not connected" : null;
        _fetching = true;
        var errorBefore = _lastError;
        try
        {
            using var response = await Http.Value.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                // Never log the address itself: it grants read access to the whole calendar.
                _ctx.Warn($"Calendar fetch failed: HTTP {(int)response.StatusCode}");
                return _lastError = response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                    ? "Google doesn't recognise that address (was it reset?)"
                    : $"Google Calendar answered {(int)response.StatusCode}";
            }
            var text = await response.Content.ReadAsStringAsync();
            if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            {
                _ctx.Warn("Calendar fetch returned something that isn't iCal");
                return _lastError = "That address didn't return a calendar";
            }
            var items = await Task.Run(() => Expand(text, DateTime.Now));
            _store.Cache = items;
            _store.FetchedAt = DateTime.Now;
            _lastError = null;
            Agenda = items;
            _ctx.Save(_store);
            _ctx.Info($"Calendar: {items.Count} occurrences in the window");
            Changed();
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // The exception text can include the request URI on some failures; log only its kind.
            _ctx.Warn($"Calendar fetch failed: {ex.GetType().Name}");
            return _lastError = "Couldn't reach Google Calendar";
        }
        catch (Exception ex)
        {
            _ctx.Warn($"Calendar parse failed: {ex.GetType().Name}");
            return _lastError = "Couldn't read that calendar";
        }
        finally
        {
            _fetching = false;
            // A refresh that failed (or worked again) changes what the card says about it.
            if (_lastError != errorBefore) _card.Refresh();
        }
    }

    /// <summary>The days kept (see <see cref="CalendarAgenda.FetchWindow"/>): the card's two weeks, the wing's two months.</summary>
    static List<CalendarOccurrence> Expand(string ics, DateTime now)
    {
        var (from, to) = CalendarAgenda.FetchWindow(now);
        var local = TimeZoneInfo.Local;
        return CalendarExpander.Expand(IcsParser.Parse(ics), TimeZoneInfo.ConvertTimeToUtc(from, local),
            TimeZoneInfo.ConvertTimeToUtc(to, local), local);
    }

    // ---- Connecting ------------------------------------------------------------------------------

    internal void OpenSetup()
    {
        _setup = new CalendarSetup(this, () =>
        {
            _setup = null;
            _ctx.CloseCard();
        });
        _ctx.ShowCard(_setup, CalendarSetup.CardWidth, CalendarSetup.CardHeight, interactive: true);
        _setup.FocusBox();
    }

    /// <summary>Checks and saves a pasted address. Returns null on success, else what's wrong.</summary>
    internal async Task<string?> ConnectAsync(string pasted)
    {
        if (CalendarUrl.Normalize(pasted) is not { } url)
            return "Paste the “Secret address in iCal format” (https://calendar.google.com/…/basic.ics)";
        var error = await FetchAsync(url);
        // Offline is fine (it fetches later); an address Google rejects is not saved.
        if (error is not null && error != "Couldn't reach Google Calendar") return error;
        _store.Url = url;
        _ctx.Save(_store);
        _fetch.Stop();
        _fetch.Start();
        int week = CalendarAgenda.Week(DateTime.Now).Sum(d => CalendarAgenda.OnDay(_store.Cache, d).Count);
        _ctx.ShowMessage("", error is null ? $"Calendar connected · {week} this week" : "Calendar saved · will sync when online", seconds: 3);
        if (!CalendarUrl.IsPrivate(url)) _ctx.Info("Calendar: a public address was used; it only works for public calendars");
        Changed();
        return null;
    }

    void Disconnect()
    {
        if (!Connected)
        {
            _ctx.ShowMessage("", "No calendar connected");
            return;
        }
        _store = new CalendarStore();
        _lastError = null;
        Agenda = _store.Cache;
        _ctx.Save(_store);
        _fetch.Stop();
        _soon.Stop();
        _ctx.ShowMessage("", "Calendar disconnected");
        Changed();
    }

    // ---- Pill, glance, card refresh ---------------------------------------------------------------

    void Changed()
    {
        _card.Refresh();
        OnTick();
    }

    void OnTick()
    {
        _tick.Stop();
        var now = DateTime.Now;
        var items = _store.Cache;

        if (CalendarAgenda.PillFor(items, now) is { } meeting) ShowPill(meeting, now);
        else HidePill();

        bool relevant = _notchCard.IsRelevant();
        if (relevant != _lastRelevant)
        {
            _lastRelevant = relevant;
            _ctx.CardsChanged();
        }
        if (_card.Compact.IsVisible) _card.Refresh();

        if (items.Count == 0 && _pill is null) return;
        // Every 15 s while a pill counts down or the card is on screen; otherwise wake for the next pill,
        // at least every 5 minutes so the home row drops the card once the day is done.
        var wait = TimeSpan.FromMinutes(5);
        if (_pill is not null || _card.Compact.IsVisible) wait = TimeSpan.FromSeconds(15);
        else if (CalendarAgenda.NextPillAt(items, now) is { } at && at - now < wait) wait = at - now;
        _tick.Interval = wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
        _tick.Start();
    }

    void ShowPill(CalendarOccurrence meeting, DateTime now)
    {
        var key = $"{meeting.Uid}|{meeting.Start:o}";
        // While the screen is shared: "Meeting in 2 min", not the meeting's title.
        var text = Shammy.Core.Connect.PrivacyText.MeetingPill(meeting, now, Infrastructure.PrivacyState.Presenting);
        if (_pill is not null && _pillKey == key && _ctx.Get(PillId) is not null)
        {
            _pill.Set(text);
            return;
        }
        _pillKey = key;
        _pill = new MeetingPill(text, () => Join(meeting));
        _ctx.Post(new Activity(PillId, ActivityKind.Custom, PillPriority, _pill, (meeting.Start + CalendarAgenda.PillAfterStart).ToUniversalTime())
        {
            Click = () => Join(meeting),
        });
    }

    void HidePill()
    {
        if (_pill is null) return;
        _pill = null;
        _pillKey = null;
        _ctx.Remove(PillId);
    }

    internal void Join(CalendarOccurrence o)
    {
        // Only links the parser recognised as Meet/Zoom/Teams (https) are ever opened.
        if (o.Link is not { } link || MeetingLinks.Classify(link) is null) return;
        if (!_ctx.Open(link)) _ctx.ShowMessage("", "Couldn't open the meeting link", error: true);
        if (_pill is not null) HidePill();
    }

    GlanceItem? Glance()
    {
        if (CalendarAgenda.Glance(_store.Cache, DateTime.Now, CardKit.H24) is not { } g) return null;
        var item = g.Item;
        var text = Shammy.Core.Connect.PrivacyText.CalendarGlance(item.Title, item.Start, Infrastructure.PrivacyState.Presenting, CardKit.H24);
        return new GlanceItem(text, NotchPalette.Calendar, g.Priority, item.HasLink ? () => Join(item) : null,
            StartsAt: new DateTimeOffset(DateTime.SpecifyKind(item.Start, DateTimeKind.Local)));
    }

    // ---- Test pipe -------------------------------------------------------------------------------

    string State()
    {
        var now = DateTime.Now;
        var items = _store.Cache;
        var next = CalendarAgenda.Next(items, now);
        var glance = Glance();
        var nextText = next is null ? "-"
            : $"\"{next.Title}\" {next.Start.ToString("ddd HH:mm", CultureInfo.InvariantCulture)} ({CalendarText.Until(next.Start, next.End, now)}) link={next.Provider ?? "-"}";
        return string.Create(CultureInfo.InvariantCulture,
            $"connected={(Connected ? (CalendarUrl.IsPrivate(_store.Url!) ? "private" : "public") : "no")} " +
            $"fetched={(_store.FetchedAt is { } f ? f.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) : "-")} " +
            $"events={items.Count} today={CalendarAgenda.OnDay(items, now).Count} next={nextText} " +
            $"relevant={_notchCard.IsRelevant()} pill={(_pill is null ? "off" : "on")} " +
            $"glance={(glance is null ? "-" : $"\"{glance.Text}\"/{glance.Priority}")} " +
            $"tick={(_tick.IsEnabled ? $"{_tick.Interval.TotalSeconds:0}s" : "off")} error={_lastError ?? "-"} " +
            $"problem={SyncProblem ?? "-"} timeline={CalendarAgenda.TimelineHours(items, now)} h24={CardKit.H24}");
    }

    /// <summary>Loads a local .ics into the card (in memory only; the next fetch of a connected calendar replaces it).</summary>
    string LoadIcsFile(string arg)
    {
        var path = arg.Trim().Trim('"');
        if (path.Length == 0 || !File.Exists(path)) return "error: usage cal-load-ics <path to .ics>";
        try
        {
            var items = Expand(File.ReadAllText(path), DateTime.Now);
            _store.Cache = items;
            Agenda = items;
            Changed();
            var next = CalendarAgenda.Next(items, DateTime.Now);
            return $"ok events={items.Count} next={(next is null ? "-" : $"\"{next.Title}\" {next.Start:ddd HH:mm}")}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "error: " + ex.Message;
        }
    }

    string PillTest()
    {
        var now = DateTime.Now;
        var start = now.AddMinutes(2);
        var fake = new CalendarOccurrence("pill-test", "Test meeting", "", start, start.AddMinutes(15), false,
            "https://meet.google.com/", "Google Meet");
        _pillKey = "pill-test";
        _pill = new MeetingPill(Shammy.Core.Connect.PrivacyText.MeetingPill(fake, now, Infrastructure.PrivacyState.Presenting), () => HidePill());
        _ctx.Post(new Activity(PillId, ActivityKind.Custom, PillPriority, _pill, DateTime.UtcNow.AddSeconds(20))
        {
            Click = HidePill,
        });
        return "ok pill shown for 20 s (Join just dismisses it)";
    }

    internal bool ShowDetail() => _ctx.OpenCard("calendar");

    /// <summary>"what's next": says what the next meeting is, then opens the calendar card's detail.</summary>
    CommandResult RunCommand()
    {
        const string Glyph = "";
        ShowDetail();
        if (!Connected && _store.Cache.Count == 0) return new CommandResult(Glyph, "Calendar", "Not connected yet · paste your Google Calendar address");
        var now = DateTime.Now;
        if (CalendarAgenda.Next(_store.Cache, now) is not { } next) return new CommandResult(Glyph, "Nothing coming up", "Your week is clear");
        bool h24 = CardKit.H24;
        var at = next.Start.Date == now.Date ? CalendarText.ClockAmPm(next.Start, h24) : $"{next.Start.ToString("ddd", CultureInfo.InvariantCulture)} {CalendarText.ClockAmPm(next.Start, h24)}";
        return new CommandResult(Glyph, next.Title, $"{at} · {CalendarText.Until(next.Start, next.End, now)}" + (next.Provider is { } p ? $" · {p}" : ""));
    }

    sealed class CalendarCommand(CalendarModule module) : ITextCommand, ICommandPreview
    {
        public bool CanHandle(string text) => CardPhrases.IsCalendar(text);

        public Task<CommandResult> RunAsync(string text) => Task.FromResult(module.RunCommand());

        public CommandPreview? Preview(string text) => new("", "Open Calendar", "Your next meeting and today's timeline");
    }
}
