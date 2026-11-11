using System.Globalization;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Core.Activities;
using Shammy.Core.Ambient;
using Shammy.Core.Notch;
using Shammy.Infrastructure;

namespace Shammy.Modules.Ambient;

/// <summary>
/// A live India match as a live activity: the pet watches with the score beside it, reacts for a few seconds to a
/// four, a six or a wicket, and the Cricket card sits on the rail while the match lasts. It reads the same public
/// feed as the cricket ticker, every 30 s while a match is live and every 10 minutes otherwise, and not at all
/// while the setting is off or the PC is offline. Which match counts and when it is live is decided in
/// <see cref="CricketWatch"/>; this class only fetches, schedules and shows.
/// </summary>
internal sealed class LiveCricketFeature : IDisposable
{
    public const string CardId = "cricket";
    const string ActivityId = "ambient.cricket";

    /// <summary>Above music (20); below downloads (40), timers and calls, so it waits as the dot for those.</summary>
    const int Priority = 21;

    readonly ModuleContext _ctx;
    readonly LiveCricketCard _card = new();
    readonly CricketWatch _watch = new();
    readonly LiveContent _content;
    readonly DispatcherTimer _timer, _momentTimer;
    readonly List<string> _fakeBalls = new();

    CancellationTokenSource? _cts;
    int _failures;
    string? _error;
    DateTime? _updatedUtc;
    IReadOnlyList<string> _listed = Array.Empty<string>();

    CricketLiveScore? _fake, _posted;
    bool _feedFake, _chip, _started, _repost;
    TimeSpan _clockShift;

    CricketMoment _moment;
    DateTime _momentUntilUtc;

    public LiveCricketFeature(ModuleContext ctx)
    {
        _ctx = ctx;
        _content = new LiveContent(this);
        _timer = new DispatcherTimer(DispatcherPriority.Background, ctx.Ui);
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _ = PollAsync();
        };
        // The pet asks again each second by itself; this is for the plain pill and for when that tick was missed.
        _momentTimer = new DispatcherTimer(DispatcherPriority.Background, ctx.Ui) { Interval = CricketMoments.Shown + TimeSpan.FromMilliseconds(150) };
        _momentTimer.Tick += (_, _) =>
        {
            _momentTimer.Stop();
            _moment = CricketMoment.None;
            Show(repost: true);
        };
    }

    bool Enabled => _ctx.Settings.LiveCricketEnabled;

    /// <summary>The clock the match is followed by; the test pipe can move it forward.</summary>
    DateTime Now => DateTime.UtcNow + _clockShift;

    /// <summary>The match on show: the made-up one, else India's while it is live.</summary>
    CricketLiveScore? Current => _fake ?? _watch.Live(Now);

    public bool IsLive => Current is not null;

    public void Start()
    {
        _card.Open = () => _ctx.OpenCard(CardId);
        AddCard(chip: false);
        Views.Cards.CardPipes.Register(_ctx, CardId, _card.DescribeCompact, _card.DescribeDetail);
        _ctx.Settings.Changed += OnSettingChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;

        _ctx.AddPipeCommand("cricket-state", _ => State());
        _ctx.AddPipeCommand("cricket-fake", Fake);
        _ctx.AddPipeCommand("cricket-fake-ball", FakeBall);
        _ctx.AddPipeCommand("cricket-fake-feed", FakeFeed);
        _ctx.AddPipeCommand("cricket-fake-minutes", FakeMinutes);
        _ctx.AddPipeCommand("cricket-feed", _ => FeedBlocking());
        Apply();
    }

    /// <summary>
    /// The card is registered again whenever a match starts or ends, because a card's chip is fixed when it is
    /// added: with a chip only while there is a match to open.
    /// </summary>
    void AddCard(bool chip)
    {
        _chip = chip;
        _ctx.AddCard(new NotchCard
        {
            Id = CardId,
            Title = "Cricket",
            Width = 176,
            Order = 22,
            Chip = chip,
            IsRelevant = () => _card.IsLive,
            Live = () => _card.Live,
            Peek = _card.Peek,
            Compact = _card.Compact,
            Detail = _card.BuildDetail,
        });
    }

    // ---- On and off -------------------------------------------------------------------------------

    /// <summary>"live cricket on/off", and "ticker cricket" turning it on; saved in the settings.</summary>
    public void SetEnabled(bool on)
    {
        var s = _ctx.Settings;
        if (s.LiveCricketEnabled == on) return;
        s.LiveCricketEnabled = on;
        s.Save();
        s.NotifyChanged(nameof(AppSettings.LiveCricketEnabled));
    }

    void OnSettingChanged(string name)
    {
        if (name != nameof(AppSettings.LiveCricketEnabled)) return;
        if (!_ctx.Ui.CheckAccess())
        {
            _ctx.Ui.BeginInvoke(() => OnSettingChanged(name));
            return;
        }
        // Off is off for everything, a made-up match included.
        if (!Enabled) ClearFakes();
        Apply();
    }

    /// <summary>Start polling when it should run, stop and forget the match when it shouldn't.</summary>
    void Apply()
    {
        if (Enabled)
        {
            // Let startup settle before the first request.
            if (!_timer.IsEnabled && _cts is null && !_feedFake) Schedule(_started ? TimeSpan.Zero : TimeSpan.FromSeconds(8));
        }
        else
        {
            _timer.Stop();
            CancelFetch();
            _watch.Reset();
            _failures = 0;
            _error = null;
            _listed = Array.Empty<string>();
        }
        _started = true;
        Show();
    }

    void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable) return;
        _ctx.Ui.BeginInvoke(() =>
        {
            if (Enabled && !_feedFake && _failures > 0 && _cts is null) Schedule(TimeSpan.FromSeconds(2));
        });
    }

    /// <summary>Drops a request in flight; its answer is ignored and it schedules nothing.</summary>
    void CancelFetch()
    {
        _cts?.Cancel();
        _cts = null;
    }

    void Schedule(TimeSpan delay)
    {
        _timer.Stop();
        _timer.Interval = delay;
        _timer.Start();
    }

    // ---- Polling ----------------------------------------------------------------------------------

    async Task PollAsync()
    {
        if (!Enabled || _feedFake || _cts is not null) return;
        if (!AmbientNet.IsOnline)
        {
            // Nothing is sent while offline; the timer only keeps counting so it picks up again by itself.
            _error = "offline";
            _failures++;
            _watch.Missed(Now);
            Show();
            Schedule(RefreshSchedule.AfterFailure(_failures, _watch.NextPoll(Now)));
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            var items = await Task.Run(() => FetchAsync(_ctx.Http, cts.Token));
            if (cts.IsCancellationRequested) return;
            Take(items);
            _updatedUtc = DateTime.UtcNow;
            _failures = 0;
            _error = null;
        }
        catch (Exception) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _failures++;
            _error = ex.Message;
            _watch.Missed(Now);
            _ctx.Warn($"live cricket refresh failed ({_failures}): {ex.Message}");
        }
        finally
        {
            if (_cts == cts) _cts = null;
            cts.Dispose();
        }

        if (!Enabled || _feedFake) return;
        Show();
        var normal = _watch.NextPoll(Now);
        Schedule(_failures > 0 ? RefreshSchedule.AfterFailure(_failures, normal) : normal);
    }

    static async Task<IReadOnlyList<CricketFeedItem>> FetchAsync(HttpClient http, CancellationToken ct)
    {
        var xml = await AmbientNet.GetAsync(http, CricketFeed.Url, ct: ct).ConfigureAwait(false);
        if (!xml.Contains("<rss", StringComparison.OrdinalIgnoreCase)) throw new HttpRequestException("the cricket feed returned something other than RSS");
        return CricketLive.ParseItems(xml);
    }

    /// <summary>One poll's matches: follow India's, and react if something just happened.</summary>
    void Take(IReadOnlyList<CricketFeedItem> items)
    {
        _listed = CricketLive.IndiaListed(items);
        var moment = _watch.Observe(CricketLive.Pick(items, _watch.Score?.MatchId), Now);
        if (moment != CricketMoment.None && _fake is null) Flash(moment);
    }

    void Flash(CricketMoment moment)
    {
        _moment = moment;
        _momentUntilUtc = DateTime.UtcNow + CricketMoments.Shown;
        _repost = true; // post again so the notch shows it now rather than at its next tick
        _momentTimer.Stop();
        _momentTimer.Start();
    }

    // ---- Showing ----------------------------------------------------------------------------------

    void Show(bool repost = false)
    {
        repost |= _repost;
        _repost = false;
        var match = Current;
        bool live = match is not null;
        _card.Show(match, _fake is null ? Array.Empty<string>() : _fakeBalls);
        if (live != _chip) AddCard(chip: live);

        if (match is null)
        {
            _momentTimer.Stop();
            _moment = CricketMoment.None;
            if (_posted is not null || repost) _ctx.Remove(ActivityId);
            _posted = null;
            return;
        }
        if (match == _posted && !repost) return;
        _posted = match;
        var state = Describe(DateTime.UtcNow);
        _content.Refresh(state.Label, state.Value);
        // The same payload each time, so the notch keeps what it built and only the words change.
        _ctx.Post(new Activity(ActivityId, ActivityKind.Custom, Priority, _content));
    }

    /// <summary>What the pet wears: the score and the overs, or for a few seconds the moment.</summary>
    PetActivityState Describe(DateTime utcNow)
    {
        // The match last shown, so the words never blank in the seconds before a poll takes the activity away.
        if ((_posted ?? Current) is not { } m)
            return new PetActivityState { Costume = PetCostume.Custom, Face = PetFace.Focused, Ongoing = true, Label = "Cricket" };
        bool moment = _moment != CricketMoment.None && utcNow < _momentUntilUtc;
        return new PetActivityState
        {
            Costume = PetCostume.Custom,
            Face = moment ? CricketMoments.Face(_moment, CricketLive.IsIndia(m.Batting)) : PetFace.Focused,
            Ongoing = true,
            Label = CricketText.Label(m),
            Value = moment ? CricketText.Moment(_moment) : CricketText.Value(m),
        };
    }

    /// <summary>The activity's payload: the pet draws it from <see cref="Describe"/>; the ears are the plain pill.</summary>
    sealed class LiveContent(LiveCricketFeature owner) : ICompactContent, IPetActivity
    {
        readonly TextBlock _label = Words(secondary: true), _value = Words(secondary: false);

        public PetActivityState Describe(DateTime utcNow) => owner.Describe(utcNow);

        // No circle: there is nothing to do to a match but watch it.
        public void RunCircle(CircleAction action) { }

        public void Refresh(string label, string value)
        {
            _label.Text = label;
            _value.Text = value;
        }

        public double Build(ContentControl left, ContentControl right)
        {
            Views.Cards.CardKit.Detach(_label);
            Views.Cards.CardKit.Detach(_value);
            left.Content = _label;
            right.Content = _value;
            return 84;
        }

        static TextBlock Words(bool secondary) => new()
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Tokens.Res(secondary ? "Fg2" : "Fg"),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    // ---- Commands ---------------------------------------------------------------------------------

    public CommandResult Run(LiveCricketCommand command)
    {
        const string glyph = "";
        switch (command)
        {
            case LiveCricketCommand.On:
                SetEnabled(true);
                return new CommandResult(glyph, "Live cricket on", "Shows the score while India is playing");
            case LiveCricketCommand.Off:
                SetEnabled(false);
                return new CommandResult("", "Live cricket off");
            default:
                if (Current is not { } m)
                    return new CommandResult(glyph, "No live India match",
                        Enabled ? "The score shows in the notch when India plays" : "Live cricket is off. Try “live cricket on”");
                _ctx.OpenCard(CardId);
                return new CommandResult(glyph, CricketText.Label(m), CricketText.Line(m));
        }
    }

    public CommandPreview? Preview(LiveCricketCommand command) => command switch
    {
        LiveCricketCommand.On => new CommandPreview("", "Turn live cricket on", "Shows the score while India is playing"),
        LiveCricketCommand.Off => new CommandPreview("", "Turn live cricket off"),
        _ => Current is { } m
            ? new CommandPreview("", "Open the cricket score", $"{CricketText.Label(m)} · {CricketText.Line(m)}")
            : new CommandPreview("", "Cricket score", "No live India match"),
    };

    // ---- Test pipe --------------------------------------------------------------------------------

    void ClearFakes()
    {
        _fake = null;
        _fakeBalls.Clear();
        _feedFake = false;
        _clockShift = TimeSpan.Zero;
    }

    /// <summary>"cricket-fake IND 182/4 18.2 AUS Need 24 from 10" starts a made-up live match; "cricket-fake off" ends it.</summary>
    string Fake(string arg)
    {
        arg = arg.Trim();
        if (arg.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            _fake = null;
            _fakeBalls.Clear();
            Show(repost: true);
            return "ok: fake off; " + State();
        }
        if (CricketFake.Parse(arg) is not { } match)
            return "error: usage cricket-fake <team1> <score1> <overs> <team2> [T20|ODI|Test] <status text> | off   e.g. cricket-fake IND 182/4 18.2 AUS Need 24 from 10";
        _fake = match;
        _fakeBalls.Clear();
        _momentTimer.Stop();
        _moment = CricketMoment.None;
        Show(repost: true);
        return "ok: " + State();
    }

    /// <summary>"cricket-fake-ball 6" (or 0-6, or W): the made-up match one ball later, with its moment.</summary>
    string FakeBall(string arg)
    {
        if (_fake is not { } before) return "error: no fake match; start one with cricket-fake";
        if (CricketFake.Ball(before, arg) is not { } after)
            return before.AllOut ? "error: all out" : "error: usage cricket-fake-ball <0-6|W>";
        _fake = after;
        _fakeBalls.Add(arg.Trim().ToUpperInvariant());
        if (_fakeBalls.Count > 6) _fakeBalls.RemoveAt(0);
        var moment = CricketMoments.Between(before, after);
        if (moment != CricketMoment.None) Flash(moment);
        Show(repost: true);
        return $"ok: moment={moment}; " + State();
    }

    /// <summary>
    /// "cricket-fake-feed India 182/4 * v Australia 239/10": one poll's answer as the real feed would give it (a
    /// title), through the real parsing and rules; real polling waits until "cricket-fake-feed off".
    /// </summary>
    string FakeFeed(string arg)
    {
        arg = arg.Trim();
        if (arg.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            _feedFake = false;
            _clockShift = TimeSpan.Zero;
            _watch.Reset();
            _listed = Array.Empty<string>();
            Apply();
            return "ok: fake feed off; " + State();
        }
        var items = new List<CricketFeedItem>();
        if (!arg.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            if (CricketFeed.ParseTitle(arg) is not { } match)
                return "error: usage cricket-fake-feed <feed title>|none|off   e.g. cricket-fake-feed India 182/4 * v Australia 239/10";
            items.Add(new CricketFeedItem("fake-feed", match));
        }
        _feedFake = true;
        _timer.Stop();
        CancelFetch();
        Take(items);
        Show();
        return "ok: " + State();
    }

    /// <summary>"cricket-fake-minutes 16": time passes for the match being followed (with a fake feed only).</summary>
    string FakeMinutes(string arg)
    {
        if (!_feedFake) return "error: only with cricket-fake-feed";
        if (!double.TryParse(arg.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes) || minutes <= 0 || minutes > 600)
            return "error: usage cricket-fake-minutes <minutes>";
        _clockShift += TimeSpan.FromMinutes(minutes);
        Show();
        return "ok: " + State();
    }

    /// <summary>Reads the real feed once and says what it has for India, without changing anything.</summary>
    string FeedBlocking()
    {
        if (!AmbientNet.IsOnline) return "error: offline";
        return AmbientNet.Block(async () =>
        {
            try
            {
                var items = await FetchAsync(_ctx.Http, CancellationToken.None).ConfigureAwait(false);
                var india = CricketLive.IndiaListed(items);
                var pick = CricketLive.Pick(items);
                return $"items={items.Count} india=[{string.Join(" | ", india)}] inPlay={(pick is null ? "-" : $"{CricketText.Label(pick)} {CricketText.Value(pick)} ({CricketText.Line(pick)})")}";
            }
            catch (Exception ex)
            {
                return "error: " + ex.Message;
            }
        }, TimeSpan.FromSeconds(30), "error: timed out");
    }

    string State()
    {
        var now = Now;
        var pet = Describe(DateTime.UtcNow);
        string followed = _watch.Score is { } s ? $"{CricketText.Label(s)} {CricketText.Value(s)} id={s.MatchId}" : "-";
        return $"enabled={Enabled} online={AmbientNet.IsOnline} phase={_watch.Phase(now)} followed='{followed}' " +
               $"showing={(Current is null ? "no" : $"yes label='{pet.Label}' value='{pet.Value}' face={pet.Face}")} " +
               $"moment={(_moment != CricketMoment.None && DateTime.UtcNow < _momentUntilUtc ? _moment : CricketMoment.None)} " +
               $"fake={(_fake is null ? "off" : "on")} fakeFeed={(_feedFake ? $"on clock=+{_clockShift.TotalMinutes:0.#}m" : "off")} " +
               $"card={(_card.IsLive ? "live" : "hidden")} chip={_chip} listed=[{string.Join(" | ", _listed)}] " +
               $"updated={AmbientNet.Ago(_updatedUtc)} failures={_failures}{(_error is null ? "" : $" ({_error})")} " +
               $"next={(_timer.IsEnabled ? $"{_timer.Interval.TotalSeconds:0}s" : _cts is not null ? "fetching" : "-")}";
    }

    public void Dispose()
    {
        _ctx.Settings.Changed -= OnSettingChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _timer.Stop();
        _momentTimer.Stop();
        _cts?.Cancel();
    }
}

/// <summary>"live cricket on", "live cricket off", "cricket score". Parsing lives in Shammy.Core so it is unit tested.</summary>
internal sealed class LiveCricketTextCommand(LiveCricketFeature cricket) : ITextCommand, ICommandPreview
{
    public bool CanHandle(string text) => CricketLive.ParseCommand(text) is not null;

    public CommandPreview? Preview(string text) => CricketLive.ParseCommand(text) is { } command ? cricket.Preview(command) : null;

    public Task<CommandResult> RunAsync(string text) => Task.FromResult(
        CricketLive.ParseCommand(text) is { } command ? cricket.Run(command) : new CommandResult("", "Not a cricket command", IsError: true));
}
