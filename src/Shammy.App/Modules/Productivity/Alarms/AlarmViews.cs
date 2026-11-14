using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Core.Productivity;
using Shammy.Views.Cards;

namespace Shammy.Modules.Productivity.Alarms;

/// <summary>
/// What the alarm views need from <see cref="AlarmController"/>: the compact card below, the Alarms detail
/// (<see cref="AlarmsDetail"/>), the ringing notch (<see cref="AlarmRingView"/>) and the snoozed/stopped pills.
/// </summary>
internal interface IAlarmHost
{
    IReadOnlyList<Alarm> Alarms { get; }
    Alarm? Ringing { get; }
    int DefaultSnoozeMinutes { get; }

    /// <summary>When it rings next (local time; a snooze counts), or null when off.</summary>
    DateTime? UpcomingLocal(Alarm a);

    void SetEnabled(Alarm a, bool on);
    void Delete(Alarm a);
    void Stop();
    void Snooze(int minutes);

    /// <summary>Adds an alarm from the editor (or turns an identical one back on); returns it.</summary>
    Alarm Add(int hour, int minute, AlarmDays days, string label);

    /// <summary>Changes an alarm's time, days and label (it starts counting again from now).</summary>
    void Edit(Alarm a, int hour, int minute, AlarmDays days, string label);

    /// <summary>A day toggle on the list: only the repeat days change.</summary>
    void SetDays(Alarm a, AlarmDays days);

    /// <summary>A snoozed alarm is done for today (a one-off turns off).</summary>
    void TurnOffSnoozed(Alarm a);

    /// <summary>The settings the Alarms screen shows: snooze length, gentle wake, breaking through fullscreen.</summary>
    void SetSnoozeMinutes(int minutes);
    bool GentleWake { get; set; }
    bool BreaksFullscreen { get; set; }
}

/// <summary>
/// The Alarms card. Compact (176×176): the next alarm's time, its days and how long until it rings (or RINGING /
/// MISSED). Detail (924×248): every alarm with an on/off switch and delete.
/// </summary>
internal sealed class AlarmsCard
{
    const string Accent = NotchPalette.Alarm;
    readonly IAlarmHost _host;
    readonly Grid _compact = new() { Margin = new Thickness(14) };

    public AlarmsCard(IAlarmHost host)
    {
        _host = host;
        // "in 8 h 20 min" only needs to be right when it's looked at.
        _compact.IsVisibleChanged += (_, _) =>
        {
            if (_compact.IsVisible) Refresh();
        };
        Refresh();
    }

    public FrameworkElement Compact => _compact;

    (Alarm Alarm, DateTime At)? Next() =>
        _host.Alarms.Select(a => (Alarm: a, At: _host.UpcomingLocal(a)))
            .Where(x => x.At is not null)
            .OrderBy(x => x.At)
            .Select(x => ((Alarm, DateTime)?)(x.Alarm, x.At!.Value))
            .FirstOrDefault();

    public void Refresh()
    {
        if (_detail is { IsVisible: true } detail) detail.Refresh();
        _compact.Children.Clear();
        var stack = new StackPanel();
        _compact.Children.Add(stack);
        var now = DateTime.Now;
        var missed = _host.Alarms.FirstOrDefault(a => a.MissedUtc is not null);

        if (_host.Ringing is { } ringing)
        {
            stack.Children.Add(CardKit.Label("Ringing", CardKit.Hex(Accent)));
            stack.Children.Add(TimeBig(ringing, 34));
            stack.Children.Add(Sub(ringing.Label.Length > 0 ? ringing.Label : "Alarm"));
            return;
        }
        if (Next() is not { } next)
        {
            stack.Children.Add(CardKit.SectionLabel("Alarms"));
            stack.Children.Add(CardKit.Text("No alarms on", 15, FontWeights.SemiBold));
            stack.Children.Add(Sub("Say “alarm at 7 every weekday”"));
        }
        else
        {
            stack.Children.Add(CardKit.Label(next.Alarm.IsSnoozed ? "Snoozed" : "Next alarm", CardKit.Hex(Accent)));
            var (time, suffix) = ClockPreference.Parts(next.At.Hour, next.At.Minute);
            var big = CardKit.Row(4, CardKit.Text(time, 34, FontWeights.ExtraBold), CardKit.Text(suffix, 13, FontWeights.SemiBold, secondary: true));
            stack.Children.Add(big);
            stack.Children.Add(Sub(AlarmText.Repeat(next.Alarm, now, next.At)));
            stack.Children.Add(Sub(AlarmText.Until(next.At - now)));
        }
        if (missed is not null) stack.Children.Add(Sub(AlarmText.Missed(missed), Accent));
    }

    static FrameworkElement TimeBig(Alarm a, double size)
    {
        var (time, suffix) = ClockPreference.Parts(a.Hour, a.Minute);
        return CardKit.Row(4, CardKit.Text(time, size, FontWeights.ExtraBold), CardKit.Text(suffix, 13, FontWeights.SemiBold, secondary: true));
    }

    static TextBlock Sub(string text, string? accent = null)
    {
        var t = CardKit.Text(text, 12, FontWeights.Medium, secondary: true);
        if (accent is not null) t.Foreground = CardKit.Hex(accent);
        t.Margin = new Thickness(0, 4, 0, 0);
        return t;
    }

    // ---- Detail ----------------------------------------------------------------------------------

    AlarmsDetail? _detail;

    /// <summary>A fresh Alarms screen (the designed list, editor and alarm settings); it follows every change while shown.</summary>
    public FrameworkElement BuildDetail()
    {
        _detail = new AlarmsDetail(_host);
        return _detail;
    }

    // ---- Test pipes ------------------------------------------------------------------------------

    public string DescribeCompact()
    {
        if (_host.Ringing is { } r) return $"alarms: ringing {AlarmText.Time(r)}";
        return Next() is { } n
            ? $"alarms: next={ClockPreference.Format(n.At)} {AlarmText.Repeat(n.Alarm, DateTime.Now, n.At)} {AlarmText.Until(n.At - DateTime.Now)} count={_host.Alarms.Count}"
            : $"alarms: none on count={_host.Alarms.Count}";
    }

    public string DescribeDetail() =>
        "alarms detail: " + string.Join(" | ", _host.Alarms.Select(a =>
            $"{AlarmText.Time(a)} {AlarmText.Repeat(a.Days)}{(a.Label.Length > 0 ? " " + a.Label : "")} {(a.Enabled ? "on" : "off")}"));
}
