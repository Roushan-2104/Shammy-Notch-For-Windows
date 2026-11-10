namespace Shammy.Core.Agents;

public enum AgentState
{
    /// <summary>A turn is running.</summary>
    Working,
    /// <summary>It stopped to ask the user something (a permission, a question).</summary>
    Waiting,
    /// <summary>The turn is over; the session is still open.</summary>
    Done,
}

/// <summary>What an event did to a session, so the notch knows whether to say anything.</summary>
public enum AgentChange
{
    None,
    /// <summary>It is working (a new turn, or back at work after a question).</summary>
    Working,
    /// <summary>It began waiting on the user.</summary>
    Waiting,
    /// <summary>The turn just finished: worth a "Done".</summary>
    Finished,
    /// <summary>It went idle without a finish to announce (Claude Code said so later, or the turn was interrupted).</summary>
    Quietened,
    /// <summary>The session closed and is gone.</summary>
    Removed,
}

/// <summary>One Claude Code session as the notch knows it.</summary>
public sealed class AgentSession
{
    internal AgentSession(string id) => Id = id;

    public string Id { get; }

    /// <summary>The folder it works in (the last one any event named).</summary>
    public string Cwd { get; internal set; } = "";

    public AgentState State { get; internal set; }

    /// <summary>When the running turn began (a turn keeps its clock through questions).</summary>
    public DateTime TurnStarted { get; internal set; }

    public DateTime WaitingSince { get; internal set; }

    public DateTime FinishedAt { get; internal set; }

    public DateTime LastEvent { get; internal set; }

    /// <summary>The last turn ended on an error.</summary>
    public bool Failed { get; internal set; }

    /// <summary>What it is waiting for ("Wants to run a command"); empty when Claude Code didn't say.</summary>
    public string Request { get; internal set; } = "";

    /// <summary>Approval cards of this session that Shammy is still holding open.</summary>
    public int Held { get; internal set; }

    public bool IsLive => State is AgentState.Working or AgentState.Waiting;
}

/// <summary>
/// The live Claude Code sessions, driven only by hook events and the clock it is handed. A session works,
/// may wait on the user, works again and finishes; one that says nothing for <see cref="Forget"/> is dropped
/// (its terminal was probably closed without a goodbye), and a closed session goes at once.
/// </summary>
public sealed class AgentSessions
{
    /// <summary>A session with no events for this long is forgotten.</summary>
    public static readonly TimeSpan Forget = TimeSpan.FromMinutes(30);

    /// <summary>How long a finished turn stays in the compact notch.</summary>
    public static readonly TimeSpan DoneStays = TimeSpan.FromSeconds(8);

    const int MaxSessions = 64, MaxSeen = 500;

    readonly Dictionary<string, AgentSession> _sessions = new(StringComparer.Ordinal);
    readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    DateOnly _seenDay;

    /// <summary>Every known session, the most recently active first.</summary>
    public IReadOnlyList<AgentSession> Sessions => _sessions.Values.OrderByDescending(s => s.LastEvent).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();

    /// <summary>Working or waiting sessions, the most recently active first (the first one owns the notch's row).</summary>
    public IReadOnlyList<AgentSession> Live => Sessions.Where(s => s.IsLive).ToList();

    public int Count => _sessions.Count;

    public AgentSession? Find(string id) => _sessions.TryGetValue(id, out var s) ? s : null;

    /// <summary>How many different sessions were heard from on <paramref name="today"/>.</summary>
    public int SeenToday(DateOnly today) => today == _seenDay ? _seen.Count : 0;

    /// <summary>The day and ids behind <see cref="SeenToday"/>, to keep across a restart.</summary>
    public (DateOnly Day, IReadOnlyList<string> Ids) Seen => (_seenDay, _seen.ToList());

    public void RestoreSeen(DateOnly day, IEnumerable<string> ids)
    {
        _seenDay = day;
        _seen.Clear();
        foreach (var id in ids.Take(MaxSeen)) _seen.Add(id);
    }

    /// <summary>Applies one hook event. <paramref name="today"/> is the local date, for the "sessions today" count.</summary>
    public AgentChange Apply(AgentEvent e, DateTime nowUtc, DateOnly today)
    {
        if (e.Kind == AgentEventKind.SessionEnd) return _sessions.Remove(e.SessionId) ? AgentChange.Removed : AgentChange.None;

        var s = Find(e.SessionId);
        // Only something that says what the session is doing may introduce it.
        bool introduces = e.Kind is AgentEventKind.Prompt or AgentEventKind.ToolDone or AgentEventKind.Permission
            or AgentEventKind.NeedsInput or AgentEventKind.Stop or AgentEventKind.StopFailed;
        bool isNew = s is null;
        if (s is null)
        {
            if (!introduces) return AgentChange.None;
            if (_sessions.Count >= MaxSessions) _sessions.Remove(Sessions[^1].Id);
            s = _sessions[e.SessionId] = new AgentSession(e.SessionId) { TurnStarted = nowUtc, State = AgentState.Done, FinishedAt = nowUtc };
            Note(e.SessionId, today);
        }
        s.LastEvent = nowUtc;
        if (e.Cwd.Length > 0) s.Cwd = e.Cwd;

        switch (e.Kind)
        {
            case AgentEventKind.Prompt:
                s.State = AgentState.Working;
                s.TurnStarted = nowUtc;
                s.Failed = false;
                s.Request = "";
                return AgentChange.Working;

            case AgentEventKind.ToolDone:
                // First heard of in the middle of a turn (Shammy started later than Claude Code did).
                if (isNew) return Work(s, nowUtc);
                // A finished turn stays finished: a late ping, or work in the background that will report back.
                if (s.State != AgentState.Waiting) return AgentChange.None;
                // A tool ran, so whatever it asked was answered; unless Shammy is itself still holding the question.
                if (s.Held > 0) return AgentChange.None;
                return Work(s, nowUtc);

            case AgentEventKind.Permission:
                s.State = AgentState.Waiting;
                s.WaitingSince = nowUtc;
                s.Request = AgentText.Wants(e.ToolName);
                return AgentChange.Waiting;

            case AgentEventKind.NeedsInput:
                if (s.State == AgentState.Waiting) return AgentChange.None;
                s.State = AgentState.Waiting;
                s.WaitingSince = nowUtc;
                s.Request = "";
                return AgentChange.Waiting;

            case AgentEventKind.Idle:
                if (!s.IsLive) return AgentChange.None;
                Finish(s, nowUtc, failed: false);
                return AgentChange.Quietened;

            case AgentEventKind.Stop or AgentEventKind.StopFailed:
                Finish(s, nowUtc, e.Kind == AgentEventKind.StopFailed);
                return AgentChange.Finished;

            default:
                return AgentChange.None;
        }
    }

    AgentChange Work(AgentSession s, DateTime nowUtc)
    {
        if (s.State == AgentState.Done) s.TurnStarted = nowUtc;
        s.State = AgentState.Working;
        s.Request = "";
        return AgentChange.Working;
    }

    static void Finish(AgentSession s, DateTime nowUtc, bool failed)
    {
        s.State = AgentState.Done;
        s.FinishedAt = nowUtc;
        s.Failed = failed;
        s.Request = "";
        s.Held = 0;
    }

    /// <summary>Shammy keeps this session's approval question open (its card is up).</summary>
    public void Hold(string sessionId)
    {
        if (Find(sessionId) is { } s) s.Held++;
    }

    /// <summary>
    /// A held question is over. Answered (in the notch, or in the terminal while the card was up) means the
    /// session is working again; handed back unanswered, it keeps waiting.
    /// </summary>
    /// <returns>True when the session went back to work.</returns>
    public bool Release(string sessionId, bool answered, DateTime nowUtc)
    {
        if (Find(sessionId) is not { } s) return false;
        s.Held = Math.Max(0, s.Held - 1);
        if (!answered || s.Held > 0 || s.State != AgentState.Waiting) return false;
        Work(s, nowUtc);
        return true;
    }

    /// <summary>Drops sessions that have been silent for <see cref="Forget"/>; returns how many went.</summary>
    public int Sweep(DateTime nowUtc)
    {
        var gone = _sessions.Values.Where(s => nowUtc - s.LastEvent >= Forget).Select(s => s.Id).ToList();
        foreach (var id in gone) _sessions.Remove(id);
        return gone.Count;
    }

    public bool Remove(string sessionId) => _sessions.Remove(sessionId);

    public void Clear() => _sessions.Clear();

    void Note(string id, DateOnly today)
    {
        if (today != _seenDay)
        {
            _seenDay = today;
            _seen.Clear();
        }
        if (_seen.Count < MaxSeen) _seen.Add(id);
    }
}
