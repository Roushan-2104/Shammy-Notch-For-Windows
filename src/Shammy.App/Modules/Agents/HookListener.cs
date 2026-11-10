using System.Net;
using System.Net.Sockets;
using Shammy.Core.Agents;

namespace Shammy.Modules.Agents;

/// <summary>One approval question a hook is waiting on. Whoever completes <see cref="Answer"/> decides what it prints.</summary>
internal sealed class PendingAsk(AgentEvent request, DateTime arrivedUtc)
{
    public AgentEvent Request { get; } = request;
    public DateTime ArrivedUtc { get; set; } = arrivedUtc;

    /// <summary>Its card has been on screen (a request nobody could see goes back to Claude Code sooner).</summary>
    public bool Shown { get; set; }

    /// <summary>The JSON the hook prints (allow, deny), or empty for "no decision".</summary>
    public TaskCompletionSource<string> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>How a held question ended.</summary>
internal enum AskEnd
{
    /// <summary>Its answer was sent to the hook.</summary>
    Answered,
    /// <summary>Nobody answered in time; the question went back to Claude Code unanswered.</summary>
    TimedOut,
    /// <summary>The hook went away first (answered in the terminal, or the turn was interrupted).</summary>
    HookGone,
}

/// <summary>
/// The door Claude Code's hooks knock on: a plain TCP listener on 127.0.0.1 only, speaking just enough HTTP for a
/// curl POST. A request must carry the secret in its address or it gets a 404 and nothing is read. Bodies are
/// capped, slow or stuck clients are dropped, and nothing a client sends can take the app down: every connection
/// is handled in its own task with everything caught. Status pings are answered at once with an empty reply; an
/// approval question is held open until it is answered, times out, or the hook hangs up.
/// </summary>
internal sealed class HookListener : IDisposable
{
    const int MaxConnections = 48;
    static readonly TimeSpan HeadTime = TimeSpan.FromSeconds(5), BodyTime = TimeSpan.FromSeconds(15), WriteTime = TimeSpan.FromSeconds(5);

    readonly TcpListener _listener;
    readonly CancellationTokenSource _stop = new();
    readonly string _token;
    readonly Action<AgentEvent, int> _status;
    readonly Action<PendingAsk, int> _ask;
    readonly Action<PendingAsk, AskEnd> _askEnded;
    readonly Action<string, Exception?> _warn;
    int _open, _warned, _served, _refused;

    /// <param name="port">The port to listen on; 0 lets Windows pick a free one (read it from <see cref="Port"/>).</param>
    /// <param name="status">A status event and the port it came from. Called on a worker thread, before the reply.</param>
    /// <param name="ask">An approval question to answer. Called on a worker thread.</param>
    /// <exception cref="SocketException">The port is taken.</exception>
    public HookListener(int port, string token, Action<AgentEvent, int> status, Action<PendingAsk, int> ask,
        Action<PendingAsk, AskEnd> askEnded, Action<string, Exception?> warn)
    {
        _token = token;
        _status = status;
        _ask = ask;
        _askEnded = askEnded;
        _warn = warn;
        _listener = new TcpListener(IPAddress.Loopback, port);
        // Nobody else may listen on this port beside us.
        _listener.ExclusiveAddressUse = true;
        _listener.Start(64);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    public int Port { get; }

    /// <summary>Requests answered since it started (for the test pipe).</summary>
    public int Served => Volatile.Read(ref _served);

    /// <summary>Requests turned away: wrong secret, malformed, too big.</summary>
    public int Refused => Volatile.Read(ref _refused);

    async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex)
            {
                if (_stop.IsCancellationRequested) return;
                Warn("accepting a hook connection failed", ex);
                try { await Task.Delay(200, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            if (Interlocked.Increment(ref _open) > MaxConnections)
            {
                Interlocked.Decrement(ref _open);
                client.Dispose();
                continue;
            }
            _ = Task.Run(() => Serve(client));
        }
    }

    async Task Serve(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                await Handle(client).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // A client that hung up or stalled: nothing to do.
        }
        catch (Exception ex)
        {
            Warn("a hook request failed", ex);
        }
        finally
        {
            Interlocked.Decrement(ref _open);
        }
    }

    async Task Handle(TcpClient client)
    {
        var stream = client.GetStream();
        int clientPort = (client.Client.RemoteEndPoint as IPEndPoint)?.Port ?? 0;

        // ---- The request line and headers.
        byte[] buffer = new byte[HookHttp.MaxHead + 1024];
        int got = 0, headEnd = -1;
        using (var headTime = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            headTime.CancelAfter(HeadTime);
            while (headEnd < 0 && got < buffer.Length)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(got), headTime.Token).ConfigureAwait(false);
                if (n == 0) return;
                got += n;
                headEnd = HookHttp.HeadEnd(buffer.AsSpan(0, got));
            }
        }
        var head = headEnd < 0 ? null : HookHttp.ParseHead(buffer.AsSpan(0, got));
        if (head is null)
        {
            await Refuse(stream, 400).ConfigureAwait(false);
            return;
        }
        // The secret first: without it not one byte of the body is read.
        string? route = HookHttp.Route(head.Method, head.Path, _token);
        if (route is null)
        {
            await Refuse(stream, 404).ConfigureAwait(false);
            return;
        }
        if (head.ContentLength < 0)
        {
            await Refuse(stream, 400).ConfigureAwait(false);
            return;
        }
        if (head.ContentLength > HookHttp.MaxBody)
        {
            await Refuse(stream, 413).ConfigureAwait(false);
            return;
        }

        // ---- The body: keep the beginning (the fields that matter come first), let the rest go by.
        int keep = (int)Math.Min(head.ContentLength, HookHttp.KeepBody);
        byte[] body = new byte[keep];
        int have = Math.Min(got - head.HeadBytes, keep);
        buffer.AsSpan(head.HeadBytes, have).CopyTo(body);
        long seen = got - head.HeadBytes;
        using (var bodyTime = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            bodyTime.CancelAfter(BodyTime);
            if (head.ExpectsContinue && seen < head.ContentLength)
                await stream.WriteAsync(HookHttp.Continue, bodyTime.Token).ConfigureAwait(false);
            byte[] spill = new byte[64 * 1024];
            while (seen < head.ContentLength)
            {
                int n = have < keep
                    ? await stream.ReadAsync(body.AsMemory(have), bodyTime.Token).ConfigureAwait(false)
                    : await stream.ReadAsync(spill.AsMemory(0, (int)Math.Min(spill.Length, head.ContentLength - seen)), bodyTime.Token).ConfigureAwait(false);
                if (n == 0) return;
                if (have < keep) have += n;
                seen += n;
            }
        }

        var e = HookPayload.Parse(body.AsSpan(0, have));
        if (e is null)
        {
            // Not a hook payload. An empty OK: Claude Code must never see an error because of us.
            Interlocked.Increment(ref _refused);
            await Reply(stream, AgentText.NoDecision).ConfigureAwait(false);
            return;
        }

        if (route == HookRoutes.Status || e.Kind != AgentEventKind.Permission)
        {
            // Before the reply, while the hook's process is still there to be looked at.
            _status(e, clientPort);
            await Reply(stream, AgentText.NoDecision).ConfigureAwait(false);
            Interlocked.Increment(ref _served);
            return;
        }

        var pending = new PendingAsk(e, DateTime.UtcNow);
        _ask(pending, clientPort);
        var end = await WaitForAnswer(pending, client.Client).ConfigureAwait(false);
        string answer = end == AskEnd.Answered ? pending.Answer.Task.Result : AgentText.NoDecision;
        pending.Answer.TrySetResult(AgentText.NoDecision);
        _askEnded(pending, end);
        if (end != AskEnd.HookGone) await Reply(stream, answer).ConfigureAwait(false);
        Interlocked.Increment(ref _served);
    }

    async Task<AskEnd> WaitForAnswer(PendingAsk pending, Socket socket)
    {
        var deadline = DateTime.UtcNow.AddSeconds(AgentHooks.AskAnswerSeconds);
        while (true)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return AskEnd.TimedOut;
            var tick = Task.Delay(left < TimeSpan.FromMilliseconds(500) ? left : TimeSpan.FromMilliseconds(500), _stop.Token);
            var done = await Task.WhenAny(pending.Answer.Task, tick).ConfigureAwait(false);
            if (done == pending.Answer.Task) return AskEnd.Answered;
            if (_stop.IsCancellationRequested) return AskEnd.TimedOut;
            // Readable with nothing to read means the other side closed.
            bool gone;
            try { gone = socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0; }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { gone = true; }
            if (gone) return AskEnd.HookGone;
        }
    }

    async Task Refuse(NetworkStream stream, int status)
    {
        Interlocked.Increment(ref _refused);
        await Send(stream, HookHttp.Response(status)).ConfigureAwait(false);
    }

    Task Reply(NetworkStream stream, string body) => Send(stream, HookHttp.Response(200, body));

    async Task Send(NetworkStream stream, byte[] bytes)
    {
        using var time = new CancellationTokenSource(WriteTime);
        await stream.WriteAsync(bytes, time.Token).ConfigureAwait(false);
        await stream.FlushAsync(time.Token).ConfigureAwait(false);
    }

    void Warn(string message, Exception ex)
    {
        // A misbehaving client must not fill the log.
        if (Interlocked.Increment(ref _warned) <= 20) _warn(message, ex);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); }
        catch (SocketException) { /* already closed */ }
    }
}
