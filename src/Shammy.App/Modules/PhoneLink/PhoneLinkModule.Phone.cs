using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shammy.Core.PhoneLink;
using Shammy.Infrastructure;
using Shammy.Modules.PhoneSend;
using Shammy.Views.Cards;

namespace Shammy.Modules.PhoneLink;

/// <summary>
/// The phone, from here (your answers, 21 Nov): its battery, charging and ringer on the Your phone card, with Sound /
/// Vibrate / Silent and Do Not Disturb (when the phone allows it), Find my phone and Send clipboard; its music with
/// cover, keys and volume while it plays; a ringing call as a card with Answer / Decline; a nudge when it's low.
/// This laptop's own battery goes to the phone. The phone runs only what it offers (docs/LINK.md §5 `phoneDo`).
/// Pipes (test only): phonelink-fake-live, -fake-status, -fake-media, -fake-notif, -fake-gone, phonelink-do,
/// phonelink-call, phonelink-peek, phonelink-phone, phonelink-sent.
/// </summary>
internal sealed partial class PhoneLinkModule
{
    const string PhoneGlyph = "\uE8EA";
    static readonly TimeSpan FindFor = TimeSpan.FromSeconds(60), PeekFor = TimeSpan.FromSeconds(12);
    static readonly string[] RingerNames = ["Sound", "Vibrate", "Silent"];

    // UI thread only: what the phone said last.
    PhoneStatus? _phoneStatus;
    PhoneMedia? _phoneMedia;
    ImageSource? _phoneArt;
    int _phoneNudged = 101;
    DispatcherTimer? _findEnds;
    (int Percent, bool Charging)? _sentStatus;
    string? _callKey, _answeredKey;

    /// <summary>Test switch: act as if the phone were online (what would go to it is only noted, see phonelink-sent).</summary>
    bool _fakeLive;

    bool Online => _live is not null || _fakeLive;

    void StartPhone()
    {
        _ctx.Services.Battery.Changed += SendStatus;
        if (!CommandServer.IsEnabled) return;
        _ctx.AddPipeCommand("phonelink-fake-live", arg =>
        {
            _fakeLive = arg.Trim() == "on";
            _sentStatus = null;
            SendStatus();
            FillPhoneSheet();
            return $"fake-live={_fakeLive}";
        });
        // "battery charging ringer dnd canRinger", e.g. "18 0 vibrate 0 1"
        _ctx.AddPipeCommand("phonelink-fake-status", arg =>
        {
            var p = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 1 || !int.TryParse(p[0], out int battery)) return "error: usage phonelink-fake-status <battery> [charging] [ringer] [dnd] [canRinger]";
            bool On(int i) => p.Length > i && p[i] is "1" or "true" or "on";
            OnPhoneSays(new JsonObject
            {
                ["t"] = "status", ["battery"] = battery, ["charging"] = On(1), ["ringer"] = p.Length > 2 ? p[2] : "", ["dnd"] = On(3), ["canRinger"] = On(4),
            });
            return PhoneState();
        });
        // "title|artist|playing|volume", or "off"
        _ctx.AddPipeCommand("phonelink-fake-media", arg =>
        {
            var p = arg.Split('|');
            OnPhoneSays(new JsonObject
            {
                ["t"] = "phoneMedia", ["has"] = arg.Trim() != "off", ["title"] = p[0].Trim(), ["artist"] = p.Length > 1 ? p[1] : "",
                ["playing"] = p.Length > 2 && p[2].Trim() is "1" or "true", ["volume"] = p.Length > 3 && double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0.5,
            });
            return PhoneState();
        });
        // "kind|app|title|text": a phone notification (kind call rings, code carries a one-time code); answers its key
        _ctx.AddPipeCommand("phonelink-fake-notif", arg =>
        {
            var p = arg.Split('|');
            if (p.Length < 4) return "error: usage phonelink-fake-notif kind|app|title|text";
            var key = $"fake.{Environment.TickCount64}";
            ShowPhoneNotification(key, p[1], p[2], p[3], false, p[0].Trim());
            return key;
        });
        _ctx.AddPipeCommand("phonelink-fake-gone", key => { DropPhoneNotification(key.Trim()); return "gone"; });
        _ctx.AddPipeCommand("phonelink-do", arg => PhoneControl(arg.Trim()));
        _ctx.AddPipeCommand("phonelink-call", arg => _callKey is null ? "error: no call" : AnswerCall(arg.Trim() == "answer"));
        _ctx.AddPipeCommand("phonelink-peek", _ => _ctx.Notch.OpenCardContent is PhonePeek p ? p.Describe : "none");
        _ctx.AddPipeCommand("phonelink-phone", _ => PhoneState());
        StartShareTests();
    }

    void StopPhone() => _ctx.Services.Battery.Changed -= SendStatus;

    string PhoneState() => string.Create(CultureInfo.InvariantCulture,
        $"online={Online} status={(_phoneStatus is { } s ? $"{s.Battery}%{(s.Charging ? "+" : "")} {s.Ringer} dnd={s.Dnd} canRinger={s.CanRinger}" : "-")} " +
        $"media={(_phoneMedia is { Has: true } m ? $"\"{m.Title}\" playing={m.Playing} vol={m.Volume} art={_phoneArt is not null}" : "-")} " +
        $"finding={_findEnds is not null} call={_callKey ?? "-"} nudged={(_phoneNudged > 100 ? "-" : _phoneNudged)}");

    /// <summary>The phone went away: its music and call can't be acted on any more (its battery stays, as last seen).</summary>
    void ForgetPhone()
    {
        _phoneMedia = null;
        _phoneArt = null;
        _findEnds?.Stop();
        _findEnds = null;
        CloseCall();
    }

    /// <summary>The phone's status, music, copied text or a link (UI thread).</summary>
    void OnPhoneSays(JsonObject m)
    {
        switch (m["t"]?.GetValue<string>())
        {
            case "status":
                if (PhoneStatus.From(m) is { } s) OnPhoneStatus(s);
                break;
            case "phoneMedia":
                OnPhoneMedia(PhoneMedia.From(m));
                break;
            case "clip":
                TakeClip(m["text"] is JsonValue v && v.TryGetValue<string>(out var text) ? LinkShare.CapClip(text) : "");
                break;
            case "link":
                OpenLink(LinkSession.Text(m, "url"));
                break;
        }
    }

    void OnPhoneStatus(PhoneStatus s)
    {
        _phoneStatus = s;
        if (s.Charging) _phoneNudged = 101;
        else if (LinkStatus.Nudge(s.Battery, s.Charging, _phoneNudged) is { } level)
        {
            _phoneNudged = level;
            // quiet in a call, a meeting, focus or a shared screen, like the other nudges
            if (!QuietState.IsQuiet && !PrivacyState.Presenting) _ctx.ShowMessage(PhoneGlyph, $"Phone at {s.Battery} %", seconds: 5);
            _ctx.Info($"The phone's battery is low ({level} % nudge)");
        }
        FillPhoneTop();
    }

    void OnPhoneMedia(PhoneMedia m)
    {
        // the cover comes only when the song changes
        if (m.Art.Length > 0) _phoneArt = Cover(m.Art);
        else if (!m.SameSong(_phoneMedia)) _phoneArt = null;
        _phoneMedia = m;
        FillPhoneTop();
    }

    static ImageSource? Cover(string base64)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 96;
            image.StreamSource = new MemoryStream(Convert.FromBase64String(base64));
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or IOException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>This laptop's battery to the phone: on connect, then on a whole 5 % step or a charging change.</summary>
    void SendStatus()
    {
        if (!Online) return;
        var b = _ctx.Services.Battery;
        bool has = b.HasBattery, charging = has && b.IsCharging;
        int percent = has ? b.Percent : -1;
        if (!LinkStatus.Moved(_sentStatus, percent, charging)) return;
        _sentStatus = (percent, charging);
        Send(LinkStatus.Laptop(has, percent, charging));
    }

    // ---- The controls ------------------------------------------------------------------------------

    void PhoneDo(string what, JsonObject? with = null)
    {
        var m = with ?? new JsonObject();
        m["t"] = "phoneDo";
        m["what"] = what;
        Send(m);
        _ctx.Info($"Asked the phone to {what}");
    }

    void Ringer(string mode)
    {
        PhoneDo("ringer", new JsonObject { ["mode"] = mode });
        // shown at once; the phone's next status confirms it
        if (_phoneStatus is { } s) _phoneStatus = s with { Ringer = mode };
        FillPhoneTop();
    }

    void Dnd(bool on)
    {
        PhoneDo("dnd", new JsonObject { ["on"] = on });
        if (_phoneStatus is { } s) _phoneStatus = s with { Dnd = on };
        FillPhoneTop();
    }

    /// <summary>Rings the phone loudly (even on silent) until Stop, a tap there, or a minute; the phone stops by itself then too.</summary>
    void Find(bool on)
    {
        PhoneDo(on ? "find" : "findStop");
        _findEnds?.Stop();
        _findEnds = on ? UiDelay.After(FindFor, () => { _findEnds = null; FillPhoneTop(); }, _ctx.Ui) : null;
        FillPhoneTop();
    }

    /// <summary>The card's controls by name, as their clicks would (the test pipe).</summary>
    string PhoneControl(string arg)
    {
        var p = arg.Split(' ', 2);
        switch (p[0])
        {
            case "ringer" when p.Length > 1 && PhoneStatus.Ringers.Contains(p[1]): Ringer(p[1]); break;
            case "dnd" when p.Length > 1: Dnd(p[1] == "on"); break;
            case "find": Find(true); break;
            case "findStop": Find(false); break;
            case "clip": return SendClipboard();
            case "playPause" or "next" or "prev" or "volUp" or "volDown": PhoneDo(p[0]); break;
            default: return "error: usage phonelink-do ringer sound|vibrate|silent | dnd on|off | find | findStop | clip | playPause | next | prev | volUp | volDown";
        }
        return PhoneState();
    }

    // ---- A ringing call ----------------------------------------------------------------------------

    PhonePeek? _callCard;

    /// <summary>A call ringing on the phone: Answer or Decline here, through the call's own buttons there.</summary>
    void ShowCall(string key, string caller)
    {
        // the call already answered (or declined) here: its notification changing is no new call
        if (key == _answeredKey) return;
        var who = PrivacyState.Presenting || caller.Length == 0 ? "Phone call" : caller;
        // dialers post a ringing call again and again under the same key: the card on show just follows
        if (key == _callKey && _callCard is { } open && ReferenceEquals(_ctx.Notch.OpenCardContent, open))
        {
            open.Title = who;
            return;
        }
        _callKey = key;
        var card = _callCard = new PhonePeek(PhoneIcon, "Call on your phone", who, "Audio stays on the phone",
            ("Decline", () => AnswerCall(false)), ("Answer", () => AnswerCall(true)));
        _ctx.ShowCard(card, PhonePeek.CardWidth, PhonePeek.CardHeight, interactive: false);
        PlayPhoneChime();
        _ctx.Info("A call is ringing on the phone");
    }

    string AnswerCall(bool answer)
    {
        if (_callKey is not { } key) return "error: no call";
        PhoneDo(answer ? "answer" : "decline", new JsonObject { ["key"] = key });
        _answeredKey = key;
        CloseCall();
        return answer ? "answered" : "declined";
    }

    /// <summary>The phone's notification left: a ringing call stopped (picked up, declined or missed).</summary>
    void CallGone(string key)
    {
        if (key == _answeredKey) _answeredKey = null;
        if (key == _callKey) CloseCall();
    }

    void CloseCall()
    {
        _callKey = null;
        if (_callCard is { } c && ReferenceEquals(_ctx.Notch.OpenCardContent, c)) _ctx.CloseCard();
        _callCard = null;
    }

    // ---- The Your phone card's top -------------------------------------------------------------------

    /// <summary>
    /// The linked phone at the top of the Your phone card: battery and ringer, the controls, and its music while it has
    /// some. Calm glass; the only accent is the music's play key.
    /// </summary>
    void FillPhoneTop()
    {
        if (_phoneSheet is not { } sheet) return;
        var top = sheet.Top;
        top.Children.Clear();
        top.Margin = new Thickness(0);
        if (!Online) return;
        top.Margin = new Thickness(0, 0, 0, 12);

        if (_phoneStatus is { } st)
        {
            int chosen = Array.IndexOf(PhoneStatus.Ringers, st.Ringer);
            var facts = new List<string>();
            if (chosen >= 0) facts.Add(RingerNames[chosen]);
            if (st.Dnd) facts.Add("Do Not Disturb");
            var battery = Sheet.Row(8, Sheet.Num($"{st.Battery}%", 30));
            if (st.Charging) battery.Children.Add(CardKit.Icon(CardKit.Paths.Bolt, CardKit.Ink, 16, 2));
            var meta = Sheet.Meta(string.Join(" · ", facts));
            meta.Margin = new Thickness(14, 0, 0, 0);
            top.Children.Add(Sheet.Tile(Sheet.Row(0, battery, meta), new Thickness(16, 10, 16, 10)));

            if (st.CanRinger)
            {
                var ringer = Sheet.Segmented(RingerNames, chosen, i => Ringer(PhoneStatus.Ringers[i]), "Ringer");
                var dnd = Sheet.Cap("Do Not Disturb", st.Dnd ? "Turn Do Not Disturb off" : "Turn Do Not Disturb on", () => Dnd(!st.Dnd),
                    st.Dnd ? CardKit.Paths.Check : null);
                top.Children.Add(Spaced(Sheet.Row(8, ringer, dnd)));
            }
        }

        var find = _findEnds is not null
            ? Sheet.Cap("Stop ringing", "Stop ringing the phone", () => Find(false), CardKit.Paths.Close)
            : Sheet.Cap("Find my phone", "Ring the phone loudly, even on silent", () => Find(true), CardKit.Paths.Bell);
        var clip = Sheet.Cap("Send clipboard", "Send what you copied to the phone's clipboard", () => SendClipboard(), CardKit.Paths.Copy);
        top.Children.Add(Spaced(Sheet.Row(8, find, clip)));

        if (_phoneMedia is { Has: true } pm) top.Children.Add(Spaced(MusicTile(pm)));
    }

    static FrameworkElement Spaced(FrameworkElement e)
    {
        e.Margin = new Thickness(0, 10, 0, 0);
        return e;
    }

    /// <summary>The phone's song: cover, title and artist, the three keys, and volume.</summary>
    Border MusicTile(PhoneMedia pm)
    {
        var cover = new Border
        {
            Width = 52,
            Height = 52,
            CornerRadius = new CornerRadius(12),
            Background = _phoneArt is { } art ? new ImageBrush(art) { Stretch = Stretch.UniformToFill } : Sheet.CapFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var words = Sheet.Column(2, Sheet.Word(pm.Title.Length > 0 ? pm.Title : "Playing on your phone", 14), Sheet.Meta(pm.Artist, 12.5));
        words.Margin = new Thickness(12, 0, 10, 0);
        words.VerticalAlignment = VerticalAlignment.Center;
        var keys = Sheet.Row(6,
            Sheet.Round("Previous", CardKit.Paths.Previous, () => PhoneDo("prev"), 32, filled: true),
            Sheet.Round(pm.Playing ? "Pause" : "Play", pm.Playing ? CardKit.Paths.Pause : CardKit.Paths.Play, () => PhoneDo("playPause"), 38, accent: true, filled: true),
            Sheet.Round("Next", CardKit.Paths.Next, () => PhoneDo("next"), 32, filled: true));
        keys.VerticalAlignment = VerticalAlignment.Center;

        var level = Sheet.Bar(Math.Max(0, pm.Volume), 4);
        level.Margin = new Thickness(10, 0, 10, 0);
        var volume = Sheet.Spread(Sheet.Round("Phone volume down", CardKit.Paths.Minus, () => PhoneDo("volDown"), 28),
            Sheet.Round("Phone volume up", CardKit.Paths.Plus, () => PhoneDo("volUp"), 28), level);
        volume.Margin = new Thickness(0, 10, 0, 0);
        return Sheet.Tile(Sheet.Column(0, Sheet.Spread(cover, keys, words), volume), new Thickness(12, 12, 12, 10));
    }

    /// <summary>The Your phone card: while the phone is online, files dropped on it go to the phone.</summary>
    sealed class PhoneCard(PhoneLinkModule m, Action close) : PhoneChoiceSheet(close), Shell.ICardDrop
    {
        public bool AcceptsDrop(IDataObject data) => m.Online && data.GetDataPresent(DataFormats.FileDrop);

        public void DropHover() { }

        public void TakeDrop(IDataObject data)
        {
            if (data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && Connect.Links.Phone is { } send) _ = send.SendAsync(files);
        }
    }
}
