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
/// so neither Shammy's clipboard history nor Windows' (Win+V) keeps it. While the screen is shared or recorded
/// the digits are hidden. The code is never written to the log.
/// Pipes: otp-test "App|text", otp-state, otp-copy.
/// </summary>
internal sealed class OtpModule : IShammyModule
{
    public const string ActivityId = "otp";
    static readonly TimeSpan Shown = TimeSpan.FromMinutes(2);

    // Above messages (50) and the music: a code is wanted right now. Below calls and alarms.
    const int Priority = 85;

    ModuleContext _ctx = null!;
    string? _code, _app;
    bool _copied;
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
            $"shown={(_ctx.Get(ActivityId) is null ? "no" : "yes")} app={_app ?? "-"} digits={_code?.Length ?? 0} copied={_copied} masked={PrivacyState.Presenting}");
        ctx.AddPipeCommand("otp-copy", _ => Copy() ? "copied" : "error: nothing to copy");
    }

    public void Dispose()
    {
        _ctx.Services.Notifications.Arrived -= OnArrived;
        PrivacyState.Changed -= OnPrivacy;
    }

    void OnArrived(Notifications.ShammyNotification n)
    {
        try { Offer(n.Title, n.Body, n.AppName); }
        catch (Exception ex) { _ctx.Warn("Looking for a code failed", ex); }
    }

    bool Offer(string title, string body, string app)
    {
        if (!_ctx.Settings.OtpEnabled || OtpFinder.Find(title, body) is not { } code) return false;
        _code = code;
        _app = app;
        _copied = false;
        _content = new OtpContent(this);
        _ctx.Post(new Activity(ActivityId, ActivityKind.Custom, Priority, _content, DateTime.UtcNow + Shown) { Click = () => Copy() });
        // Never the code itself: the log isn't a place for secrets.
        _ctx.Info($"A one-time code from {app} is in the notch");
        return true;
    }

    void OnPrivacy(bool _) => _ctx.Ui.BeginInvoke(() =>
    {
        if (_ctx.Get(ActivityId) is { } a) _ctx.Post(a);
    });

    internal string Label => _copied ? "Copied" : string.IsNullOrWhiteSpace(_app) ? "Code" : _app!;

    /// <summary>The digits to show: spaced for reading, or dots while the screen is shared.</summary>
    internal string Shown_ => _code is null ? "" : PrivacyState.Presenting ? new string('•', Math.Min(6, _code.Length)) : OtpFinder.Spaced(_code);

    /// <summary>Copies the code, marked private (no clipboard history, no cloud clipboard).</summary>
    internal bool Copy()
    {
        if (_code is null) return false;
        for (int i = 0; i < 3; i++)
        {
            try
            {
                var data = new DataObject(DataFormats.UnicodeText, _code);
                data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
                data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
                data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));
                Clipboard.SetDataObject(data, true);
                _copied = true;
                if (_ctx.Get(ActivityId) is { } a) _ctx.Post(a);
                return true;
            }
            catch (ExternalException)
            {
                // Another app has the clipboard open for a moment.
                Thread.Sleep(40);
            }
        }
        return false;
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
