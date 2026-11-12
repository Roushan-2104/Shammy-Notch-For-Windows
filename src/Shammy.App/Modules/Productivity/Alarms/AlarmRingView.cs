using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Shammy.Core.Productivity;
using Shammy.Views.Screens;

namespace Shammy.Modules.Productivity.Alarms;

/// <summary>
/// The ringing alarm: the notch drops open to 680×296 (over fullscreen apps too) with the date and how long
/// it's been ringing, the pet hopping under a rattling bell, the time huge, the label, Snooze 5 / 10 / 15 (the
/// usual one marked) and one big Stop, the screen's single accent. An accent ring pulses around the notch while
/// it rings. Shown without taking focus; the buttons work by mouse.
/// </summary>
internal sealed class AlarmRingView : Grid
{
    public const double CardWidth = 680, CardHeight = 296;
    const double Radius = 32;

    readonly Alarm _alarm;
    readonly SpacedText _ringing = ScreenKit.Label("");
    readonly DispatcherTimer _tick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    readonly Border _pulse;
    readonly ScaleTransform _pulseScale = new(1, 1);
    readonly Action<int> _snooze;
    readonly Action _stop;
    bool _done;

    public AlarmRingView(Alarm alarm, int usualSnooze, Action stop, Action<int> snooze)
    {
        _alarm = alarm;
        _stop = stop;
        _snooze = snooze;
        Width = CardWidth;
        Height = CardHeight;
        Background = Brushes.Transparent;
        ClipToBounds = true;

        // The accent ring: a steady 2 px edge, and a second one that pulses inward and fades.
        Children.Add(Ring(1));
        _pulse = Ring(0.6);
        _pulse.RenderTransformOrigin = new Point(0.5, 0);
        _pulse.RenderTransform = _pulseScale;
        Children.Add(_pulse);

        var content = new Grid { Margin = new Thickness(34, 22, 34, 26) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(content);

        // Alarm · Thu 5 Nov ............................................. Ringing · 0:12
        var top = new Grid();
        top.Children.Add(ScreenKit.Label(AlarmText.RingDate(DateTime.Now)));
        _ringing.HorizontalAlignment = HorizontalAlignment.Right;
        _ringing.Tabular = true;
        top.Children.Add(_ringing);
        content.Children.Add(top);

        // The pet under its bell, the time, the label.
        var middle = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        var pet = new Grid { Width = 132, Height = 132 };
        var face = new PetFace(3, FaceMood.Ring) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
        pet.Children.Add(face);
        middle.Children.Add(pet);

        var (time, suffix) = ClockPreference.Parts(alarm.Hour, alarm.Minute);
        var big = ScreenKit.Display(time, 116, Weight.ExtraBold, -0.05);
        // The face's line box is taller than its digits; pull the next line up like the design's line-height .84.
        big.Margin = new Thickness(0, -14, 0, -16);
        var timeRow = new StackPanel { Orientation = Orientation.Horizontal };
        timeRow.Children.Add(big);
        if (suffix.Length > 0)
        {
            var am = ScreenKit.Display(suffix, 34, Weight.Bold, -0.02);
            // On the big number's baseline, like the design's flex baseline row.
            am.VerticalAlignment = VerticalAlignment.Top;
            am.Margin = new Thickness(10, -14 + big.Baseline - am.Baseline, 0, 0);
            timeRow.Children.Add(am);
        }
        var label = ScreenKit.Text(alarm.Label.Length > 0 ? "Alarm · " + alarm.Label : "Alarm", 17, Weight.SemiBold, ScreenKit.Fg2);
        label.MaxWidth = 420;
        var words = ScreenKit.Column(8, timeRow, label);
        words.VerticalAlignment = VerticalAlignment.Center;
        words.Margin = new Thickness(22, 0, 0, 0);
        middle.Children.Add(words);
        SetRow(middle, 1);
        content.Children.Add(middle);

        // Snooze 5 / 10 / 15 (the usual one says so) ........................ [ ■ Stop ]
        var bottom = new Grid();
        var snoozes = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var snoozeLabel = ScreenKit.Label("Snooze");
        snoozeLabel.Margin = new Thickness(0, 0, 14, 0);
        snoozes.Children.Add(snoozeLabel);
        var minutes = new SortedSet<int> { 5, 10, 15 };
        if (!minutes.Contains(usualSnooze)) minutes = new SortedSet<int>(minutes.Take(2).Append(usualSnooze));
        foreach (int m in minutes)
        {
            var button = SnoozeButton(m, m == usualSnooze);
            if (snoozes.Children.Count > 1) button.Margin = new Thickness(8, 0, 0, 0);
            snoozes.Children.Add(button);
        }
        bottom.Children.Add(snoozes);
        var stopButton = StopButton();
        stopButton.HorizontalAlignment = HorizontalAlignment.Right;
        bottom.Children.Add(stopButton);
        SetRow(bottom, 2);
        content.Children.Add(bottom);

        ScreenKit.Rise(top, 350);
        ScreenKit.Rise(middle, 420);
        ScreenKit.Rise(bottom, 500);

        _tick.Tick += (_, _) => UpdateRinging();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                UpdateRinging();
                _tick.Start();
                Pulse(true);
            }
            else
            {
                _tick.Stop();
                Pulse(false);
            }
        };
        Unloaded += (_, _) =>
        {
            _tick.Stop();
            Pulse(false);
        };
        UpdateRinging();
    }

    public Alarm Alarm => _alarm;

    /// <summary>For the pipe: what's on the ringing screen.</summary>
    public string Describe() => $"ring: {AlarmText.Time(_alarm)} '{_alarm.Label}' {_ringing.Text}";

    static Border Ring(double opacity) => new()
    {
        BorderBrush = ScreenKit.Accent,
        BorderThickness = new Thickness(2, 0, 2, 2),
        CornerRadius = new CornerRadius(0, 0, Radius, Radius),
        Opacity = opacity,
        IsHitTestVisible = false,
    };

    void Pulse(bool on)
    {
        _pulse.BeginAnimation(OpacityProperty, null);
        _pulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _pulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        if (!on || ScreenKit.ReducedMotion)
        {
            _pulse.Opacity = 0;
            return;
        }
        var period = new Duration(TimeSpan.FromMilliseconds(1400));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _pulse.BeginAnimation(OpacityProperty, new DoubleAnimation(0.7, 0, period) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
        _pulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.955, period) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
        _pulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.9, period) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
    }

    void UpdateRinging()
    {
        var since = _alarm.RingingSinceUtc ?? DateTime.UtcNow;
        var rang = DateTime.UtcNow - since;
        if (rang < TimeSpan.Zero) rang = TimeSpan.Zero;
        _ringing.Text = $"RINGING · {(int)rang.TotalMinutes}:{rang.Seconds:00}";
    }

    Pressable SnoozeButton(int minutes, bool usual)
    {
        var until = DateTime.Now.AddMinutes(minutes);
        var sub = ScreenKit.Label(usual ? "Usual" : "till " + ClockPreference.Parts(until.Hour, until.Minute).Time, size: 10);
        sub.HorizontalAlignment = HorizontalAlignment.Center;
        var text = ScreenKit.Text($"{minutes} min", 16, Weight.Bold);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(text);
        stack.Children.Add(sub);
        var p = new Pressable(ScreenKit.Tile, ScreenKit.TileHover, ScreenKit.Hairline, ScreenKit.HairlineHover)
        {
            Height = 56,
            MinWidth = 84,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(Ui.Tokens.DetailRadius),
            Child = stack,
            ToolTip = $"Snooze {minutes} minutes, until {ClockPreference.Format(until)}",
        };
        p.Click += () =>
        {
            if (_done) return;
            _done = true;
            _snooze(minutes);
        };
        return p;
    }

    Pressable StopButton()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(3.5), Background = ScreenKit.OnAccent, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(ScreenKit.Display("Stop", 20, Weight.ExtraBold, -0.01, ScreenKit.OnAccent));
        var p = new Pressable(ScreenKit.Accent, ScreenKit.AccentHover)
        {
            Width = 220,
            Height = 56,
            CornerRadius = new CornerRadius(16),
            Child = row,
            PressScale = 0.97,
            ToolTip = "Stop the alarm",
        };
        p.Click += () =>
        {
            if (_done) return;
            _done = true;
            _stop();
        };
        return p;
    }
}
