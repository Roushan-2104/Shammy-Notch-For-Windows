using System.Globalization;
using System.Windows.Media;
using Shammy.Core.Agents;
using Shammy.Core.Audio;
using Shammy.Core.Notch;
using Shammy.Core.Productivity;
using Shammy.Infrastructure;
using Shammy.Shell;

namespace Shammy.Modules.Agents;

/// <summary>
/// The steps wing (see <see cref="StepsWing"/>): what each session is doing, its task list, and its questions, beside
/// the notch. This part keeps the steps and tasks from the hooks, decides what the wing shows, and answers questions.
/// </summary>
internal sealed partial class AgentsModule
{
    readonly AgentFeed _feed = new();
    readonly AgentTaskLists _tasks = new();
    readonly Dictionary<string, int> _tagColors = new(StringComparer.Ordinal);
    readonly Dictionary<PendingAsk, QuestionState> _questions = new();
    StepsWing? _wing;
    int _failedSteps;
    PendingAsk? _chimedFor;
    (string SessionId, string Text, DateTime AtUtc)? _endQuestion;
    string _endDismissed = "";
    /// <summary>The last answer the wing sent (test pipe only shows it).</summary>
    string _lastAnswer = "";

    /// <summary>How long an end-of-reply question waits in the wing before it quietly goes.</summary>
    static readonly TimeSpan EndQuestionStays = TimeSpan.FromMinutes(3);

    sealed class QuestionState(IReadOnlyList<AgentQuestion> questions)
    {
        public IReadOnlyList<AgentQuestion> Questions { get; } = questions;
        public Dictionary<string, string> Answers { get; } = new(StringComparer.Ordinal);
        public int Index { get; set; }
    }

    bool WingOn => _ctx.Settings.AgentsStepsWing;

    /// <summary>Questions go to the wing (not the island's card) while it's on and asking in the notch is allowed.</summary>
    bool WingAsks => WingOn && _ctx.Settings.AgentsAskInNotch;

    bool WingSlidIn
    {
        get => _store.WingSlidIn;
        set
        {
            if (_store.WingSlidIn == value) return;
            _store.WingSlidIn = value;
            _ctx.Save(_store);
        }
    }

    void InitWing()
    {
        IslandBounds.Changed += PlaceWing;
        _ctx.AddPipeCommand("agents-wing", arg => WingPipe(arg.Trim()));
    }

    void DisposeWing()
    {
        IslandBounds.Changed -= PlaceWing;
        _wing?.Close();
        _wing = null;
    }

    // ---- From the hooks ------------------------------------------------------------------------------

    /// <summary>Steps, tasks and end-of-reply questions from one hook event (before the session itself is updated).</summary>
    void NoteForWing(AgentEvent e)
    {
        switch (e.HookEvent)
        {
            case "UserPromptSubmit":
                _feed.NewTurn(e.SessionId);
                if (_endQuestion?.SessionId == e.SessionId) _endQuestion = null;
                break;
            case "PreToolUse" or "PostToolUse" or "PostToolUseFailure":
                // Once per call, when it has run (TaskCreate adds a task: doing it at the start too would add it twice).
                if (e.Input.Length > 0 && e.HookEvent == "PostToolUse") _tasks.Apply(e.SessionId, e.ToolName, e.Input);
                var step = _feed.Note(e, Now);
                if (step is { Failed: true } && e.HookEvent == "PostToolUseFailure") _failedSteps++;
                if (step is not null) TagColor(e.SessionId);
                break;
            case "Stop":
                string q = EndQuestion.Find(e.LastMessage);
                if (q.Length > 0) _endQuestion = (e.SessionId, q, Now);
                else if (_endQuestion?.SessionId == e.SessionId) _endQuestion = null;
                break;
            case "SessionEnd":
                ForgetForWing(e.SessionId);
                break;
        }
    }

    void ForgetForWing(string sessionId)
    {
        _feed.Forget(sessionId);
        _tasks.Forget(sessionId);
        _tagColors.Remove(sessionId);
        if (_endQuestion?.SessionId == sessionId) _endQuestion = null;
    }

    void ClearWing()
    {
        _feed.Clear();
        _tasks.Clear();
        _tagColors.Clear();
        _questions.Clear();
        _endQuestion = null;
        RefreshWing();
    }

    int TagColor(string sessionId)
    {
        if (!_tagColors.TryGetValue(sessionId, out int c)) _tagColors[sessionId] = c = _tagColors.Count;
        return c;
    }

    /// <summary>
    /// An ask the wing takes: Allow/Deny for a tool the notch can answer, or Claude's own questions with choices.
    /// Returns false to leave it to the island's card (or to Claude Code).
    /// </summary>
    bool WingTakes(PendingAsk ask)
    {
        if (!WingAsks) return false;
        var e = ask.Request;
        if (e.ToolName == "AskUserQuestion")
        {
            var qs = AgentQuestions.Parse(e.Input);
            if (qs.Count == 0) return false;
            _questions[ask] = new QuestionState(qs);
            return true;
        }
        return AgentText.CanAnswer(e.ToolName);
    }

    bool IsQuestion(PendingAsk ask) => _questions.ContainsKey(ask);

    // ---- Answers from the wing -------------------------------------------------------------------------

    PendingAsk? WingAsk => WingAsks ? _asks.FirstOrDefault() : null;

    void AnswerPermission(bool allow)
    {
        if (WingAsk is not { } ask || IsQuestion(ask)) return;
        Finish(ask, allow ? AgentText.AllowJson : AgentText.DenyJson);
    }

    void AnswerChoice(IReadOnlyList<string> labels) => AnswerQuestion(string.Join(", ", labels));

    void AnswerQuestion(string answer)
    {
        if (WingAsk is not { } ask || !_questions.TryGetValue(ask, out var q)) return;
        q.Answers[q.Questions[q.Index].Question] = answer;
        q.Index++;
        // Several questions in one go: the next one, then all the answers together.
        if (q.Index < q.Questions.Count)
        {
            RefreshWing();
            return;
        }
        Finish(ask, AgentQuestions.AnswerJson(ask.Request.Input, q.Answers));
    }

    void Finish(PendingAsk ask, string json)
    {
        // Too late (it timed out a moment ago): nothing is printed and Claude Code asks in its own window.
        ask.Answer.TrySetResult(json);
        _lastAnswer = json;
        _questions.Remove(ask);
        EndAsk(ask, answered: true);
    }

    // ---- What the wing shows -------------------------------------------------------------------------

    StepsWing EnsureWing()
    {
        if (_wing is not null) return _wing;
        var w = _wing = new StepsWing();
        w.Slide += slideIn =>
        {
            WingSlidIn = slideIn;
            RefreshWing();
        };
        w.StepClicked += id => OpenWindow(id);
        w.Answered += AnswerPermission;
        w.Chosen += AnswerChoice;
        w.Typed += AnswerQuestion;
        w.OpenWanted += () =>
        {
            if (w.View.SessionId.Length > 0) OpenWindow(w.View.SessionId);
            if (w.View.Mode == WingMode.EndQuestion) DismissEndQuestion();
        };
        w.Dismissed += DismissEndQuestion;
        w.Placed += PlaceWing;
        w.Dropped += OnWingDropped;
        w.DockHome += () => SetWingSide(WingSide.Left);
        return w;
    }

    void DismissEndQuestion()
    {
        if (_endQuestion is { } q) _endDismissed = q.SessionId + "\n" + q.Text;
        _endQuestion = null;
        RefreshWing();
    }

    /// <summary>Works out what the wing shows now and shows it (or hides it).</summary>
    void RefreshWing()
    {
        if (_disposed) return;
        var view = BuildWingView();
        if (view.Mode == WingMode.Hidden && _wing is null) return;
        var w = EnsureWing();
        // A new question chimes once (the wing's only sound).
        if (view.Mode is WingMode.Permission or WingMode.Question && WingAsk is { } ask && !ReferenceEquals(ask, _chimedFor))
        {
            _chimedFor = ask;
            // Silent in a call, a meeting or Focus, like the quiet peeks.
            if (!QuietState.IsQuiet) NotchSound.Play(PingKind.Notification);
        }
        w.SetExcluded(_ctx.Notch.ExcludedFromCapture);
        w.Show(view);
        // Seen only when it can be: over a fullscreen app (a game may draw over everything) it isn't counted, so a
        // question nobody sees goes back to Claude Code after a while instead of holding it for minutes.
        if (WingAsk is { } shown && _wing is { IsVisible: true } && !_ctx.Notch.IsFullscreen) shown.Shown = true;
        PlaceWing();
    }

    WingView BuildWingView()
    {
        if (!WingOn || IslandBounds.Screen.IsEmpty) return new WingView();
        var now = Now;

        // A question always comes first, even over a fullscreen app.
        if (WingAsk is { } ask)
        {
            var e = ask.Request;
            // While the screen is shared nothing names a folder, a file or a command: the question waits for you.
            bool sharing = PrivacyState.Presenting;
            string where = sharing ? "" : AgentText.Folder(e.Cwd);
            string key = ask.ArrivedUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            if (sharing)
                return new WingView
                {
                    Mode = WingMode.EndQuestion,
                    Key = key,
                    Label = IsQuestion(ask) ? "Claude asks you" : "Claude needs your OK",
                    Title = "Hidden while you share your screen",
                    SessionId = e.SessionId,
                    CanOpen = CanOpen(e.SessionId),
                };
            if (_questions.TryGetValue(ask, out var q))
            {
                var current = q.Questions[Math.Min(q.Index, q.Questions.Count - 1)];
                return new WingView
                {
                    Mode = WingMode.Question,
                    Key = key + ":" + q.Index.ToString(CultureInfo.InvariantCulture),
                    Label = "Claude asks you" + (where.Length > 0 ? " · " + where : ""),
                    Title = current.Question,
                    Options = current.Options,
                    MultiSelect = current.MultiSelect,
                    Progress = q.Questions.Count > 1 ? $"{q.Index + 1} of {q.Questions.Count}" : "",
                    SessionId = e.SessionId,
                    CanOpen = CanOpen(e.SessionId),
                };
            }
            return new WingView
            {
                Mode = WingMode.Permission,
                Key = key,
                Label = "Claude needs your OK" + (where.Length > 0 ? " · " + where : ""),
                Title = AgentText.Wants(e.ToolName).Replace("Wants to ", "", StringComparison.Ordinal) is { Length: > 0 } a ? char.ToUpperInvariant(a[0]) + a[1..] + "?" : "Allow this?",
                Detail = AgentText.Box(AgentText.Detail(e), PrivacyState.Presenting),
                SessionId = e.SessionId,
                CanOpen = CanOpen(e.SessionId),
            };
        }

        if (_ctx.Notch.IsFullscreen) return new WingView();
        // You're using the island right now (open, typing, talking, circling): the wing waits. A card that only
        // sits there (a missed alarm) doesn't count, or it could hide the steps for hours.
        if (_ctx.Notch.Mode is NotchMode.Expanded or NotchMode.Listening or NotchMode.Circling or NotchMode.Thinking
            or NotchMode.Typing or NotchMode.Answer or NotchMode.Result or NotchMode.Drop) return new WingView();

        var live = _sessions.Live;
        bool inFront = InFrontNow();

        // A plain question Claude ended its reply on: shown until answered, dismissed or a few minutes pass.
        if (_endQuestion is { } eq)
        {
            if (now - eq.AtUtc > EndQuestionStays || _endDismissed == eq.SessionId + "\n" + eq.Text) _endQuestion = null;
            else if (!inFront && !PrivacyState.Presenting)
            {
                string where = AgentText.Folder(_sessions.Find(eq.SessionId)?.Cwd);
                return new WingView
                {
                    Mode = WingMode.EndQuestion,
                    Key = "end:" + eq.SessionId + ":" + eq.Text,
                    Label = "Claude asks you" + (where.Length > 0 ? " · " + where : ""),
                    Title = eq.Text,
                    SessionId = eq.SessionId,
                    CanOpen = CanOpen(eq.SessionId),
                };
            }
        }

        var done = _sessions.Sessions.FirstOrDefault(s => s.State == AgentState.Done && now - s.FinishedAt < AgentSessions.DoneStays);
        if (live.Count == 0 && done is null) return new WingView();
        if (inFront) return new WingView();

        int steps = _feed.Recent(AgentFeed.Keep).Count;
        if (PrivacyState.Presenting)
            return new WingView { Mode = WingMode.Hint, Title = live.Count > 0 ? $"Claude working · {StepCount(live)} steps" : "Claude is done" };

        if (live.Count == 0 && done is not null)
        {
            int n = _feed.TurnSteps(done.Id);
            var took = done.FinishedAt - done.TurnStarted;
            string folder = AgentText.Folder(done.Cwd);
            return new WingView
            {
                Mode = WingMode.Done,
                Title = done.Failed ? "Stopped on an error" : $"Done · {n} {(n == 1 ? "step" : "steps")} · {Minutes(took)}",
                Detail = folder,
                Label = done.Failed ? "error" : "",
                SessionId = done.Id,
                CanOpen = CanOpen(done.Id),
            };
        }

        var newest = live[0];
        var recent = _feed.Recent(WingSlidIn ? 1 : 4).Select(ToWingStep).ToList();
        string taskOwner = recent.FirstOrDefault()?.SessionId ?? newest.Id;
        return new WingView
        {
            Mode = WingSlidIn ? WingMode.Line : WingMode.Full,
            // The spark already says Claude: the header only says where (or how many).
            Header = live.Count == 1 ? AgentText.Folder(newest.Cwd) is { Length: > 0 } project ? project : "Working" : $"{live.Count} sessions",
            Clock = TimeFormat.Elapsed(now - newest.TurnStarted),
            Tasks = TaskWindow(_tasks.For(taskOwner)),
            Steps = recent,
            SessionId = newest.Id,
            Live = live.Count,
            FailedCount = _failedSteps,
        };
    }

    int StepCount(IReadOnlyList<AgentSession> live) => live.Sum(s => _feed.TurnSteps(s.Id));

    WingStep ToWingStep(AgentStep s)
    {
        string tag = AgentText.Folder(_sessions.Find(s.SessionId)?.Cwd);
        if (tag.Length == 0) tag = "Claude";
        if (tag.Length > 14) tag = tag[..13] + "…";
        return new WingStep(s.Text, tag, TagColor(s.SessionId), s.Failed, !s.Finished, s.SessionId);
    }

    /// <summary>At most four tasks, kept around the one underway (the list can be long).</summary>
    static IReadOnlyList<AgentTask> TaskWindow(IReadOnlyList<AgentTask> all)
    {
        if (all.Count <= 4) return all;
        int now = all.ToList().FindIndex(t => t.Mark == TaskMark.Now);
        if (now < 0) now = all.ToList().FindIndex(t => t.Mark == TaskMark.Todo);
        if (now < 0) now = all.Count - 1;
        int from = Math.Clamp(now - 1, 0, all.Count - 4);
        return all.Skip(from).Take(4).ToList();
    }

    static string Minutes(TimeSpan t) => t.TotalMinutes < 1 ? "under a minute" : $"{(int)Math.Round(t.TotalMinutes)} min";

    /// <summary>
    /// This session's own window (its terminal, or the Claude app) is the one in front. Sessions sharing a window (tabs
    /// of one terminal, the Claude app) all count as in front then: you're on Claude, so Claude asks you itself.
    /// </summary>
    bool SessionInFront(string sessionId) =>
        _windows.TryGetValue(sessionId, out var hwnd) && hwnd != IntPtr.Zero && hwnd == Interop.FrontWindow.Handle;

    /// <summary>Questions whose session window was in front at the last tick (handed back if it still is).</summary>
    readonly HashSet<PendingAsk> _frontOnce = new();

    /// <summary>One of the sessions' own windows is in front: you're looking at Claude already.</summary>
    bool InFrontNow()
    {
        var front = Interop.FrontWindow.Handle;
        if (front == IntPtr.Zero) return false;
        foreach (var s in _sessions.Live)
            if (_windows.TryGetValue(s.Id, out var hwnd) && hwnd == front) return true;
        return false;
    }

    /// <summary>
    /// Puts the wing where it lives: snapped beside the notch on its left or right (level with its top, the dot in the
    /// gap), or floating where it was dropped. A floating wing whose screen is gone comes back beside the notch.
    /// </summary>
    void PlaceWing()
    {
        if (_wing is not { IsVisible: true } w || w.IsDragging) return;
        var island = IslandBounds.Screen;
        if (island.IsEmpty) return;
        double px = VisualTreeHelper.GetDpi(w).DpiScaleX;
        int width = (int)Math.Round(w.ActualWidth * px), margin = (int)Math.Round(StepsWing.GlowRoom * px), gap = (int)Math.Round(StepsWing.Gap * px);
        var side = Enum.IsDefined(_store.WingSide) ? _store.WingSide : WingSide.Left;
        if (side == WingSide.Free && Interop.Native.MonitorFromPoint(new Interop.Native.POINT(_store.WingX + 8, _store.WingY + 8), 0) == IntPtr.Zero) side = WingSide.Left;
        w.SetSide(side);
        w.SetDotLevel(Math.Min(island.Height / px, 40) / 2);
        int top = (int)Math.Round(island.Top) - margin;
        switch (side)
        {
            // Past the action circle and dot beside the island, if they're out.
            case WingSide.Right: w.PlaceAt((int)Math.Round(island.Right + IslandBounds.RightReach) + gap - margin, top); break;
            case WingSide.Free:
            {
                // Kept whole on its screen (a smaller screen, or dropped half off the edge, never strands it).
                var mon = Interop.Native.MonitorFromPoint(new Interop.Native.POINT(_store.WingX + 8, _store.WingY + 8), Interop.Native.MONITOR_DEFAULTTONEAREST);
                var work = Interop.Native.MonitorInfo(mon).rcWork;
                var (x, y) = WingDock.Clamp(_store.WingX, _store.WingY, width - 2 * margin, (int)Math.Round(w.ActualHeight * px) - 2 * margin,
                    work.Left, work.Top, work.Right, work.Bottom);
                w.PlaceAt(x - margin, y - margin);
                break;
            }
            default: w.PlaceAt((int)Math.Round(island.Left) - gap - width + margin, top); break;
        }
    }

    /// <summary>Let go after a drag: snapped to the notch's side it was dropped near, or left floating where it is.</summary>
    void OnWingDropped(int left, int right, int top)
    {
        var island = IslandBounds.Screen;
        if (_wing is null) return;
        if (island.IsEmpty)
        {
            // Nowhere to snap to: back where it was.
            PlaceWing();
            return;
        }
        double px = VisualTreeHelper.GetDpi(_wing).DpiScaleX;
        var side = WingDock.Decide(left, right, top, island.Left, island.Right, island.Top, WingDock.Snap * px, IslandBounds.RightReach);
        if (side == WingSide.Free)
        {
            _store.WingX = left;
            _store.WingY = top;
        }
        SetWingSide(side, force: true);
    }

    void SetWingSide(WingSide side, bool force = false)
    {
        if (_store.WingSide == side && !force) return;
        _store.WingSide = side;
        _ctx.Save(_store);
        PlaceWing();
    }

    // ---- Test pipe -----------------------------------------------------------------------------------

    /// <summary>"agents-wing [state|in|out|allow|deny|pick &lt;n&gt;[,&lt;n&gt;]|type &lt;text&gt;|later]".</summary>
    string WingPipe(string arg)
    {
        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        switch (parts.Length == 0 ? "state" : parts[0])
        {
            case "state": break;
            case "in": WingSlidIn = true; RefreshWing(); break;
            case "out": WingSlidIn = false; RefreshWing(); break;
            case "allow": AnswerPermission(true); break;
            case "deny": AnswerPermission(false); break;
            case "later": DismissEndQuestion(); break;
            case "pick" when parts.Length > 1 && _wing?.View is { Mode: WingMode.Question } v:
            {
                var picks = parts[1].Split(',').Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : -1)
                    .Where(i => i >= 0 && i < v.Options.Count).Select(i => v.Options[i].Label).ToList();
                if (picks.Count == 0) return "error: no such option";
                AnswerChoice(picks);
                break;
            }
            case "type" when parts.Length > 1: AnswerQuestion(parts[1]); break;
            case "refresh": RefreshWing(); break;
            case "dock" when parts.Length > 1:
            {
                // "dock left|right" or "dock free <x> <y>" (screen pixels of the slab's top-left).
                var d = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (!Enum.TryParse<WingSide>(d[0], ignoreCase: true, out var side)) return "error: dock left|right|free <x> <y>";
                if (side == WingSide.Free)
                {
                    if (d.Length < 3 || !int.TryParse(d[1], out int x) || !int.TryParse(d[2], out int y)) return "error: dock free <x> <y>";
                    _store.WingX = x;
                    _store.WingY = y;
                }
                SetWingSide(side, force: true);
                break;
            }
            case "drop" when parts.Length > 1:
            {
                // "drop <left> <right> <top>": as if dragged there and let go.
                var d = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out int v) ? v : int.MinValue).ToArray();
                if (d.Length < 3 || d.Contains(int.MinValue)) return "error: drop <left> <right> <top>";
                OnWingDropped(d[0], d[1], d[2]);
                break;
            }
            default: return "error: agents-wing state|in|out|allow|deny|pick <n>[,<n>]|type <text>|later|refresh|dock <side>|drop <l> <r> <t>";
        }
        return WingState();
    }

    string WingState()
    {
        var steps = string.Join(" | ", _feed.Recent(4).Select(s => $"{s.Text}{(s.Failed ? "(failed)" : "")}{(s.Finished ? "" : "(running)")}"));
        var list = _sessions.Live.Select(s => _tasks.For(s.Id)).FirstOrDefault(l => l.Count > 0) ?? Array.Empty<AgentTask>();
        var tasks = string.Join(" | ", list.Select(t => $"{t.Mark}:{t.Text}"));
        return $"wing={(_wing?.Describe() ?? "none")} on={WingOn} slidIn={WingSlidIn} side={_store.WingSide} steps=[{steps}] tasks=[{tasks}] " +
               $"end=\"{_endQuestion?.Text ?? ""}\" island={IslandBounds.Screen} lastAnswer={LastAnswerShort()}";
    }

    /// <summary>"allow", "deny" or the answers map of the last answer (never the whole input).</summary>
    string LastAnswerShort()
    {
        if (_lastAnswer.Length == 0) return "-";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(_lastAnswer);
            var d = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision");
            string behavior = d.GetProperty("behavior").GetString() ?? "";
            return d.TryGetProperty("updatedInput", out var input) && input.TryGetProperty("answers", out var answers)
                ? behavior + ":" + answers.GetRawText() + ":questions=" + input.GetProperty("questions").GetArrayLength()
                : behavior;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return "unreadable";
        }
    }
}
