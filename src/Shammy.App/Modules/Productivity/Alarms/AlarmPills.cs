using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using System.Windows.Threading;
using Shammy.Core.Productivity;
using Shammy.Views.Screens;

namespace Shammy.Modules.Productivity.Alarms;

/// <summary>
/// The snoozed alarm in the compact notch: the pet dozing beside "SNOOZED" on the left; on the right the alarm's sun
/// (or moon) climbing a small arc as the snooze runs out, the countdown to the next ring and a round "turn off for
/// today" button. Clicking the pill itself opens Alarms. The countdown ticks only while the pill is on screen.
/// </summary>
internal sealed class SnoozedPill : ICompactContent
{
    readonly Alarm _alarm;
    readonly Action _turnOff;
    readonly TimeSpan _length;
    SpacedText? _countdown;
    SunArc? _arc;
    DispatcherTimer? _tick;

    /// <param name="usualMinutes">This alarm's usual snooze: with 5 and 15, the lengths a snooze can have.</param>
    public SnoozedPill(Alarm alarm, int usualMinutes, Action turnOff)
    {
        _alarm = alarm;
        _turnOff = turnOff;
        // The snooze's whole length: the shortest one it can be that still covers what is left (after a restart
        // the pill is rebuilt partway through, so what is left is not the whole of it).
        var left = (alarm.SnoozedUntilUtc ?? DateTime.UtcNow) - DateTime.UtcNow;
        int minutes = new[] { 5, 10, 15, usualMinutes, 60 }.Where(m => TimeSpan.FromMinutes(m) >= left).DefaultIfEmpty(60).Min();
        _length = TimeSpan.FromMinutes(minutes);
    }

    public Alarm Alarm => _alarm;

    public double Build(ContentControl left, ContentControl right)
    {
        var face = new PetFace(0.62, FaceMood.Sleep) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(-4, 0, 6, 0) };
        var label = ScreenKit.Label("Snoozed", size: 10.5);
        var l = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        l.Children.Add(face);
        l.Children.Add(label);
        left.Content = l;

        _countdown = ScreenKit.Display("", 14, Weight.Bold, 0);
        _countdown.Tabular = true;
        var off = ScreenKit.Round(ScreenKit.Paths.BellOff, _turnOff, 24, 12, ScreenKit.Tile,
            $"Turn off the {AlarmText.Time(_alarm)} alarm for today");
        off.Margin = new Thickness(8, 0, 0, 0);
        var r = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _arc = new SunArc(AlarmSkyLook.For(_alarm)) { Margin = new Thickness(0, 0, 10, 0) };
        r.Children.Add(_arc);
        r.Children.Add(_countdown);
        r.Children.Add(off);
        right.Content = r;
        if (_alarm.SnoozedUntilUtc is { } until) r.ToolTip = $"Rings again at {ClockPreference.Format(until.ToLocalTime())}";

        _tick?.Stop();
        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => Refresh();
        r.IsVisibleChanged += (_, _) =>
        {
            if (r.IsVisible)
            {
                Refresh();
                _tick.Start();
            }
            else _tick.Stop();
        };
        Refresh();
        _countdown.Text = "00:00";
        double width = Math.Max(Measure(l), Measure(r));
        Refresh();
        return Math.Ceiling(width) + 2;
    }

    void Refresh()
    {
        if (_countdown is null) return;
        var left = (_alarm.SnoozedUntilUtc ?? DateTime.UtcNow) - DateTime.UtcNow;
        _countdown.Text = TimeFormat.Countdown(left < TimeSpan.Zero ? TimeSpan.Zero : left);
        _arc?.Place(1 - Math.Clamp(left / _length, 0, 1));
    }

    /// <summary>For the pipe.</summary>
    public string Describe() => $"snoozed {_countdown?.Text ?? "-"}";

    static double Measure(FrameworkElement e)
    {
        e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return e.DesiredSize.Width;
    }
}

/// <summary>
/// A missed alarm (the PC slept through it, or it rang out): the pet beside "MISSED", then the alarm's sun (or moon)
/// faded on its horizon and the time it should have rung, in the sky's light.
/// </summary>
internal sealed class MissedPill(Alarm alarm) : ICompactContent
{
    public double Build(ContentControl left, ContentControl right)
    {
        var look = AlarmSkyLook.For(alarm);
        var l = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        l.Children.Add(new PetFace(0.62, FaceMood.Idle) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(-4, 0, 6, 0) });
        l.Children.Add(ScreenKit.Label("Missed", size: 10.5));
        left.Content = l;
        var mark = look.Mark(10, 0.6);
        mark.Margin = new Thickness(0, 0, 8, 0);
        var r = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        r.Children.Add(mark);
        r.Children.Add(ScreenKit.Display(AlarmText.Time(alarm), 14, Weight.Bold, 0, Ui.Tokens.Frozen(look.Glow)));
        right.Content = r;
        l.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        r.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Ceiling(Math.Max(l.DesiredSize.Width, r.DesiredSize.Width)) + 2;
    }

    public string Describe() => $"missed {AlarmText.Time(alarm)}";
}

/// <summary>A small dotted arc over a horizon with the alarm's sun (or moon) on it, placed from 0 (rising) to 1 (overhead and down).</summary>
internal sealed class SunArc : Canvas
{
    const double W = 60, H = 18, Base = 15, Mark = 8;
    readonly FrameworkElement _mark;

    public SunArc(AlarmSkyLook look)
    {
        Width = W;
        Height = H;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
        var faint = Ui.Tokens.Frozen(AlarmSkyLook.Alpha(look.Glow, 0.4));
        Children.Add(new Path { Data = Geometry.Parse(FormattableString.Invariant($"M3,{Base} Q{W / 2},-5 {W - 3},{Base}")), Stroke = faint, StrokeThickness = 1, StrokeDashArray = { 1.5, 2.5 } });
        Children.Add(new Line { X1 = 0, X2 = W, Y1 = Base + 0.5, Y2 = Base + 0.5, Stroke = faint, StrokeThickness = 1 });
        _mark = look.Mark(Mark);
        Children.Add(_mark);
        Place(0);
    }

    /// <summary>Moves the mark along the arc (a quadratic curve) to <paramref name="t"/> of the way.</summary>
    public void Place(double t)
    {
        double u = 1 - t;
        double x = u * u * 3 + 2 * u * t * (W / 2) + t * t * (W - 3);
        double y = u * u * Base + 2 * u * t * -5 + t * t * Base;
        SetLeft(_mark, x - Mark / 2);
        SetTop(_mark, y - Mark / 2);
    }
}

/// <summary>
/// After Stop: the pet stretches and waves beside the time of day ("MORNING") with the temperature (or the
/// time when there's no weather), for a few seconds.
/// </summary>
internal sealed class StoppedPill(string greeting, string value) : ICompactContent
{
    public double Build(ContentControl left, ContentControl right)
    {
        var face = new PetFace(0.62, FaceMood.Stretch) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(-4, 0, 6, 0) };
        var l = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        l.Children.Add(face);
        l.Children.Add(ScreenKit.Label(greeting, size: 10.5));
        left.Content = l;
        var r = ScreenKit.Display(value, 14, Weight.Bold, 0);
        right.Content = r;
        l.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        r.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Ceiling(Math.Max(l.DesiredSize.Width, r.DesiredSize.Width)) + 2;
    }

    public string Describe() => $"stopped {greeting} {value}";
}
