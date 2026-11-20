using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shammy.Core.PhoneLink;
using Shammy.Infrastructure;
using Shammy.Modules.PhoneSend;
using Shammy.Notifications;

namespace Shammy.Modules.PhoneLink;

/// <summary>
/// Shammy Island (the phone's notch) linked to this laptop over the same Wi-Fi, end to end encrypted, no server
/// (protocol v1, docs/LINK.md on the phone branch). The phone's notifications show here like any notification
/// (history, pop-up, the code card for OTPs) and "reply …" answers the latest one; this laptop's notifications go to
/// the phone's notch. Pairing is Bluetooth-style: "Pair a phone…" in the tray opens a 2-minute window, both screens
/// show the same 6 digits, and the user confirms on both. Nothing listens on the network unless a phone is paired or
/// the window is open. Shared-file workaround: phone notifications enter through NotificationService.InjectForTest,
/// the only way in for a module.
/// The phone is also a remote for this laptop: see PhoneLinkModule.Remote.cs.
/// The phone's own state, controls, music and calls: PhoneLinkModule.Phone.cs; text, links and files both ways: .Share.cs.
/// Pipes: phonelink-state, phonelink-pair, phonelink-reply "text", phonelink-beacon.
/// </summary>
internal sealed partial class PhoneLinkModule : IShammyModule
{
    /// <summary>A little longer than the relay holds a lone side (120 s), so the relay is the one that lets go.</summary>
    static readonly TimeSpan RelayWait = TimeSpan.FromSeconds(150);

    static readonly TimeSpan PairWindow = TimeSpan.FromMinutes(2), PairConfirm = TimeSpan.FromSeconds(60),
        HandshakeLimit = TimeSpan.FromSeconds(10), PingEvery = TimeSpan.FromSeconds(30), Silence = TimeSpan.FromSeconds(95);

    const string CheckIcon = "M5 12.5l4.5 4.5L19 7.5", CrossIcon = "M7 7l10 10 M17 7L7 17";

    sealed class Store
    {
        public string? Identity { get; set; }
        public string? Phone { get; set; }
        public string? PhoneName { get; set; }

        /// <summary>The relay for "link from anywhere" ("host:port"), or null for same Wi-Fi only.</summary>
        public string? Relay { get; set; }

        /// <summary>The pair's secret relay room (made at first use, sent to the phone only inside the link).</summary>
        public string? RelayRoom { get; set; }
    }

    ModuleContext _ctx = null!;
    Store _store = new();
    ECDsa _identity = null!;
    const int MaxConnections = 4, MaxPairAttempts = 5;

    (CancellationTokenSource Cts, TcpListener Tcp, UdpClient Udp)? _listen;
    volatile bool _pairing;
    /// <summary>Test switch: stop answering the phone's Wi-Fi question, so it has to come through the relay.</summary>
    volatile bool _lanOff;
    int _connections, _pairAttempts;
    System.Windows.Threading.DispatcherTimer? _pairEnds;
    PhoneChoiceSheet? _pairSheet;
    LinkSession? _live;

    // UI thread only: the phone's notifications on show here, by the phone's key.
    readonly Dictionary<string, ShammyNotification> _shown = new();
    string? _replyKey;
    uint _nextId = 0xF000_0000;

    public string Name => "phonelink";

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        _store = ctx.Load<Store>();
        _identity = LoadIdentity();
        ctx.Services.Notifications.Arrived += OnLaptopNotification;
        ctx.AddTrayItem("Pair a phone…", OpenPairing, () => _store.Phone is null);
        ctx.AddTrayItem("Unpair phone", Unpair, () => _store.Phone is not null);
        ctx.AddCommand(new ReplyCommand(this));
        ctx.AddPipeCommand("phonelink-state", _ =>
            $"paired={(_store.Phone is null ? "no" : _store.PhoneName)} connected={_live is not null} listening={_listen is not null} pairing={_pairing} shown={_shown.Count}");
        ctx.AddPipeCommand("phonelink-pair", _ =>
        {
            if (_store.Phone is not null) return "error: already paired";
            OpenPairing();
            return "pairing window open";
        });
        ctx.AddPipeCommand("phonelink-relay", arg =>
        {
            var at = arg.Trim();
            if (at is "off" or "") _store.Relay = null;
            else if (Relay.Parse(at) is null) return "error: usage phonelink-relay host[:port]|off";
            else _store.Relay = at;
            _ctx.Save(_store);
            if (_store.Phone is not null) { StopListening(); Listen(); }
            return $"relay={_store.Relay ?? "off"}";
        });
        ctx.AddPipeCommand("phonelink-lan", arg => $"lan={((_lanOff = arg.Trim() == "off") ? "off" : "on")}");
        ctx.AddPipeCommand("phonelink-beacon", _ => { Beacon(); return _beacon is { } b ? $"beacon {b.Status}" : "error: no beacon"; });
        ctx.AddPipeCommand("phonelink-reply", text => Reply(text) is { } who ? $"replied to {who}" : "error: nothing to reply to");
        StartRemote();
        StartCard();
        StartSync();
        StartPhone();
        StartShare();
        if (_store.Phone is not null) Listen();
    }

    public void Dispose()
    {
        _ctx.Services.Notifications.Arrived -= OnLaptopNotification;
        StopRemote();
        StopCard();
        StopSync();
        StopPhone();
        StopShare();
        StopBeacon();
        StopListening();
        _identity.Dispose();
    }

    ECDsa LoadIdentity()
    {
        if (_store.Identity is { } saved)
        {
            var key = ECDsa.Create();
            try
            {
                key.ImportPkcs8PrivateKey(Convert.FromBase64String(saved), out _);
                return key;
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                key.Dispose();
                _ctx.Warn("The link key was unreadable; made a new one (the phone must pair again)", ex);
            }
        }
        var fresh = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _store.Identity = Convert.ToBase64String(fresh.ExportPkcs8PrivateKey());
        _store.Phone = _store.PhoneName = null;
        _ctx.Save(_store);
        return fresh;
    }

    // ---- Pairing ---------------------------------------------------------------------------------

    void OpenPairing()
    {
        if (_store.Phone is not null) return;
        _pairing = true;
        _pairAttempts = 0;
        Listen();
        _ctx.ShowMessage("", "Open Shammy Island on your phone and tap Pair", seconds: 8);
        _pairEnds?.Stop();
        _pairEnds = UiDelay.After(PairWindow, () =>
        {
            _pairing = false;
            if (_store.Phone is null && _pairSheet is null) StopListening();
        }, _ctx.Ui);
    }

    void Unpair()
    {
        _store.Phone = _store.PhoneName = null;
        _store.RelayRoom = null; // a new pairing gets a new room
        _ctx.Save(_store);
        _pairing = false;
        StopListening();
        _shown.Clear();
        _replyKey = null;
        _sentToasts.Clear();
        _ctx.ShowMessage("", "Phone unpaired");
        _ctx.Info("Unpaired the phone");
    }

    /// <summary>First connection of an unknown phone: both screens show the digits, and both users' taps pin the keys.</summary>
    async Task<bool> PairAsync(LinkSession s, CancellationToken ct)
    {
        var tap = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!await _ctx.Ui.InvokeAsync(() => ShowPairSheet(s, tap)))
        {
            _ctx.Info("Pairing: another pairing is showing, refused this one");
            return false;
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(PairConfirm);
        try
        {
            var theirs = WaitForPairedAsync(s, limit.Token);
            // Cancelled on the laptop (or timed out), nobody awaits this any more; the connection closing then
            // ends it with an error that was reported as an unobserved task exception. Expected, so observe it.
            _ = theirs.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            // The phone cancelling closes the connection, which ends the wait with an error: close the card at once.
            if (await Task.WhenAny(tap.Task, theirs) == theirs) await theirs;
            if (!await tap.Task.WaitAsync(limit.Token))
            {
                _ctx.Info("Pairing: cancelled on the laptop");
                return false;
            }
            await s.SendAsync(new JsonObject { ["t"] = "paired" }, limit.Token);
            await theirs;
            // pinned before the card closes, so closing it never stops the listener under the new connection
            await _ctx.Ui.InvokeAsync(() =>
            {
                _store.Phone = Convert.ToBase64String(s.PhoneIdentity);
                _store.PhoneName = s.PhoneName;
                _ctx.Save(_store);
                _pairing = false;
                _pairEnds?.Stop();
                _ctx.Info("Paired a phone");
                FillPhoneSheet();
                if (_store.Relay is not null && _listen is { } l) _ = Task.Run(() => RelayLoopAsync(l.Cts.Token));
            });
            return true;
        }
        finally
        {
            await _ctx.Ui.InvokeAsync(ClosePairSheet);
        }
    }

    static async Task WaitForPairedAsync(LinkSession s, CancellationToken ct)
    {
        while ((await s.ReceiveAsync(ct))["t"]?.GetValue<string>() != "paired") { }
    }

    bool ShowPairSheet(LinkSession s, TaskCompletionSource<bool> tap)
    {
        if (_pairSheet is not null || _store.Phone is not null) return false;
        var sheet = new PhoneChoiceSheet(() => { if (tap.TrySetResult(false)) _ctx.Info("Pairing: card closed (Back/Esc)"); });
        sheet.TitleText.Text = s.Sas;
        sheet.TitleText.FontSize = 22;
        sheet.Show($"same digits on {s.PhoneName}?",
        [
            new PhoneChoiceSheet.Row("match", CheckIcon, "They match", $"Pair with {s.PhoneName}", () => tap.TrySetResult(true) ? "paired" : "late"),
            new PhoneChoiceSheet.Row("cancel", CrossIcon, "Cancel", "", () => tap.TrySetResult(false) ? "cancelled" : "late"),
        ]);
        // Closed by a click elsewhere or another card: that's a no.
        sheet.IsVisibleChanged += (_, _) =>
        {
            if (!sheet.IsVisible && !ReferenceEquals(_ctx.Notch.OpenCardContent, sheet) && tap.TrySetResult(false))
                _ctx.Info("Pairing: card left the screen");
        };
        _pairSheet = sheet;
        _ctx.ShowCard(sheet, PhoneChoiceSheet.SheetWidth, sheet.Fit(), interactive: true);
        return true;
    }

    void ClosePairSheet()
    {
        if (_pairSheet is { } sheet && ReferenceEquals(_ctx.Notch.OpenCardContent, sheet)) _ctx.CloseCard();
        _pairSheet = null;
        if (_store.Phone is null && !_pairing) StopListening();
    }

    // ---- Network ---------------------------------------------------------------------------------

    void Listen()
    {
        if (_listen is not null) return;
        TcpListener? tcp = null;
        try
        {
            tcp = new TcpListener(IPAddress.Any, LinkSession.TcpPort) { ExclusiveAddressUse = true };
            tcp.Start(4);
            var udp = new UdpClient(new IPEndPoint(IPAddress.Any, LinkSession.UdpPort));
            var cts = new CancellationTokenSource();
            _listen = (cts, tcp, udp);
            _ = Task.Run(() => AcceptAsync(tcp, cts.Token));
            _ = Task.Run(() => AnswerAsync(udp, cts.Token));
            if (_store.Phone is not null && _store.Relay is not null) _ = Task.Run(() => RelayLoopAsync(cts.Token));
            _ctx.Info("Listening for the phone");
        }
        catch (SocketException ex)
        {
            tcp?.Stop();
            _ctx.Warn("Can't listen for the phone (port in use?)", ex);
            _ctx.ShowMessage("", "Phone link can't start: port in use", error: true);
        }
    }

    void StopListening()
    {
        if (_listen is not { } l) return;
        _listen = null;
        l.Cts.Cancel();
        try { l.Tcp.Stop(); }
        catch (SocketException) { }
        l.Udp.Dispose();
        _live?.Dispose();
        _ctx.Info("Stopped listening for the phone");
    }

    /// <summary>Answers "SHAMMY?1" broadcasts so the phone finds this laptop without typing an address.</summary>
    async Task AnswerAsync(UdpClient udp, CancellationToken ct)
    {
        var id = _identity.ExportSubjectPublicKeyInfo();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await udp.ReceiveAsync(ct);
                if (!_lanOff && LinkSession.DiscoveryReply(r.Buffer, Environment.MachineName, id, _pairing) is { } reply)
                    await udp.SendAsync(reply, r.RemoteEndPoint, ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return; }
            catch (SocketException) { /* an ICMP "port unreachable" from an earlier reply; keep answering */ }
        }
    }

    async Task AcceptAsync(TcpListener tcp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await tcp.AcceptTcpClientAsync(ct); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            // one phone needs one connection (two while it reconnects): anything past that is someone else's
            var from = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            if (Interlocked.Increment(ref _connections) > MaxConnections || !TakeSlot(from))
            {
                Interlocked.Decrement(ref _connections);
                client.Dispose();
                continue;
            }
            _ = Task.Run(async () => { try { await ServeAsync(client, ct); } finally { FreeSlot(from); } });
        }
    }

    readonly Dictionary<IPAddress, int> _perAddress = new();

    bool TakeSlot(IPAddress? a)
    {
        if (a is null) return false;
        lock (_perAddress)
        {
            _perAddress.TryGetValue(a, out int n);
            if (n >= 2) return false;
            _perAddress[a] = n + 1;
            return true;
        }
    }

    void FreeSlot(IPAddress? a)
    {
        if (a is null) return;
        lock (_perAddress)
            if (_perAddress.TryGetValue(a, out int n)) { if (n <= 1) _perAddress.Remove(a); else _perAddress[a] = n - 1; }
    }

    /// <summary>The pair's secret relay room, made once (UI thread: only from OnConnected; the relay loop reads it).</summary>
    string RelayRoom()
    {
        if (_store.RelayRoom is { Length: 32 } r) return r;
        _store.RelayRoom = Relay.NewRoom();
        _ctx.Save(_store);
        return _store.RelayRoom;
    }

    async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        try { await ServeStreamAsync(client.GetStream(), ct, relayed: false); }
        finally
        {
            client.Dispose();
            Interlocked.Decrement(ref _connections);
        }
    }

    /// <summary>
    /// "Link from anywhere": while paired with a relay set, one connection waits at the relay; when the phone can't find
    /// this laptop on its network it comes in there, and the same handshake runs (pinned key only, never pairing).
    /// </summary>
    async Task RelayLoopAsync(CancellationToken ct)
    {
        var wait = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested && _store.Relay is { } at && _store.Phone is { } phone && Relay.Parse(at) is { } hp)
        {
            try
            {
                var room = await _ctx.Ui.InvokeAsync(RelayRoom);
                TcpClient client;
                try { client = await Relay.JoinAsync(hp.Host, hp.Port, room, "laptop", RelayWait, ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is OperationCanceledException or IOException)
                {
                    // nobody came in the relay's waiting time (or it let us go): dial again, no back-off
                    wait = TimeSpan.FromSeconds(5);
                    try { await Task.Delay(TimeSpan.FromSeconds(1), ct); } catch (OperationCanceledException) { return; }
                    continue;
                }
                using var joined = client;
                wait = TimeSpan.FromSeconds(5);
                _ctx.Info("The phone came in through the relay");
                await ServeStreamAsync(joined.GetStream(), ct, relayed: true);
            }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _ctx.Info($"Relay: {ex.GetType().Name}; trying again in {wait.TotalSeconds:0} s");
                try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { return; }
                wait = TimeSpan.FromSeconds(Math.Min(wait.TotalSeconds * 2, 300));
            }
        }
    }

    async Task ServeStreamAsync(Stream stream, CancellationToken ct, bool relayed)
    {
        using var s = new LinkSession(stream, _identity);
        try
        {
            // While unpaired, each attempt is a fresh 1-in-a-million guess at the digits: only a few per window.
            if (_store.Phone is null && Interlocked.Increment(ref _pairAttempts) > MaxPairAttempts)
            {
                _ = _ctx.Ui.BeginInvoke(ClosePairingAfterTooManyTries);
                return;
            }
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                limit.CancelAfter(HandshakeLimit);
                await s.HandshakeAsync(limit.Token);
            }
            var pinned = _store.Phone;
            if (pinned is null && relayed) return; // pairing happens on the same Wi-Fi only, with both screens in hand
            if (pinned is null)
            {
                if (!_pairing || !await PairAsync(s, ct)) return;
            }
            else if (pinned != Convert.ToBase64String(s.PhoneIdentity))
            {
                _ctx.Info("A phone that isn't the paired one tried to connect");
                return;
            }
            await RunAsync(s, ct);
        }
        catch (Exception ex)
        {
            // the network edge: anything a stranger sends ends only that connection
            _ctx.Info($"Phone connection ended ({ex.GetType().Name})");
        }
    }

    void ClosePairingAfterTooManyTries()
    {
        if (!_pairing) return;
        _pairing = false;
        _pairEnds?.Stop();
        if (_pairSheet is null) StopListening();
        _ctx.ShowMessage("", "Pairing stopped: too many tries. Choose “Pair a phone…” again", error: true, seconds: 6);
        _ctx.Warn("Pairing window closed after too many attempts");
    }

    /// <summary>A trusted phone: read its messages until it goes quiet for too long or leaves.</summary>
    async Task RunAsync(LinkSession s, CancellationToken ct)
    {
        await _ctx.Ui.InvokeAsync(() =>
        {
            _live?.Dispose();
            _live = s;
            _ctx.Info("Phone connected");
            OnConnected();
        });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = PingAsync(s, stop.Token);
        try
        {
            while (true)
            {
                using var quiet = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                quiet.CancelAfter(Silence);
                var m = await s.ReceiveAsync(quiet.Token);
                switch (m["t"]?.GetValue<string>())
                {
                    case "ping":
                        await s.SendAsync(new JsonObject { ["t"] = "pong" }, stop.Token);
                        break;
                    case "notif":
                        var n = (Key: LinkSession.Text(m, "key"), App: LinkSession.Text(m, "app"), Title: LinkSession.Text(m, "title"),
                            Text: LinkSession.Text(m, "text"), CanReply: m["canReply"]?.GetValue<bool>() == true, Kind: LinkSession.Text(m, "kind"));
                        if (n.Key.Length > 0) _ = _ctx.Ui.BeginInvoke(() => ShowPhoneNotification(n.Key, n.App, n.Title, n.Text, n.CanReply, n.Kind));
                        break;
                    case "notifGone":
                        var key = LinkSession.Text(m, "key");
                        _ = _ctx.Ui.BeginInvoke(() => DropPhoneNotification(key));
                        break;
                    case "answer":
                        var (id, choice) = (LinkSession.Text(m, "id"), LinkSession.Text(m, "choice"));
                        _ = _ctx.Ui.BeginInvoke(() => Answer(id, choice));
                        break;
                    case "dismiss":
                        var dismissed = LinkSession.Text(m, "id");
                        _ = _ctx.Ui.BeginInvoke(() => DismissFromPhone(dismissed));
                        break;
                    case "version":
                        int code = m["code"] is JsonValue cv && cv.TryGetValue<int>(out int c) ? c : 0;
                        _ = _ctx.Ui.BeginInvoke(() => OnPhoneVersion(code));
                        break;
                    case "getUpdate":
                        _ = _ctx.Ui.BeginInvoke(SendUpdate);
                        break;
                    case "beacon":
                        _ = _ctx.Ui.BeginInvoke(Beacon);
                        break;
                    case "do":
                        var what = LinkSession.Text(m, "what");
                        _ = _ctx.Ui.BeginInvoke(() => Do(what));
                        break;
                    case "status" or "phoneMedia" or "clip" or "link":
                        _ = _ctx.Ui.BeginInvoke(() => OnPhoneSays(m));
                        break;
                    case "file" or "fileChunk" or "fileEnd":
                        TakeFile(m, s);
                        break;
                    // "pong" and anything newer: ignored (§5).
                }
            }
        }
        finally
        {
            stop.Cancel();
            DropIncoming(s);
            _ = _ctx.Ui.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_live, s)) return;
                _live = null;
                OnDisconnected();
            });
        }
    }

    static async Task PingAsync(LinkSession s, CancellationToken ct)
    {
        try
        {
            using var every = new PeriodicTimer(PingEvery);
            while (await every.WaitForNextTickAsync(ct)) await s.SendAsync(new JsonObject { ["t"] = "ping" }, ct);
        }
        catch (Exception) { /* the read loop notices the connection is gone and ends the session */ }
    }

    void Send(JsonObject message)
    {
        Note(message);
        if (_live is not { } s) return;
        _ = Task.Run(async () =>
        {
            try { await s.SendAsync(message, CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { s.Dispose(); }
        });
    }

    // ---- Notifications both ways -----------------------------------------------------------------

    void ShowPhoneNotification(string key, string app, string title, string text, bool canReply, string kind)
    {
        if (kind == "call")
        {
            ShowCall(key, title.Length > 0 ? title : app);
            return;
        }
        DropPhoneNotification(key);
        var name = app.Length > 0 ? app : "Phone";
        var n = new ShammyNotification
        {
            Id = _nextId++,
            AppId = "phone." + name,
            AppName = name,
            Title = title.Length > 0 ? title : name,
            Body = text,
            Time = DateTimeOffset.Now,
        };
        _shown[key] = n;
        if (_shown.Count > 200) _shown.Remove(_shown.MinBy(kv => kv.Value.Time).Key);
        if (canReply) _replyKey = key;
        _ctx.Services.Notifications.InjectForTest(n);
        PlayPhoneChime();
    }

    void DropPhoneNotification(string key)
    {
        CallGone(key);
        if (!_shown.Remove(key, out var n)) return;
        _ctx.Services.Notifications.History.Remove(n);
        if (_replyKey == key) _replyKey = null;
    }

    /// <summary>This laptop's notifications go to the phone's notch (never the phone's own, which came from there).</summary>
    void OnLaptopNotification(ShammyNotification n)
    {
        if (_live is null || n.IsFromPhone) return;
        var text = n.Body.Length > 0 ? $"{n.Title}\n{n.Body}" : n.Title;
        Send(new JsonObject
        {
            ["t"] = "moment", ["id"] = $"toast.{n.Id}", ["kind"] = "app", ["title"] = n.AppName,
            ["text"] = LinkSession.Cap(text),
        });
        RememberSentToast(n.Id);
    }

    /// <summary>Replies to the latest phone notification that can take one; returns who it went to, or null.</summary>
    string? Reply(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || _live is null || _replyKey is not { } key || !_shown.TryGetValue(key, out var n)) return null;
        Send(new JsonObject { ["t"] = "reply", ["key"] = key, ["text"] = LinkSession.Cap(text) });
        _ctx.Info("Sent a reply to the phone");
        return n.Title;
    }

    /// <summary>"reply on my way": answers the phone's latest message from the laptop's keyboard or voice.</summary>
    sealed class ReplyCommand(PhoneLinkModule m) : ITextCommand, ICommandPreview
    {
        const string Prefix = "reply ";

        public bool CanHandle(string text) =>
            text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && m._live is not null && m._replyKey is not null;

        public Task<CommandResult> RunAsync(string text) => Task.FromResult(m.Reply(text[Prefix.Length..]) is { } who
            ? new CommandResult("", $"Replied to {who}")
            : new CommandResult("", "Nothing to reply to", IsError: true));

        public CommandPreview? Preview(string text) =>
            m._replyKey is { } k && m._shown.TryGetValue(k, out var n) ? new CommandPreview("", $"Reply to {n.Title}", n.AppName) : null;
    }
}
