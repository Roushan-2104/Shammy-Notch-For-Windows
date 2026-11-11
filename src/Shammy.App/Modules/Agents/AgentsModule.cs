using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Shammy.Core.Activities;
using Shammy.Core.Agents;
using Shammy.Core.Notch;
using Shammy.Infrastructure;
using Shammy.Shell;

namespace Shammy.Modules.Agents;

/// <summary>Kept between runs: where Shammy listens for hooks, and today's session count.</summary>
internal sealed class AgentsStore
{
    public int Port { get; set; }

    /// <summary>The secret in the hook address. Stored encrypted for this Windows account, like all module data.</summary>
    public string Token { get; set; } = "";

    /// <summary>The user pressed Connect and hasn't pressed Disconnect since: listen from start-up.</summary>
    public bool Connected { get; set; }

    public string SeenDay { get; set; } = "";
    public List<string> SeenIds { get; set; } = new();
}

/// <summary>
/// Claude Code in the notch. Claude Code runs "hooks" (small commands) as a session works; once the user has
/// pressed Connect in Settings, those hooks post each event to a listener here on 127.0.0.1. From them the pet
/// shows a session working ("Claude 2:14") and finishing ("Done"), a card lists the live sessions, and when a
/// session needs an OK to run something a card drops with Deny and Allow, whose answer goes back to exactly that
/// request. With no answer, or with Shammy closed, Claude Code asks in its own window as always: nothing here ever
/// allows by itself.
///
/// Claude Code's settings file is changed only by Connect and Disconnect, never at start-up: starting only opens
/// the listener the user already connected. The file's path comes from one place, <see cref="SettingsPath"/>.
///
/// Pipes: "agents-state"; "agents-fake &lt;prompt|tool|permission|needs|idle|stop|fail|end|gone|timeout&gt; &lt;session&gt;
/// [tool] [detail]" (a made-up hook event); "agents-answer allow|deny"; "agents-connect-preview &lt;path&gt;" (what
/// Connect would write, without writing); "agents-port"; "agents-commands"; "agents-use &lt;path&gt;" then
/// "agents-connect" / "agents-disconnect" (the real thing, but only on a path given this way); "agents-advance
/// &lt;seconds&gt;"; "agents-window &lt;session&gt;" (treat the front window as that session's); "agents-open &lt;session&gt;".
/// </summary>
internal sealed class AgentsModule : IShammyModule
{
    public const string ModuleName = "agents";
    const string CardId = "agents", MainId = "agents.main", OtherId = "agents.other", DoneId = "agents.done";

    /// <summary>Working sits just above music; waiting on you with no card to answer on is worth more; "Done" is a moment.</summary>
    const int WorkingPriority = 24, WaitingPriority = 46, DonePriority = 44;

    readonly AgentSessions _sessions = new();
    readonly List<PendingAsk> _asks = new();
    readonly ConcurrentDictionary<string, IntPtr> _windows = new(StringComparer.Ordinal);
    readonly DispatcherTimer _tick = new(DispatcherPriority.Background);

    ModuleContext _ctx = null!;
    AgentsStore _store = new();
    HookListener? _listener;
    ApprovalCard _card = null!;
    AgentsCard? _agentsCard;
    AgentPetContent? _main, _other;
    string _posted = "", _settingsPath = "";
    bool _cardUp, _pathFromPipe, _disposed;
    int _cardMore = -1, _windowWarned;
    TimeSpan _skew;

    public string Name => ModuleName;

    /// <summary>The clock everything here reads (the test pipe can move it forward).</summary>
    internal DateTime Now => DateTime.UtcNow + _skew;

    DateOnly Today => DateOnly.FromDateTime(DateTime.Now + _skew);

    /// <summary>The one place the path of Claude Code's settings file comes from.</summary>
    internal string SettingsPath => _settingsPath;

    internal IReadOnlyList<AgentSession> Sessions => _sessions.Sessions;

    internal AgentSession? Session(string id) => _sessions.Find(id);

    HookEndpoint? Endpoint => new HookEndpoint(_store.Port, _store.Token, CurlPath()) is { IsValid: true } ep ? ep : null;

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        _store = ctx.Load<AgentsStore>();
        // The right-now line and the peek: an agent waiting on you outranks everything else there.
        ctx.AddGlance("agents", () => _asks.Count == 0 ? null
            : new GlanceItem(_asks.Count == 1 ? "Claude Code is waiting for you" : $"{_asks.Count} Claude Code requests are waiting", NotchPalette.Purple, 120,
                () => ctx.OpenCard(CardId)));
        _settingsPath = ClaudeSettingsFile.DefaultPath();
        // A test run can keep Shammy away from the real file from its first moment.
        if (CommandServer.IsEnabled && Environment.GetEnvironmentVariable("SHAMMY_CLAUDE_SETTINGS") is { Length: > 0 } testPath
            && Path.IsPathFullyQualified(testPath))
        {
            _settingsPath = testPath;
            _pathFromPipe = true;
        }
        if (DateOnly.TryParseExact(_store.SeenDay, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            _sessions.RestoreSeen(day, _store.SeenIds);

        _card = new ApprovalCard(() => AnswerCard(true), () => AnswerCard(false));
        _tick.Tick += (_, _) => Tick();

        ctx.AddPipeCommand("agents-state", _ => State());
        ctx.AddPipeCommand("agents-fake", Fake);
        ctx.AddPipeCommand("agents-answer", arg => arg.Trim().ToLowerInvariant() switch
        {
            "allow" => "printed=" + AnswerCard(true),
            "deny" => "printed=" + AnswerCard(false),
            _ => "error: usage agents-answer allow|deny",
        });
        ctx.AddPipeCommand("agents-connect-preview", Preview);
        ctx.AddPipeCommand("agents-port", _ => PortLine());
        ctx.AddPipeCommand("agents-commands", _ => Endpoint is { } ep
            ? string.Join(" || ", AgentHooks.Entries(ep).Select(e => $"{e.Event}{(e.Matcher is null ? "" : $"[{e.Matcher}]")}: {e.Command}"))
            : "error: no port yet (connect first)");
        ctx.AddPipeCommand("agents-use", arg =>
        {
            string path = arg.Trim().Trim('"');
            if (path.Length == 0 || !Path.IsPathFullyQualified(path)) return "error: usage agents-use <full path to a settings.json>";
            _settingsPath = path;
            _pathFromPipe = true;
            AgentsLink.RaiseChanged();
            return State();
        });
        // The pipe may only ever write a file it was pointed at, never the user's real settings.
        ctx.AddPipeCommand("agents-connect", _ => _pathFromPipe ? Said(Connect()) : "error: set a test path first with agents-use <path>");
        ctx.AddPipeCommand("agents-disconnect", _ => _pathFromPipe ? Said(Disconnect()) : "error: set a test path first with agents-use <path>");
        ctx.AddPipeCommand("agents-advance", arg =>
        {
            if (!double.TryParse(arg.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds < 0)
                return "error: usage agents-advance <seconds>";
            _skew += TimeSpan.FromSeconds(seconds);
            Tick();
            return State();
        });
        ctx.AddPipeCommand("agents-window", arg =>
        {
            string id = arg.Trim();
            if (_sessions.Find(id) is null) return "error: no such session";
            _windows[id] = Interop.Native.GetForegroundWindow();
            Sync(force: true);
            return State();
        });
        ctx.AddPipeCommand("agents-open", arg => OpenWindow(arg.Trim()) ? "ok" : "error: no window known for that session");

        AgentsLink.View = View;
        AgentsLink.Press = Press;
        ctx.Settings.Changed += OnSettingChanged;

        // Start-up never touches Claude Code's settings: it only opens the door the user already connected.
        if (_store.Connected && Endpoint is not null)
        {
            EnsureAgentsCard();
            if (!TryListen(_store.Port)) ctx.Warn($"port {_store.Port} is taken; Claude Code's hooks can't reach Shammy until it is reconnected");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _tick.Stop();
        AgentsLink.View = null;
        AgentsLink.Press = null;
        if (_ctx is not null) _ctx.Settings.Changed -= OnSettingChanged;
        foreach (var ask in _asks) ask.Answer.TrySetResult(AgentText.NoDecision);
        _asks.Clear();
        _listener?.Dispose();
        _listener = null;
    }

    // ---- Listening ---------------------------------------------------------------------------------

    bool TryListen(int port)
    {
        if (_listener is not null) return true;
        try
        {
            _listener = new HookListener(port, _store.Token, OnStatusFromHook, OnAskFromHook, OnAskEndedFromHook, _ctx.Warn);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException or ArgumentException)
        {
            _ctx.Warn($"listening on port {port} failed", ex);
            return false;
        }
    }

    /// <summary>A listener on the stored port, or (only from Connect) on a fresh one Windows picks.</summary>
    bool EnsureListener()
    {
        if (_listener is not null) return true;
        if (_store.Token.Length != 32 || !_store.Token.All(char.IsAsciiLetterOrDigit)) _store.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        if (_store.Port > 1023 && TryListen(_store.Port)) return true;
        if (!TryListen(0)) return false;
        _store.Port = _listener!.Port;
        _ctx.Save(_store);
        return true;
    }

    void StopListener()
    {
        _listener?.Dispose();
        _listener = null;
    }

    /// <summary>Windows' own curl when it sits where it needs no quoting; otherwise whichever curl.exe is on the PATH.</summary>
    static string CurlPath()
    {
        try
        {
            string path = Path.Combine(Environment.SystemDirectory, "curl.exe");
            return File.Exists(path) ? AgentHooks.SafeCurl(path) : AgentHooks.PlainCurl;
        }
        catch (Exception)
        {
            return AgentHooks.PlainCurl;
        }
    }

    // Called on a worker thread, before the hook gets its reply (its process is still there to be found).
    void OnStatusFromHook(AgentEvent e, int clientPort)
    {
        NoteWindow(e, clientPort);
        _ctx.Ui.BeginInvoke(() => OnEvent(e));
    }

    void OnAskFromHook(PendingAsk ask, int clientPort)
    {
        NoteWindow(ask.Request, clientPort);
        _ctx.Ui.BeginInvoke(() => OnAsk(ask));
    }

    void OnAskEndedFromHook(PendingAsk ask, AskEnd end) => _ctx.Ui.BeginInvoke(() => OnAskEnded(ask, end));

    /// <summary>Remembers which window a session lives in, once per session (and again at a prompt if it wasn't found).</summary>
    void NoteWindow(AgentEvent e, int clientPort)
    {
        try
        {
            if (clientPort == 0 || e.Kind == AgentEventKind.SessionEnd || _listener is not { } listener) return;
            if (_windows.TryGetValue(e.SessionId, out var known)
                && (known != IntPtr.Zero ? AgentWindows.IsAlive(known) : e.Kind != AgentEventKind.Prompt)) return;
            int pid = AgentWindows.ClientProcess(clientPort, listener.Port);
            _windows[e.SessionId] = pid == 0 ? IntPtr.Zero : AgentWindows.Find(pid, AgentText.Folder(e.Cwd));
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _windowWarned) <= 3) _ctx.Warn("finding a session's window failed", ex);
        }
    }

    // ---- Events (UI thread from here on) ------------------------------------------------------------

    void OnEvent(AgentEvent e)
    {
        if (_disposed) return;
        var change = _sessions.Apply(e, Now, Today);
        if (change == AgentChange.Removed)
        {
            _windows.TryRemove(e.SessionId, out _);
            foreach (var ask in _asks.Where(a => a.Request.SessionId == e.SessionId).ToList())
            {
                ask.Answer.TrySetResult(AgentText.NoDecision);
                _asks.Remove(ask);
            }
            if (_ctx.Get(DoneId)?.Payload is AgentPetContent done && done.SessionId == e.SessionId) _ctx.Remove(DoneId);
        }
        else if (change == AgentChange.Finished && _sessions.Find(e.SessionId) is { } finished) PostDone(finished);
        else if (change == AgentChange.Working && _ctx.Get(DoneId)?.Payload is AgentPetContent stale && stale.SessionId == e.SessionId) _ctx.Remove(DoneId);
        RememberSeen();
        // A tool finishing while it works (the common event) changes nothing on screen.
        if (change != AgentChange.None) Sync();
    }

    void OnAsk(PendingAsk ask)
    {
        if (_disposed)
        {
            ask.Answer.TrySetResult(AgentText.NoDecision);
            return;
        }
        var e = ask.Request;
        ask.ArrivedUtc = Now;
        _sessions.Apply(e, Now, Today);
        RememberSeen();
        // No card: switched off, a question only the terminal can answer, or a fullscreen app in front. The
        // hook gets "no decision" at once, so Claude Code's own prompt appears without any wait.
        if (!_ctx.Settings.AgentsAskInNotch || !AgentText.CanAnswer(e.ToolName) || _ctx.Notch.IsFullscreen)
        {
            ask.Answer.TrySetResult(AgentText.NoDecision);
            Sync();
            return;
        }
        _asks.Add(ask);
        _sessions.Hold(e.SessionId);
        Sync();
    }

    void OnAskEnded(PendingAsk ask, AskEnd end)
    {
        if (!_asks.Contains(ask)) return;
        // The hook hanging up means it was answered in the terminal (or the turn was stopped): not waiting on us.
        EndAsk(ask, answered: end != AskEnd.TimedOut);
    }

    void EndAsk(PendingAsk ask, bool answered)
    {
        _asks.Remove(ask);
        _sessions.Release(ask.Request.SessionId, answered, Now);
        Sync();
    }

    /// <summary>Allow or Deny on the card: answers the one request it shows. Returns what the hook will print.</summary>
    string AnswerCard(bool allow)
    {
        if (_card.Ask is not { } ask || !_asks.Contains(ask)) return "error: no request waiting";
        string json = allow ? AgentText.AllowJson : AgentText.DenyJson;
        // Too late (it timed out a moment ago): nothing is printed and Claude Code asks in its own window.
        if (!ask.Answer.TrySetResult(json)) json = AgentText.NoDecision;
        EndAsk(ask, answered: true);
        return json.Length == 0 ? "(nothing: it had already timed out)" : json;
    }

    void OnSettingChanged(string name)
    {
        if (name != nameof(AppSettings.AgentsAskInNotch) || _ctx.Settings.AgentsAskInNotch) return;
        // Switched off with a card up: hand the questions back to Claude Code.
        PassBack(_asks);
    }

    void RememberSeen()
    {
        var (day, ids) = _sessions.Seen;
        string text = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_store.SeenDay == text && _store.SeenIds.Count == ids.Count) return;
        _store.SeenDay = text;
        _store.SeenIds = ids.ToList();
        _ctx.Save(_store);
        AgentsLink.RaiseChanged();
    }

    // ---- What shows -------------------------------------------------------------------------------

    void Sync(bool force = false)
    {
        var live = _sessions.Live;
        string key = string.Join("|", live.Take(2).Select(s => $"{s.Id}:{s.State}:{CanOpen(s.Id)}"));
        if (force || key != _posted)
        {
            _posted = key;
            // Ties go to the newest post, so the session that owns the row is posted last.
            if (live.Count > 1) PostLive(OtherId, ref _other, live[1]);
            else
            {
                _ctx.Remove(OtherId);
                _other = null;
            }
            if (live.Count > 0) PostLive(MainId, ref _main, live[0]);
            else
            {
                _ctx.Remove(MainId);
                _main = null;
            }
        }
        _main?.Refresh();
        _other?.Refresh();
        if (_sessions.Count > 0) EnsureAgentsCard();
        _agentsCard?.Refresh();
        _ctx.CardsChanged();
        ShowCardIfNeeded();
        UpdateTimer();
    }

    void PostLive(string id, ref AgentPetContent? content, AgentSession s)
    {
        if (content?.SessionId != s.Id) content = new AgentPetContent(this, s.Id, finished: false);
        bool waiting = s.State == AgentState.Waiting;
        string session = s.Id;
        _ctx.Post(new Activity(id, ActivityKind.Custom, waiting ? WaitingPriority : WorkingPriority, content)
        {
            Click = waiting && CanOpen(session) ? () => OpenWindow(session) : null,
        });
    }

    void PostDone(AgentSession s)
    {
        string session = s.Id;
        var content = new AgentPetContent(this, session, finished: true);
        _ctx.Post(new Activity(DoneId, ActivityKind.Custom, DonePriority, content, DateTime.UtcNow + AgentSessions.DoneStays)
        {
            Click = () => CircleClicked(session, true, CanOpen(session) ? CircleAction.Open : CircleAction.Dismiss),
        });
    }

    internal void CircleClicked(string sessionId, bool finished, CircleAction action)
    {
        if (action == CircleAction.Open) OpenWindow(sessionId);
        if (finished) _ctx.Remove(DoneId);
    }

    internal bool CanOpen(string sessionId) => _windows.TryGetValue(sessionId, out var hwnd) && AgentWindows.IsAlive(hwnd);

    internal bool OpenWindow(string sessionId)
    {
        try
        {
            if (_windows.TryGetValue(sessionId, out var hwnd) && AgentWindows.Focus(hwnd)) return true;
        }
        catch (Exception ex)
        {
            _ctx.Warn("bringing a session's window forward failed", ex);
        }
        _windows.TryRemove(sessionId, out _);
        return false;
    }

    /// <summary>The card joins the notch the first time it's needed, so people who never connect don't get its chip.</summary>
    void EnsureAgentsCard()
    {
        if (_agentsCard is not null) return;
        var card = _agentsCard = new AgentsCard(this, () => _ctx.OpenCard(CardId));
        _ctx.AddCard(new NotchCard
        {
            Id = CardId,
            Title = "Coding agents",
            Width = 176,
            Order = 45,
            Chip = true,
            IsRelevant = () => _sessions.Count > 0,
            Compact = card.Compact,
            Detail = card.BuildDetail,
            Live = () => card.Live,
            Peek = card.Peek,
        });
    }

    // ---- The approval card --------------------------------------------------------------------------

    /// <summary>A request whose card couldn't be put on screen for this long goes back to Claude Code's own prompt.</summary>
    static readonly TimeSpan UnseenLimit = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Puts the oldest unanswered request on the card. It shows over quiet mode (the user set the agent going and it
    /// is waiting on them), never over a fullscreen app, and never on top of another card such as a ringing alarm:
    /// then it waits its turn briefly, and a request nobody answers goes back to Claude Code.
    /// </summary>
    void ShowCardIfNeeded()
    {
        if (_asks.Count == 0)
        {
            if (_cardUp && _card.Parent is not null) _ctx.CloseCard();
            _cardUp = false;
            _cardMore = -1;
            return;
        }
        var next = _asks[0];
        bool changed = !ReferenceEquals(_card.Ask, next) || _cardMore != _asks.Count - 1;
        if (changed)
        {
            _cardMore = _asks.Count - 1;
            _card.Show(next, _cardMore);
        }
        var mode = _ctx.Notch.Mode;
        bool ours = _cardUp && _card.Parent is not null;
        if (ours && mode == NotchMode.Card && !changed) return;
        if (_ctx.Notch.IsFullscreen) return;
        if (mode == NotchMode.Card && !ours) return;
        // First time for this request, or it was closed under us while the notch rests: (re)show it.
        if (changed || !ours || mode is NotchMode.Idle or NotchMode.Compact or NotchMode.Peek)
        {
            _ctx.ShowCard(_card, ApprovalCard.CardWidth, _card.CardHeight, interactive: false);
            _cardUp = true;
        }
        if (_ctx.Notch.Mode == NotchMode.Card && _cardUp && _card.Parent is not null) next.Shown = true;
    }

    /// <summary>Hands requests back unanswered: Claude Code asks in its own window, the session keeps "Needs you".</summary>
    void PassBack(IEnumerable<PendingAsk> asks)
    {
        foreach (var ask in asks.ToList())
        {
            ask.Answer.TrySetResult(AgentText.NoDecision);
            _asks.Remove(ask);
            _sessions.Release(ask.Request.SessionId, answered: false, Now);
        }
        Sync();
    }

    void UpdateTimer()
    {
        // A second while a request waits (its clock); otherwise now and then, to forget silent sessions.
        var interval = _asks.Count > 0 ? TimeSpan.FromSeconds(1) : _sessions.Count > 0 ? TimeSpan.FromSeconds(20) : TimeSpan.Zero;
        if (interval == TimeSpan.Zero)
        {
            _tick.Stop();
            return;
        }
        if (_tick.Interval != interval) _tick.Interval = interval;
        if (!_tick.IsEnabled) _tick.Start();
    }

    void Tick()
    {
        if (_disposed) return;
        if (_sessions.Sweep(Now) > 0)
        {
            foreach (var id in _windows.Keys.Where(id => _sessions.Find(id) is null).ToList()) _windows.TryRemove(id, out _);
            Sync();
        }
        if (_asks.Count > 0)
        {
            // A fullscreen app came to the front, or something else holds the notch: don't keep Claude Code waiting
            // on a card nobody can see.
            if (_ctx.Notch.IsFullscreen) PassBack(_asks);
            else if (!_asks[0].Shown && Now - _asks[0].ArrivedUtc > UnseenLimit) PassBack(_asks.Take(1));
        }
        if (_asks.Count > 0)
        {
            _card.Tick(Now);
            ShowCardIfNeeded();
        }
        _main?.Refresh();
        _other?.Refresh();
        UpdateTimer();
    }

    // ---- Settings: Connect / Disconnect --------------------------------------------------------------

    AgentConnectView View()
    {
        var read = ClaudeSettingsFile.Read(_settingsPath);
        var state = read.Error.Length > 0 ? HookState.Unreadable : AgentHooks.Inspect(read.Text, Endpoint);
        return AgentText.ConnectView(read.FolderExists, state, _listener is not null, _sessions.SeenToday(Today));
    }

    (bool Ok, string Said) Press()
    {
        var view = View();
        if (!view.Enabled) return (false, view.Status);
        return view.Button == "Disconnect" ? Disconnect() : Connect();
    }

    (bool Ok, string Said) Connect()
    {
        var read = ClaudeSettingsFile.Read(_settingsPath);
        if (!read.FolderExists) return (false, "Claude Code not found.");
        // A port that another program took since last time means a new one, and so new hook commands.
        if (!EnsureListener() || Endpoint is not { } ep) return (false, "Shammy couldn’t open a port to listen on.");
        var edit = ClaudeSettingsFile.Apply(_settingsPath, text => AgentHooks.Connect(text, ep), backup: true);
        if (!edit.Ok)
        {
            if (!_store.Connected) StopListener();
            AgentsLink.RaiseChanged();
            return (false, edit.Error);
        }
        _store.Connected = true;
        _ctx.Save(_store);
        EnsureAgentsCard();
        _ctx.CardsChanged();
        _ctx.Info($"connected to Claude Code on port {ep.Port}{(edit.Changed ? "" : " (hooks were already there)")}");
        AgentsLink.RaiseChanged();
        return (true, "Connected to Claude Code");
    }

    (bool Ok, string Said) Disconnect()
    {
        var edit = ClaudeSettingsFile.Apply(_settingsPath, AgentHooks.Disconnect, backup: false);
        // A settings folder that is gone has no hooks left in it either.
        if (!edit.Ok && ClaudeSettingsFile.Read(_settingsPath).FolderExists)
        {
            AgentsLink.RaiseChanged();
            return (false, edit.Error);
        }
        _store.Connected = false;
        _ctx.Save(_store);
        foreach (var ask in _asks.ToList())
        {
            ask.Answer.TrySetResult(AgentText.NoDecision);
            _asks.Remove(ask);
        }
        StopListener();
        _sessions.Clear();
        _windows.Clear();
        _ctx.Remove(DoneId);
        Sync(force: true);
        _ctx.Info("disconnected from Claude Code");
        AgentsLink.RaiseChanged();
        return (true, "Disconnected from Claude Code");
    }

    static string Said((bool Ok, string Said) result) => (result.Ok ? "ok: " : "error: ") + result.Said;

    // ---- Test pipe ---------------------------------------------------------------------------------

    string PortLine() =>
        $"port={(_listener?.Port ?? _store.Port)} listening={_listener is not null} served={_listener?.Served ?? 0} refused={_listener?.Refused ?? 0}";

    string State()
    {
        var now = Now;
        var view = View();
        string sessions = string.Join(",", _sessions.Sessions.Select(s =>
            $"{s.Id}:{s.State}{(s.Failed ? "(failed)" : "")}:{AgentText.Folder(s.Cwd)}:\"{AgentText.StateLine(s, now)}\"{(CanOpen(s.Id) ? ":window" : "")}"));
        string asks = string.Join(",", _asks.Select(a => $"{a.Request.SessionId}:{a.Request.ToolName}"));
        string Pet(string id) => _ctx.Get(id) is { Payload: AgentPetContent p } a
            ? $"{p.SessionId}/{p.Describe(now).Value}/p{a.Priority}{(p.Describe(now).Circle is { } c ? "/" + c.Action : "")}"
            : "-";
        return $"{PortLine()} connected={_store.Connected} status=\"{view.Status}\" button={view.Button}{(view.Enabled ? "" : "(off)")} " +
               $"path={(_pathFromPipe ? _settingsPath : "default")} ask={_ctx.Settings.AgentsAskInNotch} today={_sessions.SeenToday(Today)} " +
               $"sessions=[{sessions}] asks=[{asks}] card={(_cardUp && _card.Parent is not null ? _card.Describe() : "-")} " +
               $"pet=main:{Pet(MainId)},other:{Pet(OtherId)},done:{Pet(DoneId)} wall={(_agentsCard is null ? "-" : _agentsCard.Describe())} " +
               $"presenting={PrivacyState.Presenting} quiet={QuietState.IsQuiet} fullscreen={_ctx.Notch.IsFullscreen} notch={_ctx.Notch.Mode}";
    }

    /// <summary>A hook payload as Claude Code would send it, through the same parser and the same handlers.</summary>
    string Fake(string arg)
    {
        var words = arg.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2)
            return "error: usage agents-fake prompt|tool|permission|needs|idle|stop|fail|end|gone|timeout <session> [tool] [detail]";
        string kind = words[0].ToLowerInvariant(), session = words[1];
        string tool = words.Length > 2 ? words[2] : kind == "permission" ? "Bash" : "";
        string detail = words.Length > 3 ? words[3] : "";

        if (kind is "gone" or "timeout")
        {
            if (_asks.FirstOrDefault(a => a.Request.SessionId == session) is not { } held) return "error: that session has no request waiting";
            held.Answer.TrySetResult(AgentText.NoDecision);
            OnAskEnded(held, kind == "gone" ? AskEnd.HookGone : AskEnd.TimedOut);
            return State();
        }

        string? hookEvent = kind switch
        {
            "prompt" => "UserPromptSubmit",
            "tool" => "PostToolUse",
            "permission" => "PermissionRequest",
            "needs" or "idle" => "Notification",
            "stop" => "Stop",
            "fail" => "StopFailure",
            "end" => "SessionEnd",
            _ => null,
        };
        if (hookEvent is null) return "error: unknown event";

        var payload = new Dictionary<string, object>
        {
            ["session_id"] = session,
            ["transcript_path"] = $@"C:\fake\{session}.jsonl",
            ["cwd"] = $@"C:\Projects\{session}",
            ["permission_mode"] = "default",
            ["hook_event_name"] = hookEvent,
        };
        if (kind is "needs") payload["notification_type"] = "permission_prompt";
        if (kind is "idle") payload["notification_type"] = "idle_prompt";
        if (tool.Length > 0)
        {
            payload["tool_name"] = tool;
            string field = tool switch
            {
                "Edit" or "Write" or "Read" or "MultiEdit" => "file_path",
                "WebFetch" => "url",
                "WebSearch" => "query",
                _ => "command",
            };
            payload["tool_input"] = new Dictionary<string, object> { [field] = detail };
        }
        if (HookPayload.Parse(JsonSerializer.SerializeToUtf8Bytes(payload)) is not { } e) return "error: that isn't a payload Shammy accepts";

        if (e.Kind == AgentEventKind.Permission) OnAsk(new PendingAsk(e, Now));
        else OnEvent(e);
        return State();
    }

    /// <summary>What Connect would write for the file at a path, without writing anything.</summary>
    string Preview(string arg)
    {
        string path = arg.Trim().Trim('"');
        if (path.Length == 0 || !Path.IsPathFullyQualified(path)) return "error: usage agents-connect-preview <full path to a settings.json>";
        var read = ClaudeSettingsFile.Read(path);
        if (!read.FolderExists) return "error: that folder doesn't exist (Claude Code not found)";
        if (read.Error.Length > 0) return "error: " + read.Error;
        // Before the first Connect there is no port yet; a made-up one stands in and says so.
        var ep = Endpoint;
        string note = ep is null ? " note=\"example port and token; Connect picks real ones\"" : "";
        ep ??= new HookEndpoint(47600, new string('0', 32), CurlPath());
        var edit = AgentHooks.Connect(read.Text, ep);
        if (!edit.Ok) return "refused: " + edit.Error;
        var text = new StringBuilder(edit.Json).Replace("\r", "\\r").Replace("\n", "\\n");
        return $"ok: exists={read.FileExists} changed={edit.Changed} before={AgentHooks.Inspect(read.Text, ep)} written=nothing{note} text={text}";
    }
}
