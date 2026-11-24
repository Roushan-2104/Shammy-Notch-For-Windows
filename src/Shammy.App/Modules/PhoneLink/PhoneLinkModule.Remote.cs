using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shammy.Core.Notch;
using Shammy.Infrastructure;
using Shammy.Core.PhoneLink;
using Shammy.Modules.Agents;

namespace Shammy.Modules.PhoneLink;

/// <summary>
/// The phone as a remote (your answers, 15 Nov): this laptop's live moments mirror to the phone's notch (timers, alarms,
/// downloads, meetings, calls, Claude Code working or waiting), each with the one action its circle offers here, and
/// Claude Code's "Allow?" with Deny / Allow; what's playing here with play/pause, next, previous and volume; lock and
/// sleep. The phone can only run what is offered: a moment's own action, the fixed media/volume keys, and the same
/// "lock" / "sleep" commands you would type (so a dry run, "quick-dry on", covers them too).
/// </summary>
internal sealed partial class PhoneLinkModule
{
    const string ClaudeAsk = "claude.ask.";
    static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(5);

    // UI thread only: what the phone was last told, so only changes go out.
    readonly Dictionary<string, string> _sentMoments = new();
    string _sentMedia = "";
    System.Windows.Threading.DispatcherTimer? _refresh;

    void StartRemote()
    {
        _ctx.Activities.Changed += SyncMoments;
        _ctx.Services.Media.Changed += SendMedia;
        if (_ctx.Services.Volume is { } v) v.Changed += OnVolume;
        AgentsLink.AskChanged += SyncMoments;
        // live values (a timer's countdown) change without events: refreshed every few seconds while linked
        _refresh = new System.Windows.Threading.DispatcherTimer(RefreshEvery, System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => SyncMoments(), _ctx.Ui) { IsEnabled = false };
    }

    void StopRemote()
    {
        _ctx.Activities.Changed -= SyncMoments;
        _ctx.Services.Media.Changed -= SendMedia;
        if (_ctx.Services.Volume is { } v) v.Changed -= OnVolume;
        AgentsLink.AskChanged -= SyncMoments;
        _refresh?.Stop();
    }

    /// <summary>A phone just connected (UI thread): tell it everything once.</summary>
    void OnConnected()
    {
        _awayTimer?.Stop(); // back in time: no lock
        _sentMoments.Clear();
        _sentMedia = "";
        _sentStatus = null;
        ShowOnline(true);
        SendStatus();
        // where to find this laptop when the phone is away from its network (or "" for nowhere)
        Send(new JsonObject { ["t"] = "relay", ["at"] = _store.Relay ?? "", ["room"] = _store.Relay is null ? "" : RelayRoom() });
        SendMedia();
        SyncMoments();
        _refresh?.Start();
    }

    void OnDisconnected()
    {
        _refresh?.Stop();
        WalkedAway();
        ForgetPhone();
        ShowOnline(false);
        // the phone's notifications stay in the list and stay linked (a reconnect continues the sync); no reply target
        _replyKey = null;
    }

    void OnVolume(float level, bool muted) => _ctx.Ui.BeginInvoke(SendMedia);

    void SendMedia()
    {
        if (_live is null) return;
        var m = _ctx.Services.Media;
        var v = _ctx.Services.Volume;
        var msg = new JsonObject
        {
            ["t"] = "media", ["has"] = m.HasSession, ["title"] = LinkSession.Cap(m.Title), ["artist"] = LinkSession.Cap(m.Artist),
            ["playing"] = m.IsPlaying, ["volume"] = Math.Round(v?.Level ?? -1, 2), ["muted"] = v?.Muted ?? false,
            ["art"] = ArtFor(m.HasSession ? m.Art : null),
        };
        var json = msg.ToJsonString();
        if (json == _sentMedia) return;
        _sentMedia = json;
        Send(msg);
    }

    ImageSource? _artOf;
    string _art = "";

    /// <summary>The cover, small (96 px JPEG, a few KB), made once per song: the phone shows it on the laptop's music card.</summary>
    string ArtFor(ImageSource? art)
    {
        if (ReferenceEquals(art, _artOf)) return _art;
        _artOf = art;
        _art = "";
        if (art is not BitmapSource b || b.PixelWidth <= 0) return _art;
        try
        {
            double s = 96.0 / Math.Max(b.PixelWidth, b.PixelHeight);
            var small = s < 1 ? new TransformedBitmap(b, new ScaleTransform(s, s)) : (BitmapSource)b;
            var jpeg = new JpegBitmapEncoder { QualityLevel = 80 };
            jpeg.Frames.Add(BitmapFrame.Create(small));
            using var ms = new MemoryStream();
            jpeg.Save(ms);
            if (ms.Length < 32 * 1024) _art = Convert.ToBase64String(ms.ToArray());
        }
        catch (Exception ex) { _ctx.Warn("Making the cover for the phone failed", ex); }
        return _art;
    }

    /// <summary>
    /// The phone pressed something: its new state goes back at once (and again a moment later, for actions that land
    /// asynchronously, like a media session or the microphone), so the phone's buttons never wait for the 5-second refresh.
    /// </summary>
    void EchoSoon()
    {
        foreach (var ms in new[] { 150, 700 })
            UiDelay.After(TimeSpan.FromMilliseconds(ms), () => { SendMedia(); SyncMoments(); }, _ctx.Ui);
    }

    /// <summary>What the phone's notch should show from here right now, by id.</summary>
    Dictionary<string, JsonObject> CurrentMoments()
    {
        var now = DateTime.UtcNow;
        var want = new Dictionary<string, JsonObject>();
        var ask = AgentsLink.Waiting?.Invoke();
        foreach (var a in _ctx.Activities.Ranked)
        {
            // the phone's own notifications (and codes found in them) came from the phone; toasts are sent as they arrive
            if (a.Id.StartsWith(Name, StringComparison.Ordinal) || a.Id == "otp") continue;
            // while Claude Code asks, its question (below) says it all: no second "needs you" row
            if (ask is not null && a.Id.StartsWith("agents.", StringComparison.Ordinal)) continue;
            if (PetActivitySource.From(a, _ctx.Services, now) is not { } live) continue;
            var s = live.State;
            if (s.Costume is PetCostume.Music or PetCostume.Notify or PetCostume.Charging or PetCostume.Earbuds) continue;
            var choices = new JsonArray();
            if (live.Circle is { Enabled: true } c && live.Run is not null) choices.Add(c.Action.ToString());
            want[a.Id] = new JsonObject
            {
                ["t"] = "moment", ["id"] = a.Id, ["kind"] = s.Costume.ToString().ToLowerInvariant(),
                ["title"] = LinkSession.Cap(s.Label), ["text"] = LinkSession.Cap(s.Line.Length > 0 ? $"{s.Value} · {s.Line}" : s.Value),
                ["done"] = s.Done, ["choices"] = choices,
            };
        }
        if (ask is not null)
            want[ClaudeAsk + ask.Value.Id] = new JsonObject
            {
                ["t"] = "moment", ["id"] = ClaudeAsk + ask.Value.Id, ["kind"] = "claude", ["title"] = LinkSession.Cap(ask.Value.Wants),
                ["text"] = LinkSession.Cap(ask.Value.Detail.Length > 0 ? $"{ask.Value.Where}\n{ask.Value.Detail}" : ask.Value.Where),
                ["done"] = false, ["choices"] = new JsonArray("Deny", "Allow"),
            };
        return want;
    }

    void SyncMoments()
    {
        if (_live is null) return;
        var want = CurrentMoments();
        foreach (var (id, m) in want)
        {
            var json = m.ToJsonString();
            if (_sentMoments.TryGetValue(id, out var old) && old == json) continue;
            _sentMoments[id] = json;
            Send(m);
        }
        foreach (var id in _sentMoments.Keys.Where(id => !want.ContainsKey(id)).ToList())
        {
            _sentMoments.Remove(id);
            Send(new JsonObject { ["t"] = "momentGone", ["id"] = id });
        }
    }

    /// <summary>The phone tapped a moment's choice: only the action that moment offers right now runs.</summary>
    void Answer(string id, string choice)
    {
        // only what the phone was shown (and is still live), never any other activity's action
        if (!_sentMoments.ContainsKey(id)) return;
        if (id.StartsWith(ClaudeAsk, StringComparison.Ordinal))
        {
            // and only the request that is waiting now: a late tap never answers the next one
            if (choice is "Allow" or "Deny" && AgentsLink.Waiting?.Invoke() is { } now && ClaudeAsk + now.Id == id && AgentsLink.Answer is { } answer)
                _ctx.Info($"Claude Code answered from the phone: {choice} ({answer(choice == "Allow").Length > 0})");
            EchoSoon();
            return;
        }
        var a = _ctx.Activities.All.FirstOrDefault(x => x.Id == id);
        if (a is null || PetActivitySource.From(a, _ctx.Services, DateTime.UtcNow) is not { Circle: { Enabled: true } c, Run: { } run }) return;
        if (c.Action.ToString() != choice) return;
        run(c.Action);
        _ctx.Info($"The phone ran {choice} on {id}");
        EchoSoon();
    }

    /// <summary>The phone's remote keys. Lock and sleep go through the same commands you would type.</summary>
    void Do(string what)
    {
        var media = _ctx.Services.Media;
        switch (what)
        {
            case "playPause": media.TogglePlayPause(); break;
            case "next": media.Next(); break;
            case "prev": media.Previous(); break;
            case "volUp": _ctx.Services.Volume?.Step(1); break;
            case "volDown": _ctx.Services.Volume?.Step(-1); break;
            case "lock" or "sleep": RunCommand(what); break;
            // the phone as an object (Reach P15): face down = focus (Do Not Disturb, Shammy's own "focus" command), up = its end
            // only a focus the phone started is ended by it (never one you started on the laptop)
            case "focusOn": if (!_phoneFocus) { _phoneFocus = true; RunCommand("focus for 120 minutes"); } break;
            case "focusOff": if (_phoneFocus) { _phoneFocus = false; RunCommand("stop focus"); } break;
            // take it with you (Reach P14): the page that's playing goes to the phone, at the same second, and pauses here
            case "handoff": _ = HandOff(); break;
            default: return;
        }
        _ctx.Info($"The phone pressed {what}");
        EchoSoon();
    }

    /**
     * Take it with you: the address of the page in the browser in front (read the way "send to phone" reads it) goes to
     * the phone as a link, a YouTube video with its place (t=…s) so it carries on at the same second; then the laptop pauses.
     */
    async Task HandOff()
    {
        try
        {
            var page = await PhoneSend.BrowserAddress.ReadAsync(TimeSpan.FromSeconds(2));
            if (page is null || LinkShare.Url(page.Value.Url) is not { } url) { _ctx.Info("Take it with you: no page to hand over"); return; }
            // only when what's playing is that browser's (Spotify playing beside a paused video keeps playing, and gives no place)
            var media = _ctx.Services.Media;
            bool browserMedia = media.HasSession && page.Value.InFront && PhoneSend.BrowserAddress.Browsers.Any(b => media.SourceApp.Contains(b, StringComparison.OrdinalIgnoreCase));
            int at = browserMedia && media.Duration > TimeSpan.Zero ? (int)media.Position.TotalSeconds : 0;
            Send(new JsonObject { ["t"] = "link", ["url"] = LinkShare.WithTime(url, at) });
            if (browserMedia && media.IsPlaying) media.TogglePlayPause();
            _ctx.Info("Handed the page to the phone");
        }
        catch (Exception ex) { _ctx.Warn("Take it with you failed", ex); }
    }

    // ---- walk away, it locks (Reach P15) ----

    /** The phone asked: lock this laptop when it goes away (the link drops and doesn't come back within a minute). */
    bool _awayLock;
    /// <summary>Flip to focus started a focus session here (so picking the phone up ends only that one).</summary>
    bool _phoneFocus;
    System.Windows.Threading.DispatcherTimer? _awayTimer;

    void AwayLock(bool on) => _awayLock = on;

    /// <summary>The link dropped (UI thread): with walk-away lock on, a minute's grace for a Wi-Fi hiccup, then lock.</summary>
    void WalkedAway()
    {
        if (!_awayLock) return;
        _awayTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(60), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) =>
            {
                _awayTimer!.Stop();
                // never while someone is using the laptop (the link can drop with the phone still on the desk)
                bool idle = Nudges.NudgesModule.LastInput.Idle() is { } i && i >= TimeSpan.FromSeconds(60);
                if (_live is null && _awayLock && idle) { _ctx.Info("The phone walked away: locking"); RunCommand("lock"); }
            }, _ctx.Ui);
        _awayTimer.Stop(); _awayTimer.Start();
    }

    async void RunCommand(string text)
    {
        try
        {
            if (_ctx.Commands.FirstOrDefault(c => c.CanHandle(text)) is not { } command)
            {
                _ctx.Warn($"No command handles \"{text}\"");
                return;
            }
            var result = await command.RunAsync(text);
            _ctx.Info($"The phone asked to {text}: {result.Title}");
        }
        catch (Exception ex)
        {
            _ctx.Warn($"Running \"{text}\" for the phone failed", ex);
        }
    }
}
