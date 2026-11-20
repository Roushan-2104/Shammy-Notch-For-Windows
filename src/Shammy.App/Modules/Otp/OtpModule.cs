using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Shammy.Core.Activities;
using Shammy.Core.Notch;
using Shammy.Core.Notifications;
using Shammy.Infrastructure;

namespace Shammy.Modules.Otp;

/// <summary>
/// One-time codes at a glance: when a message (WhatsApp, Phone Link SMS, a mail app) carries an OTP, the notch
/// shows the code big with a Copy circle for two minutes, the pet in its ninja mask. Copying marks the code private
/// so neither Shammy's clipboard history nor Windows' (Win+V) keeps it, and a minute later the clipboard is
/// emptied if it still holds the code. While the screen is shared or recorded, or always with "Always hide message
/// text", the digits are hidden. A code from the linked phone is copied at once the same private way, with "Code copied"
/// on its pill, except while the screen is shared or recorded. The code is never written to the log.
/// Pipes: otp-test "App|text", otp-state, otp-copy, otp-clear-now.
/// </summary>
internal sealed class OtpModule : IShammyModule
{
    public const string ActivityId = "otp";
    static readonly TimeSpan Shown = TimeSpan.FromMinutes(2);

    // Above messages (50) and the music: a code is wanted right now. Below calls and alarms.
    const int Priority = 85;

    ModuleContext _ctx = null!;
    string? _code, _app;
    bool _copied, _auto;

    // the code copied by itself last (only its hash: codes aren't kept past their pill), and when (UI thread)
    int? _autoCode;
    DateTime _autoAt;
    OtpContent? _content;

    public string Name => "otp";

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        ctx.Services.Notifications.Arrived += OnArrived;
        PrivacyState.Changed += OnPrivacy;
        ctx.AddPipeCommand("otp-test", arg =>
        {
            var parts = arg.Split('|', 2);
            return Offer(parts[0], parts.Length > 1 ? parts[1] : "", parts[0]) ? "shown" : "no code";
        });
        ctx.AddPipeCommand("otp-state", _ =>
            $"shown={(_ctx.Get(ActivityId) is null ? "no" : "yes")} app={_app ?? "-"} digits={_code?.Length ?? 0} copied={_copied} masked={Masked} " +
            $"clearing={(_onClipboard is null ? "no" : "yes")}");
        ctx.AddPipeCommand("otp-copy", _ => Copy() ? "copied" : "error: nothing to copy");
        // Test hook: the minute's wait for the clipboard to be cleared, now.
        ctx.AddPipeCommand("otp-clear-now", _ =>
        {
            bool waiting = _onClipboard is not null;
            ClearNow();
            return waiting ? "cleared if it still held the code" : "nothing waiting";
        });
    }

    public void Dispose()
    {
        _ctx.Services.Notifications.Arrived -= OnArrived;
        PrivacyState.Changed -= OnPrivacy;
        // Leaving with a code still on the clipboard: take it off now.
        if (_onClipboard is not null && _ctx.Ui.CheckAccess())
        {
            try { ClearNow(); }
            catch (Exception ex) { _ctx.Warn("Clearing the copied code failed", ex); }
        }
    }

    void OnArrived(Notifications.ShammyNotification n)
    {
        try
        {
            if (!Offer(n.Title, n.Body, n.AppName) || !n.IsFromPhone || PrivacyState.Presenting) return;
            // the same notification posted again: never over whatever was copied since
            if (_code?.GetHashCode() == _autoCode && DateTime.UtcNow - _autoAt < Shown) return;
            _auto = true; // first, so the pill the copy refreshes says "Code copied"
            _auto = Copy();
            if (_auto) (_autoCode, _autoAt) = (_code!.GetHashCode(), DateTime.UtcNow);
        }
        catch (Exception ex) { _ctx.Warn("Looking for a code failed", ex); }
    }

    bool Offer(string title, string body, string app)
    {
        if (!_ctx.Settings.OtpEnabled || OtpFinder.Find(title, body) is not { } code) return false;
        _code = code;
        _app = app;
        _copied = _auto = false;
        var content = _content = new OtpContent(this);
        _ctx.Post(new Activity(ActivityId, ActivityKind.Custom, Priority, content, DateTime.UtcNow + Shown) { Click = () => Copy() });
        // Once its pill has gone, the code isn't kept either (unless a newer code took its place).
        UiDelay.After(Shown, () =>
        {
            if (_content != content) return;
            _code = _app = null;
            _content = null;
        }, _ctx.Ui);
        // Never the code itself: the log isn't a place for secrets.
        _ctx.Info($"A one-time code from {app} is in the notch");
        return true;
    }

    void OnPrivacy(bool _) => _ctx.Ui.BeginInvoke(() =>
    {
        if (_ctx.Get(ActivityId) is { } a) _ctx.Post(a);
    });

    internal string Label => _copied ? _auto ? "Code copied" : "Copied" : string.IsNullOrWhiteSpace(_app) ? "Code" : _app!;

    /// <summary>
    /// Whether the digits are hidden: while the screen is shared or recorded, and always with Settings' "Always
    /// hide message text" (the code is message text). Copy still copies it.
    /// </summary>
    internal bool Masked => PrivacyState.Presenting || _ctx.Settings.AlwaysHideNotificationText;

    /// <summary>The digits to show: spaced for reading, or dots when <see cref="Masked"/>.</summary>
    internal string Shown_ => _code is null ? "" : Masked ? new string('•', Math.Min(6, _code.Length)) : OtpFinder.Spaced(_code);

    /// <summary>A copied code leaves the clipboard this long after, if nothing else was copied since.</summary>
    static readonly TimeSpan ClearAfter = TimeSpan.FromSeconds(60);

    System.Windows.Threading.DispatcherTimer? _clear;
    string? _onClipboard;

    /// <summary>Copies the code, marked private (no clipboard history, no cloud clipboard); it's cleared a minute later.</summary>
    internal bool Copy()
    {
        if (_code is null || !SelectionTools.ClipboardText.TrySet(_code, secret: true)) return false;
        _copied = true;
        ClearLater(_code);
        if (_ctx.Get(ActivityId) is { } a) _ctx.Post(a);
        return true;
    }

    void ClearLater(string code)
    {
        _onClipboard = code;
        _clear ??= new System.Windows.Threading.DispatcherTimer(ClearAfter, System.Windows.Threading.DispatcherPriority.Background, (_, _) => ClearNow(), _ctx.Ui);
        _clear.Stop();
        _clear.Start();
    }

    /// <summary>Empties the clipboard, but only if it still holds the code (something copied since is left alone).</summary>
    void ClearNow()
    {
        _clear?.Stop();
        var code = _onClipboard;
        _onClipboard = null;
        if (code is null) return;
        for (int i = 0; i < 3; i++)
        {
            try
            {
                if (Clipboard.ContainsText() && Clipboard.GetText() == code)
                {
                    Clipboard.Clear();
                    _ctx.Info("The copied code left the clipboard");
                }
                return;
            }
            catch (ExternalException)
            {
                Thread.Sleep(40);
            }
        }
    }

    sealed class OtpContent(OtpModule module) : ICompactContent, IPetActivity
    {
        static readonly CircleSpec CopyCircle = new(CircleAction.Copy, CircleTone.Accent, true, "Copy the code");

        public PetActivityState Describe(DateTime utcNow) => new()
        {
            Costume = PetCostume.Custom,
            Face = PetFace.Ninja,
            Label = module.Label,
            Value = module.Shown_,
            Ongoing = true,
            Circle = CopyCircle,
        };

        public void RunCircle(CircleAction action)
        {
            if (action == CircleAction.Copy) module.Copy();
        }

        public double Build(ContentControl left, ContentControl right)
        {
            left.Content = Words(module.Label, "Fg2", 12);
            right.Content = Words(module.Shown_, "Fg", 15);
            return 80;
        }

        static TextBlock Words(string text, string brush, double size) => new()
        {
            Text = text,
            FontSize = size,
            FontWeight = FontWeights.Bold,
            Foreground = Ui.Tokens.Res(brush),
            VerticalAlignment = VerticalAlignment.Center,
        };

        public UIElement? BuildBubble() => null;
    }
}
