using System.Windows.Threading;
using Shammy.Core.Updates;
using Shammy.Infrastructure;
using Shammy.Shell;

namespace Shammy.Modules.WhatsNew;

/// <summary>
/// After an update, tells the user what changed: once, on the first start of the new version, in a small card from
/// the notch. It waits for a free moment (not during a call, a screen share, a fullscreen app or another card) and
/// closes itself; hovering keeps it open. A copy that isn't installed never updates, so it never shows this.
/// Pipes: "whatsnew-state", "whatsnew-show [version]" (shows that version's lines whatever has been seen).
/// </summary>
internal sealed class WhatsNewModule : IShammyModule
{
    static readonly TimeSpan FirstLook = TimeSpan.FromSeconds(10), Retry = TimeSpan.FromSeconds(10), GiveUp = TimeSpan.FromMinutes(15);
    static readonly TimeSpan Stay = TimeSpan.FromSeconds(12);

    readonly DispatcherTimer _wait = new(DispatcherPriority.Background), _close = new(DispatcherPriority.Background);
    ModuleContext _ctx = null!;
    WhatsNewCard? _card;
    IReadOnlyList<string> _lines = Array.Empty<string>();
    string _version = "";
    DateTime _waitStarted;

    public string Name => "whatsnew";

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        _wait.Tick += (_, _) => Look();
        _close.Interval = Stay;
        _close.Tick += (_, _) => Close();
        ctx.AddPipeCommand("whatsnew-state", _ => State());
        ctx.AddPipeCommand("whatsnew-show", arg =>
        {
            var version = arg.Trim();
            if (!Shammy.Core.Updates.WhatsNew.Notes.TryGetValue(version, out var lines)) return "error: no lines for that version";
            Show(version, lines);
            return State();
        });

        _waitStarted = DateTime.UtcNow;
        _wait.Interval = FirstLook;
        _wait.Start();
    }

    public void Dispose()
    {
        _wait.Stop();
        _close.Stop();
    }

    string State() =>
        $"lastRun={_ctx.Settings.LastRunVersion ?? "-"} version={HostLinks.Version?.Invoke() ?? "?"} waiting={_wait.IsEnabled} " +
        $"shown={(_card?.Parent is not null ? "yes" : "no")} lines={_lines.Count} notch={_ctx.Notch.Mode}";

    void Look()
    {
        _wait.Stop();
        if (HostLinks.UpdateStatus?.Invoke() is not { Installed: true } || HostLinks.Version?.Invoke() is not { } version) return;

        if (_version != version)
        {
            _version = version;
            var s = _ctx.Settings;
            _lines = Shammy.Core.Updates.WhatsNew.LinesFor(s.LastRunVersion, version, s.StartupAsked || s.TourSeen);
        }
        if (_lines.Count == 0)
        {
            Remember(version);
            return;
        }
        if (!IsFree)
        {
            // Busy the whole time (a long call, a game): it shows next start.
            if (DateTime.UtcNow - _waitStarted < GiveUp)
            {
                _wait.Interval = Retry;
                _wait.Start();
            }
            return;
        }
        Remember(version);
        Show(version, _lines);
    }

    bool IsFree => _ctx.Notch.Mode is NotchMode.Idle or NotchMode.Compact or NotchMode.Peek && !QuietState.IsQuiet && !PrivacyState.Presenting;

    void Remember(string version)
    {
        if (_ctx.Settings.LastRunVersion == version) return;
        _ctx.Settings.LastRunVersion = version;
        _ctx.Settings.Save();
    }

    void Show(string version, IReadOnlyList<string> lines)
    {
        var card = new WhatsNewCard(version, lines, Close, Guide.GuideLinks.OpenNew is { } openNew
            ? () =>
            {
                Close();
                openNew();
            }
            : null);
        card.MouseEnter += (_, _) => _close.Stop();
        card.MouseLeave += (_, _) =>
        {
            if (card.Parent is not null) _close.Start();
        };
        _card = card;
        // Not interactive: it never takes focus; Got it works by mouse.
        _ctx.ShowCard(card, WhatsNewCard.CardWidth, WhatsNewCard.HeightFor(lines.Count), interactive: false);
        _close.Stop();
        _close.Start();
    }

    void Close()
    {
        _close.Stop();
        // Only if it's still ours on screen (an alarm may have replaced it).
        if (_card?.Parent is not null) _ctx.CloseCard();
        _card = null;
    }
}
