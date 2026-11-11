using System.Globalization;
using Microsoft.Win32;
using Shammy.Core.Productivity;

namespace Shammy.Modules.Brief;

/// <summary>What the brief keeps: the day it was last given.</summary>
internal sealed class BriefStore
{
    public string? GivenDay { get; set; }
}

/// <summary>
/// The morning brief: the first time you unlock (or Shammy starts) on a morning, the notch says good morning with
/// the weather, today's meetings, reminders due today and a low battery, and says it aloud in the online voice
/// when a Gnani key is saved. Once a day, mornings only (5 AM to noon); "good morning" or "morning brief" asks for
/// it again any time. Settings › Notch &amp; pet › Morning brief.
/// Pipes: brief-now, brief-state.
/// </summary>
internal sealed class MorningBriefModule : IShammyModule
{
    static readonly TimeSpan AfterUnlock = TimeSpan.FromSeconds(3), AfterStart = TimeSpan.FromSeconds(25);
    const string Glyph = "";

    ModuleContext _ctx = null!;
    BriefStore _store = new();
    string _last = "-";

    public string Name => "brief";

    static string Today => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        _store = ctx.Load<BriefStore>();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        // Started in the morning (the PC was just switched on): the brief comes once things have settled.
        Later(AfterStart, () => MaybeGive());
        ctx.AddCommand(new Command(this));
        ctx.AddPipeCommand("brief-now", _ => Give(force: true));
        ctx.AddPipeCommand("brief-state", _ => $"given={_store.GivenDay ?? "-"} today={Today} enabled={_ctx.Settings.MorningBrief} last='{_last}'");
    }

    public void Dispose() => SystemEvents.SessionSwitch -= OnSessionSwitch;

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon)
            _ctx.Ui.BeginInvoke(() => Later(AfterUnlock, () => MaybeGive()));
    }

    void Later(TimeSpan wait, Action run)
    {
        var t = new System.Windows.Threading.DispatcherTimer(wait, System.Windows.Threading.DispatcherPriority.Background, (s, _) =>
        {
            ((System.Windows.Threading.DispatcherTimer)s!).Stop();
            run();
        }, _ctx.Ui);
        t.Start();
    }

    void MaybeGive()
    {
        if (!_ctx.Settings.MorningBrief || _store.GivenDay == Today || !MorningBrief.IsMorning(DateTime.Now)) return;
        Give(force: false);
    }

    /// <summary>Composes the brief, shows it, and says it when the online voice can. Returns the line shown.</summary>
    internal string Give(bool force)
    {
        var now = DateTime.Now;
        var meetings = Calendar.CalendarModule.Agenda
            .Where(o => !o.AllDay && o.Start.Date == now.Date)
            .Select(o => new BriefMeeting(o.Start, o.Title))
            .ToList();
        var reminders = Connect.Links.Notes?.Reminders.Where(r => !r.Done && r.Due.Date == now.Date).OrderBy(r => r.Due).Select(r => r.Text).ToList()
                        ?? new List<string>();
        var b = _ctx.Services.Battery;
        int? battery = b.HasBattery && !b.IsCharging ? b.Percent : null;
        var (spoken, line) = MorningBrief.Compose(now, HostLinks.WeatherGlance(), meetings, reminders, battery, Core.Productivity.ClockPreference.Is24Hour);

        if (!force)
        {
            _store.GivenDay = Today;
            _ctx.Save(_store);
        }
        _ctx.ShowMessage(Glyph, line, seconds: 8);
        // Aloud in the online voice when there is a key (it stays quiet in a call, a meeting or with sounds off).
        HostLinks.SayAnyway?.Invoke(spoken);
        _ctx.Info("Morning brief given");
        return _last = line;
    }

    sealed class Command(MorningBriefModule module) : ITextCommand
    {
        static bool Asks(string text) => Core.Text.PhraseText.Normalize(text) is "good morning" or "morning brief" or "my morning brief" or "brief me" or "daily brief" or "suprabhat";

        public bool CanHandle(string text) => Asks(text);

        public Task<CommandResult> RunAsync(string text) => Task.FromResult(new CommandResult(Glyph, module.Give(force: true)));
    }
}
