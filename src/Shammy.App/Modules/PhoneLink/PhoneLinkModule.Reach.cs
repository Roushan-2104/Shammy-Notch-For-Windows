using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Shammy.Core.Activities;
using Shammy.Core.Notch;
using Shammy.Core.PhoneLink;
using Shammy.Interop;

namespace Shammy.Modules.PhoneLink;

/// <summary>
/// Reach (24 Nov): the phone controls this laptop. The phone turns it on (after your fingerprint there) with
/// <c>reach</c> {on: true}; until then every key, text, move, click and scroll is ignored. While it's on, the notch says
/// "Phone · In control" with Stop, which ends it on both sides. It also ends when the phone says so or that connection
/// drops; input still waiting is dropped and any mouse button the phone was holding is let go.
/// Trust: the fingerprint is asked on the phone; the laptop trusts its paired phone's "on" (shortcut: no laptop-side
/// "Allow Reach" setting yet).
/// </summary>
internal sealed partial class PhoneLinkModule
{
    const string ReachActivity = "phonelink.reach";
    const int ReachPriority = 24;

    readonly ReachGate _reach = new();
    ReachInjector? _injector;
    string _lastBlocked = "";
    long _lastBlockedAt;

    /// <summary>A Reach message from the phone on connection [s] (that connection's reader thread).</summary>
    void OnReach(JsonObject m, LinkSession s)
    {
        if (LinkSession.Text(m, "t") != "reach")
        {
            _reach.Admit(m, DateTime.UtcNow, input => _injector?.Send(input));
            return;
        }
        if (m["on"] is JsonValue v && v.TryGetValue<bool>(out var on) && on)
        {
            _injector ??= NewInjector();
            if (_reach.Start(s))
                _ = _ctx.Ui.BeginInvoke(() =>
                {
                    _ctx.Post(new Activity(ReachActivity, ActivityKind.Custom, ReachPriority, new ReachContent(() => EndReach(null, tellPhone: true))));
                    _ctx.Info("Reach on: the phone controls this laptop");
                    _sentFront = "";
                    SendFront();
                    _frontTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(1), System.Windows.Threading.DispatcherPriority.Background,
                        (_, _) => SendFront(), _ctx.Ui);
                    _frontTimer.Start();
                });
        }
        else EndReach(s, tellPhone: false);
    }

    /// <summary>Ends Reach (any thread; [owner]: only if that connection started it, null: whichever).</summary>
    void EndReach(LinkSession? owner, bool tellPhone)
    {
        bool ended = _reach.End(owner, held =>
        {
            _injector?.Clear();
            foreach (var b in held) _injector?.Send(new ReachButton(b, ReachPress.Up));
        });
        if (!ended) return;
        if (tellPhone) Send(new JsonObject { ["t"] = "reach", ["on"] = false });
        _ = _ctx.Ui.BeginInvoke(() =>
        {
            _frontTimer?.Stop();
            _ctx.Remove(ReachActivity);
            _ctx.Info("Reach off");
        });
    }

    System.Windows.Threading.DispatcherTimer? _frontTimer;
    string _sentFront = "";

    /// <summary>
    /// While Reach is on (UI thread, every second): the app in front and its window's title, when they change, so the
    /// phone's panel names what your keys will land in ("Chrome · YouTube"). Shammy's own windows are skipped.
    /// </summary>
    void SendFront()
    {
        if (!_reach.On) return;
        var front = FrontWindow.Ordinary;
        var key = $"{front.Process}\n{front.Title}";
        if (key == _sentFront) return;
        _sentFront = key;
        // the window in front's own app, by its process name ("chrome" → "Chrome"): no process lookups on title changes
        var name = Shammy.Core.CircleMemory.AppNames.Friendly(front.Process) ?? front.Process ?? "";
        Send(new JsonObject { ["t"] = "front", ["app"] = LinkSession.Cap(name), ["title"] = LinkSession.Cap(front.Title ?? "") });
    }

    ReachInjector NewInjector()
    {
        var i = new ReachInjector(msg => _ctx.Warn(msg));
        i.Blocked += why =>
        {
            // once per reason every few seconds, not once per pointer move (injector thread only)
            long now = Environment.TickCount64;
            if (why == _lastBlocked && now - _lastBlockedAt < 5000) return;
            (_lastBlocked, _lastBlockedAt) = (why, now);
            Send(new JsonObject { ["t"] = "reachNote", ["why"] = why });
        };
        return i;
    }

    void DisposeReach()
    {
        EndReach(null, tellPhone: false);
        _injector?.Dispose();
    }

    /// <summary>The notch while the phone is in control: "Phone · In control" and a Stop circle.</summary>
    sealed class ReachContent(Action stop) : ICompactContent, IPetActivity
    {
        static readonly CircleSpec StopCircle = new(CircleAction.Stop, CircleTone.Neutral, true, "Stop the phone's control");

        public PetActivityState Describe(DateTime utcNow) => new()
        {
            Costume = PetCostume.Custom,
            Face = PetFace.Focused,
            Label = "Phone",
            Value = "In control",
            Ongoing = true,
            Circle = StopCircle,
        };

        public void RunCircle(CircleAction action)
        {
            if (action == CircleAction.Stop) stop();
        }

        public double Build(ContentControl left, ContentControl right)
        {
            left.Content = Words("Phone", "Fg2");
            right.Content = Words("In control", "Fg");
            return 64;
        }

        static TextBlock Words(string text, string brush) => new()
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Tokens.Res(brush),
            VerticalAlignment = VerticalAlignment.Center,
        };

        public UIElement? BuildBubble() => null;
    }
}
