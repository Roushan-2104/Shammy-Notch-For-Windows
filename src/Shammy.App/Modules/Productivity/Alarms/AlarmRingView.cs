using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Shammy.Core.Productivity;
using Shammy.Views.Screens;

namespace Shammy.Modules.Productivity.Alarms;

/// <summary>
/// The ringing alarm: the notch drops open to 680×296 (over fullscreen apps too) into the sky of the hour it rings
/// (dawn, day, sunset or night, see <see cref="AlarmSky"/>). The sky comes up over the first 30 seconds with its sun
/// (or moon) rising behind the time, which is thin, huge and lit by it. The pet and the label sit above it; a quiet
/// row of Snooze 5 / 10 / 15 (the usual one ringed) and one bright Stop below. Calm motion shows the risen sky
/// straight away. Shown without taking focus; the buttons work by mouse.
/// </summary>
internal sealed class AlarmRingView : Grid
{
    public const double CardWidth = 680, CardHeight = 296;
    const double Radius = 32, SunSize = 150, MoonSize = 96, Sink = 130;

    readonly Alarm _alarm;
    readonly AlarmSkyLook _look;
    readonly SpacedText _ringing;
    readonly DispatcherTimer _tick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    readonly Grid _sky = new() { IsHitTestVisible = false };
    readonly TranslateTransform _rise = new();
    readonly Action<int> _snooze;
    readonly Action _stop;
    bool _done;

    public AlarmRingView(Alarm alarm, int usualSnooze, Action stop, Action<int> snooze)
    {
        _alarm = alarm;
        _stop = stop;
        _snooze = snooze;
        _look = AlarmSkyLook.For(alarm);
        Width = CardWidth;
        Height = CardHeight;
        Background = Brushes.Transparent;
        ClipToBounds = true;
        var label = Ui.Tokens.Frozen(_look.Label);
        _ringing = ScreenKit.Label("", label);

        Children.Add(_sky);
        _sky.Children.Add(new Border { Background = _look.Background, CornerRadius = new CornerRadius(0, 0, Radius, Radius) });
        _sky.Children.Add(Art());

        var content = new Grid { Margin = new Thickness(30, 22, 30, 24) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(content);

        // (pet) Wake up · Sun 15 Nov ......................................... Ringing · 0:12
        var top = new Grid();
        var title = ScreenKit.Label(AlarmText.RingDate(DateTime.Now, alarm.Label), label);
        title.MaxWidth = 440;
        top.Children.Add(ScreenKit.Row(10, new PetFace(0.62, FaceMood.Ring) { VerticalAlignment = VerticalAlignment.Center }, title));
        _ringing.HorizontalAlignment = HorizontalAlignment.Right;
        _ringing.Tabular = true;
        top.Children.Add(_ringing);
        content.Children.Add(top);

        // The time, thin and huge, lit from below; AM/PM small on its baseline.
        var (time, suffix) = ClockPreference.Parts(alarm.Hour, alarm.Minute);
        var big = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.Light,
            FontSize = 132,
            Foreground = _look.Lit,
            VerticalAlignment = VerticalAlignment.Center,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = 150,
            Margin = new Thickness(-6, 0, 0, 0),
            IsHitTestVisible = false,
        };
        big.Inlines.Add(new Run(time));
        if (suffix.Length > 0) big.Inlines.Add(new Run(" " + suffix) { FontSize = 30, Foreground = label });
        SetRow(big, 1);
        content.Children.Add(big);

        // Snooze 5 / 10 / 15 (the usual one ringed) ...................................... [ Stop ]
        var bottom = new Grid();
        var snoozes = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var minutes = new SortedSet<int> { 5, 10, 15 };
        if (!minutes.Contains(usualSnooze)) minutes = new SortedSet<int>(minutes.Take(2).Append(usualSnooze));
        foreach (int m in minutes)
        {
            var button = SnoozeButton(m, m == usualSnooze, label);
            if (snoozes.Children.Count > 0) button.Margin = new Thickness(8, 0, 0, 0);
            snoozes.Children.Add(button);
        }
        bottom.Children.Add(snoozes);
        var stopButton = StopButton();
        stopButton.HorizontalAlignment = HorizontalAlignment.Right;
        bottom.Children.Add(stopButton);
        SetRow(bottom, 2);
        content.Children.Add(bottom);

        ScreenKit.Rise(top, 350);
        ScreenKit.Rise(big, 420);
        ScreenKit.Rise(bottom, 500);

        _tick.Tick += (_, _) => UpdateRinging();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                UpdateRinging();
                _tick.Start();
                Dawn();
            }
            else _tick.Stop();
        };
        Unloaded += (_, _) => _tick.Stop();
        UpdateRinging();
    }

    public Alarm Alarm => _alarm;

    /// <summary>For the pipe: what's on the ringing screen.</summary>
    public string Describe() => $"ring: {AlarmText.Time(_alarm)} '{_alarm.Label}' {_ringing.Text} sky={_look.Sky}";

    /// <summary>The sun or moon with its halo, the night's stars, and the horizon line, clipped to the card's round foot.</summary>
    Canvas Art()
    {
        var art = new Canvas
        {
            Width = CardWidth,
            Height = CardHeight,
            // Taller than the card so only its bottom corners round.
            Clip = new RectangleGeometry(new Rect(0, -Radius, CardWidth, CardHeight + Radius), Radius, Radius),
        };
        var lights = new Canvas { RenderTransform = _rise };
        double size = _look.Moon ? MoonSize : SunSize;
        double top = _look.Moon ? 60 : CardHeight - _look.SunBottom - size;
        var halo = AlarmSkyLook.Halo(_look.Glow, size * 3.2, _look.Moon ? 0.28 : 0.5);
        Canvas.SetLeft(halo, _look.SunX - halo.Width / 2);
        Canvas.SetTop(halo, top + size / 2 - halo.Height / 2);
        lights.Children.Add(halo);
        var body = _look.Body(size);
        Canvas.SetLeft(body, _look.SunX - size / 2);
        Canvas.SetTop(body, top);
        lights.Children.Add(body);
        if (_look.Moon) AlarmSkyLook.Stars(art, CardWidth);
        art.Children.Add(lights);

        var horizon = new Rectangle
        {
            Width = CardWidth,
            Height = 1,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0),
                GradientStops = { new(AlarmSkyLook.Alpha(_look.Glow, 0), 0), new(AlarmSkyLook.Alpha(_look.Glow, 0.55), 0.5), new(AlarmSkyLook.Alpha(_look.Glow, 0), 1) },
            },
        };
        Canvas.SetTop(horizon, CardHeight - 84);
        art.Children.Add(horizon);
        return art;
    }

    /// <summary>Brings the sky up over its first 30 seconds of ringing (picking up where a restart left it); Calm shows it risen.</summary>
    void Dawn()
    {
        _sky.BeginAnimation(OpacityProperty, null);
        _rise.BeginAnimation(TranslateTransform.YProperty, null);
        var rang = DateTime.UtcNow - (_alarm.RingingSinceUtc ?? DateTime.UtcNow);
        double k = AlarmSky.Risen(rang);
        if (k >= 1 || ScreenKit.ReducedMotion)
        {
            _sky.Opacity = 1;
            _rise.Y = 0;
            return;
        }
        var left = new Duration(AlarmSky.RiseTime - rang);
        var ease = new SineEase { EasingMode = EasingMode.EaseOut };
        // Back from being covered: start where the eased first run would be by now, so the sky never dims.
        double shown = ease.Ease(k);
        var fade = new DoubleAnimation(0.2 + 0.8 * shown, 1, left) { EasingFunction = ease };
        var lift = new DoubleAnimation(Sink * (1 - shown), 0, left) { EasingFunction = ease };
        // A slow 30 s rise on a software-drawn window: 20 frames a second is plenty.
        Timeline.SetDesiredFrameRate(fade, 20);
        Timeline.SetDesiredFrameRate(lift, 20);
        _sky.BeginAnimation(OpacityProperty, fade);
        _rise.BeginAnimation(TranslateTransform.YProperty, lift);
    }

    void UpdateRinging()
    {
        var since = _alarm.RingingSinceUtc ?? DateTime.UtcNow;
        var rang = DateTime.UtcNow - since;
        if (rang < TimeSpan.Zero) rang = TimeSpan.Zero;
        _ringing.Text = $"RINGING · {(int)rang.TotalMinutes}:{rang.Seconds:00}";
    }

    Pressable SnoozeButton(int minutes, bool usual, Brush label)
    {
        var until = DateTime.Now.AddMinutes(minutes);
        var unit = ScreenKit.Label(usual ? "min · usual" : "min", label, size: 10);
        unit.Margin = new Thickness(6, 1, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(ScreenKit.Display(minutes.ToString(), 15, Weight.Bold, 0));
        row.Children.Add(unit);
        var ring = Ui.Tokens.Frozen(usual ? AlarmSkyLook.Alpha(_look.Glow, 0.65) : AlarmSkyLook.Alpha(Colors.White, 0.1));
        var p = new Pressable(Ui.Tokens.Frozen(AlarmSkyLook.Alpha(Color.FromRgb(6, 8, 14), 0.35)), Ui.Tokens.Frozen(AlarmSkyLook.Alpha(Colors.White, 0.14)),
            ring, Ui.Tokens.Frozen(AlarmSkyLook.Alpha(Colors.White, 0.3)))
        {
            Height = 44,
            Padding = new Thickness(16, 0, 16, 0),
            CornerRadius = new CornerRadius(22),
            Child = row,
            ToolTip = $"Snooze {minutes} minutes, until {ClockPreference.Format(until)}",
        };
        AutomationProperties.SetName(p, (string)p.ToolTip);
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
        var text = ScreenKit.Display("Stop", 18, Weight.ExtraBold, -0.01, Ui.Tokens.Frozen(Color.FromRgb(0x1B, 0x0E, 0x0A)));
        text.HorizontalAlignment = HorizontalAlignment.Center;
        var p = new Pressable(Ui.Tokens.Frozen(_look.Stop), Ui.Tokens.Frozen(_look.Core))
        {
            Width = 190,
            Height = 52,
            CornerRadius = new CornerRadius(26),
            Child = text,
            PressScale = 0.97,
            ToolTip = "Stop the alarm",
        };
        AutomationProperties.SetName(p, "Stop the alarm");
        p.Click += () =>
        {
            if (_done) return;
            _done = true;
            _stop();
        };
        return p;
    }
}
