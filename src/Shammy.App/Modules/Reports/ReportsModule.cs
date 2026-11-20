using System.Windows;
using System.Windows.Threading;
using Shammy.Core.Reports;
using Shammy.Infrastructure;
using Shammy.Infrastructure.Reports;
using Shammy.Shell;

namespace Shammy.Modules.Reports;

/// <summary>
/// Offers what <see cref="ProblemWatch"/> found, one card at a time, in a free moment (not during a call, a screen
/// share, a fullscreen app or another card): safe mode's note after a feature was paused, a crash report on the start
/// after a crash, and a report for an error that keeps repeating. Nothing is sent without Send.
/// Pipes: "reports-state", "reports-show crash|error|safe" (a sample card, for looking at it).
/// </summary>
internal sealed class ReportsModule : IShammyModule
{
    public const string ModuleName = "reports";
    static readonly TimeSpan FirstLook = TimeSpan.FromSeconds(25), Retry = TimeSpan.FromSeconds(10), Between = TimeSpan.FromSeconds(30);
    static readonly TimeSpan AskStays = TimeSpan.FromSeconds(40), NoteStays = TimeSpan.FromSeconds(20);

    abstract record Offer;
    sealed record SafeModeOffer(IReadOnlyList<PausedFeature> Paused, Report? Crash) : Offer;
    sealed record ReportOffer(Report Report) : Offer;

    readonly DispatcherTimer _wait = new(DispatcherPriority.Background), _close = new(DispatcherPriority.Background);
    readonly Queue<Offer> _offers = new();
    ModuleContext _ctx = null!;
    FrameworkElement? _card;

    public string Name => ModuleName;

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        _wait.Tick += (_, _) => Look();
        _close.Tick += (_, _) => CloseIfIdle();
        ProblemWatch.ErrorToOffer += OnErrorToOffer;
        ProblemWatch.CanOffer = () => CanOffer;
        ctx.AddPipeCommand("reports-state", _ => State());
        ctx.AddPipeCommand("reports-show", ShowSample);

        var (crash, newlyPaused) = ProblemWatch.Waiting;
        if (newlyPaused.Count > 0) _offers.Enqueue(new SafeModeOffer(newlyPaused, crash));
        else if (crash is not null && CanOffer) _offers.Enqueue(new ReportOffer(crash));
        if (_offers.Count > 0) WaitFor(FirstLook);
    }

    public void Dispose()
    {
        ProblemWatch.ErrorToOffer -= OnErrorToOffer;
        ProblemWatch.CanOffer = null;
        _wait.Stop();
        _close.Stop();
    }

    /// <summary>The user wants offers (Settings) and there's somewhere to send them.</summary>
    bool CanOffer => _ctx.Settings.OfferReports && ReportRelay.IsSetUp;

    bool IsFree => _ctx.Notch.Mode is NotchMode.Idle or NotchMode.Compact or NotchMode.Peek && !QuietState.IsQuiet && !PrivacyState.Presenting;

    string State() =>
        $"{ProblemWatch.State()} offer={_ctx.Settings.OfferReports} relay={(ReportRelay.IsSetUp ? "set" : "none")} " +
        $"queued={_offers.Count} shown={(_card?.Parent is not null ? _card.GetType().Name : "no")} notch={_ctx.Notch.Mode}";

    void OnErrorToOffer(Report report) => _ctx.Ui.BeginInvoke(() =>
    {
        if (!CanOffer || _offers.Any(o => o is ReportOffer r && r.Report.Signature == report.Signature)) return;
        _offers.Enqueue(new ReportOffer(report));
        if (!_wait.IsEnabled && _card?.Parent is null) WaitFor(Retry);
    });

    void WaitFor(TimeSpan delay)
    {
        _wait.Stop();
        _wait.Interval = delay;
        _wait.Start();
    }

    void Look()
    {
        _wait.Stop();
        if (_offers.Count == 0) return;
        // Busy (a call, a game, another card): look again shortly. Offers keep for the run; a crash keeps for the next.
        if (!IsFree || _card?.Parent is not null)
        {
            WaitFor(Retry);
            return;
        }
        switch (_offers.Dequeue())
        {
            case SafeModeOffer s: ShowSafeMode(s.Paused, s.Crash); break;
            case ReportOffer r: ShowReport(r.Report); break;
        }
    }

    // ---- Cards ----------------------------------------------------------------------------------------------

    void ShowSafeMode(IReadOnlyList<PausedFeature> paused, Report? crash)
    {
        // Seen once shown: Settings lists what's paused from now on.
        ProblemWatch.Seen(crash: false, pauseNotices: true);
        var card = new SafeModeCard(paused,
            crash is not null && CanOffer ? () => ShowReport(crash) : null,
            turnOn: () =>
            {
                foreach (var (key, _) in ProblemWatch.Paused.Where(p => paused.Any(f => f == p.Feature))) ProblemWatch.Resume(key);
                Close();
            },
            later: Close);
        Show(card, SafeModeCard.CardWidth, SafeModeCard.CardHeight, interactive: false, stay: AskStays);
    }

    void ShowReport(Report report)
    {
        ReportCard? card = null;
        card = new ReportCard(report,
            send: (r, note) => ReportRelay.SendAsync(r, note),
            resize: (height, interactive) =>
            {
                if (card?.Parent is null) return;
                _ctx.ShowCard(card, ReportCard.CardWidth, height, interactive);
            },
            answered: sent =>
            {
                if (report.IsCrash) ProblemWatch.Seen(crash: true, pauseNotices: false);
                else ProblemWatch.Answered(report);
                Log.Info($"Problem report {(sent ? "sent" : "declined")}: {report.Signature}");
            },
            close: Close);
        Show(card, ReportCard.CardWidth, ReportCard.AskHeight, interactive: false, stay: AskStays);
    }

    void Show(FrameworkElement card, double width, double height, bool interactive, TimeSpan stay)
    {
        card.MouseLeave += (_, _) =>
        {
            if (card.Parent is null) return;
            _close.Stop();
            _close.Interval = NoteStays;
            _close.Start();
        };
        _card = card;
        _ctx.ShowCard(card, width, height, interactive);
        _close.Stop();
        _close.Interval = stay;
        _close.Start();
    }

    /// <summary>Unanswered for a while and nobody's using it: it goes (a crash is offered again next start).</summary>
    void CloseIfIdle()
    {
        if (_card is ReportCard { InUse: true }) return;
        Close();
    }

    void Close()
    {
        _close.Stop();
        // Only if it's still ours on screen (an alarm may have replaced it).
        if (_card?.Parent is not null) _ctx.CloseCard();
        _card = null;
        if (_offers.Count > 0) WaitFor(Between);
    }

    // ---- Samples (pipe) -------------------------------------------------------------------------------------

    string ShowSample(string arg)
    {
        var when = DateTime.UtcNow.AddHours(-13);
        var log = new[]
        {
            "20:31:52.104 [INFO] Dictation: listening", "20:31:55.410 [INFO] Dictation: stopping",
            "20:31:55.412 [WARN] Stopping the microphone failed: ‹path›", "20:32:01.003 [INFO] Dictation: listening",
        };
        var crash = new Report(Report.CrashKind, "crash/voice/sample", ProblemWatch.Version, "AccessViolationException", "Voice › Microphone",
            "WaveInEvent.DoRecording", ProblemWatch.WindowsVersion, when, 1, null, new[] { "NAudio.Wave.WaveInEvent.DoRecording" }, log);
        switch (arg.Trim())
        {
            case "crash":
                ShowReport(crash);
                break;
            case "error":
                ShowReport(crash with
                {
                    Kind = Report.ErrorKind, Signature = "error/music/sample", Error = "Looking up lyrics failed (HttpRequestException)",
                    Area = "Music", Where = "LyricsSource.GetRecordsAsync", WhenUtc = DateTime.UtcNow, Times = 7,
                });
                break;
            case "safe":
                ShowSafeMode(new[] { new PausedFeature("Dictation", ProblemWatch.Version, DateTime.UtcNow) }, crash);
                break;
            default:
                return "error: reports-show crash|error|safe";
        }
        return State();
    }
}
