using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shammy.Core.Activities;
using Shammy.Infrastructure;
using Shammy.Playback;

namespace Shammy.Views;

/// <summary>
/// The compact notch: content lives in the two "ears" either side of the centre, with the middle kept
/// black like the real cut-out, and the time at the right end. Each activity kind decides its own pill width.
/// </summary>
internal sealed class CompactView : Grid
{
    const double EarPadding = 13;
    const double CenterGap = 120;

    /// <summary>The time at the right end.</summary>
    const double ClockRoom = 76;

    readonly IslandClock _clock = new() { HorizontalAlignment = HorizontalAlignment.Right };

    public IslandClock Clock => _clock;

    readonly ContentControl _left = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
    readonly ContentControl _right = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };

    Activity? _shown;
    HudParts? _hud;
    TextBlock? _rightText;
    Border? _art;
    VisualizerBars? _bars;
    BatteryIcon? _battery;
    ProgressRing? _ring;

    public CompactView()
    {
        Height = 32;
        _left.Margin = new Thickness(EarPadding, 0, 0, 0);
        _right.Margin = new Thickness(0, 0, EarPadding + ClockRoom, 0);
        // The time after a hairline, as on the live pill, so it never reads as part of the activity's words.
        var clockHost = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, EarPadding, 0) };
        clockHost.Children.Add(new System.Windows.Shapes.Rectangle { Width = 1, Height = 16, Fill = Ui.Tokens.Res("WallHairline"), Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center });
        clockHost.Children.Add(_clock);
        Children.Add(_left);
        Children.Add(_right);
        Children.Add(clockHost);
        Background = Brushes.Transparent;
        MouseLeftButtonUp += (_, _) => _shown?.Click?.Invoke();
    }

    public string? ShownId => _shown?.Id;
    public ActivityKind? ShownKind => _shown?.Kind;

    /// <summary>Builds the content for an activity and returns the pill width it wants.</summary>
    public double Show(Activity a, ShammyServices s)
    {
        _shown = a;
        _hud = null;
        _rightText = null;
        _art = null;
        _bars = null;
        _battery = null;
        _ring = null;
        Cursor = a.Click is null ? null : Cursors.Hand;

        double earWidth = a.Kind switch
        {
            ActivityKind.NowPlaying => BuildNowPlaying(s),
            ActivityKind.Hud => BuildHud((HudPayload)a.Payload!),
            ActivityKind.Charging or ActivityKind.LowBattery => BuildBattery(a),
            ActivityKind.Bluetooth => BuildBluetooth((BluetoothPayload)a.Payload!),
            ActivityKind.Downloading => BuildDownloading((DownloadPayload)a.Payload!),
            ActivityKind.DownloadComplete => BuildDownloadDone((DownloadPayload)a.Payload!),
            ActivityKind.Progress => BuildProgress((ProgressPayload)a.Payload!),
            ActivityKind.Custom when a.Payload is Modules.ICompactContent custom => custom.Build(_left, _right),
            _ => BuildMessage((MessagePayload)a.Payload!),
        };
        Width = CenterGap + 2 * (earWidth + EarPadding) + ClockRoom;
        return Width;
    }

    /// <summary>Updates in place when only the payload changed (volume level, bytes downloaded...).</summary>
    public double Update(Activity a, ShammyServices s)
    {
        if (_shown is null || _shown.Kind != a.Kind) return Show(a, s);
        // Module content updates its own elements; only a new payload instance needs a rebuild.
        if (a.Kind == ActivityKind.Custom && !ReferenceEquals(a.Payload, _shown.Payload)) return Show(a, s);
        _shown = a;
        if (a.Kind == ActivityKind.Custom) return Width;
        switch (a.Payload)
        {
            case HudPayload hud when _hud is not null:
                _hud.Set(hud);
                return Width;
            case DownloadPayload d when a.Kind == ActivityKind.Downloading && _rightText is not null:
                _rightText.Text = Shammy.Core.Connect.ConnectText.DownloadProgress(d.Bytes, d.Total);
                return Width;
            case ProgressPayload p when _ring is not null && _rightText is not null:
                _ring.Value = p.Progress;
                _rightText.Text = $"{p.Progress:P0}";
                return Width;
            case BatteryPayload b when _battery is not null && _rightText is not null:
                _battery.Set(b.Percent, b.Charging);
                _rightText.Text = $"{b.Percent}%";
                return Width;
        }
        if (a.Kind == ActivityKind.NowPlaying)
        {
            RefreshMedia(s.Media);
            return Width;
        }
        return Show(a, s);
    }

    public void RefreshMedia(MediaService media)
    {
        if (_shown?.Kind != ActivityKind.NowPlaying) return;
        if (_art is not null) ApplyArt(_art, media, 6);
        if (_bars is not null)
        {
            _bars.Color = media.ArtColor;
            _bars.Active = media.IsPlaying;
        }
    }

    double BuildNowPlaying(ShammyServices s)
    {
        _art = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(6) };
        _bars = new VisualizerBars { BarCount = 4, Height = 14, Source = s.Visualizer.Level, Feed = s.Visualizer };
        _left.Content = _art;
        _right.Content = _bars;
        RefreshMedia(s.Media);
        return 24;
    }

    internal static void ApplyArt(Border frame, MediaService media, double glyphSize)
    {
        if (media.Art is { } art)
        {
            frame.Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill };
            frame.Child = null;
        }
        else
        {
            frame.Background = Ui.Tokens.Res("Fill2");
            frame.Child = Glyph("", glyphSize + 4, Ui.Tokens.Res("Fg2"));
        }
    }

    double BuildHud(HudPayload p)
    {
        // One row across both ears up to the time: the glyph, the level filling the pill, the number.
        _hud = new HudParts { Row = { Width = CenterGap + 2 * HudParts.EarWidth } };
        _left.Content = _hud.Row;
        _right.Content = null;
        _hud.Set(p);
        return HudParts.EarWidth;
    }

    double BuildBattery(Activity a)
    {
        var p = (BatteryPayload)a.Payload!;
        bool low = a.Kind == ActivityKind.LowBattery;
        // The same quiet label as the other pills; the red stays on the number that needs you.
        _left.Content = Label(low ? "LOW BATTERY" : "CHARGING", Ui.Tokens.Res("Fg2"));
        _battery = new BatteryIcon { VerticalAlignment = VerticalAlignment.Center };
        _battery.Set(p.Percent, p.Charging);
        _rightText = Label($"{p.Percent}%", Ui.Tokens.Res(low ? "Red" : "Green"));
        _rightText.Margin = new Thickness(0, 0, 6, 0);
        _right.Content = Row(_rightText, _battery);
        return 78;
    }

    double BuildBluetooth(BluetoothPayload p)
    {
        _left.Content = Glyph("", 15, Ui.Tokens.Res("AccentBrush"));
        // "Airdopes 311 Pro · 80%" once the headset has reported its battery.
        var name = Label(Shammy.Core.Devices.BluetoothBattery.Pill(p.Name, p.Battery), Ui.Tokens.Res("Fg"));
        double width = p.Battery is null ? 120 : 150;
        name.MaxWidth = width;
        var state = Label(p.Connected ? "Connected" : "Disconnected", Ui.Tokens.Res("Fg2"));
        state.FontWeight = FontWeights.Normal;
        state.FontSize = 10.5;
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.HorizontalAlignment = HorizontalAlignment.Right;
        state.HorizontalAlignment = HorizontalAlignment.Right;
        name.FontSize = 11.5;
        stack.Children.Add(name);
        stack.Children.Add(state);
        _right.Content = stack;
        return width;
    }

    double BuildDownloading(DownloadPayload p)
    {
        var spinner = new Spinner { Width = 15, Height = 15, Stroke = Ui.Tokens.Res("AccentBrush") };
        _left.Content = Row(Glyph("", 14, Ui.Tokens.Res("AccentBrush")), new FrameworkElement { Width = 7 }, spinner);
        _rightText = Label(Shammy.Core.Connect.ConnectText.DownloadProgress(p.Bytes, p.Total), Ui.Tokens.Res("Fg"));
        _right.Content = _rightText;
        return 70;
    }

    double BuildDownloadDone(DownloadPayload p)
    {
        _left.Content = Glyph("", 15, Ui.Tokens.Res("Green"));
        var name = Label(p.Name, Ui.Tokens.Res("Fg"));
        name.MaxWidth = 130;
        _right.Content = name;
        return 130;
    }

    double BuildProgress(ProgressPayload p)
    {
        _ring = new ProgressRing { Value = p.Progress };
        _left.Content = Row(Glyph(p.Glyph, 14, Ui.Tokens.Res("Fg")), new FrameworkElement { Width = 8 },
            Label(p.Label, Ui.Tokens.Res("Fg2")));
        _rightText = Label($"{p.Progress:P0}", Ui.Tokens.Res("Fg"));
        _rightText.Margin = new Thickness(0, 0, 6, 0);
        _right.Content = Row(_rightText, _ring);
        return 96;
    }

    double BuildMessage(MessagePayload p)
    {
        var brush = Ui.Tokens.Res(p.IsError ? "Red" : "AccentBrush");
        _left.Content = Glyph(p.Glyph, 15, brush);
        var text = Label(p.Text, Ui.Tokens.Res("Fg"));
        text.MaxWidth = 170;
        _right.Content = text;
        return 170;
    }

    internal static TextBlock Glyph(string glyph, double size, Brush brush) => new()
    {
        Text = glyph,
        FontFamily = Ui.Tokens.FontRes("IconFont"),
        FontSize = size,
        Foreground = brush,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    static TextBlock Label(string text, Brush brush) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = brush,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in children) row.Children.Add(c);
        return row;
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };

    /// <summary>Speaker/sun glyph, a level track that springs to the new value, and the level as a number.</summary>
    sealed class HudParts
    {
        public const double EarWidth = 84;

        readonly Motion.SpringValue _level = new(0, 0.002);
        readonly Border _fill;
        IDisposable? _frames;

        public HudParts()
        {
            Icon = Glyph("", 15, Ui.Tokens.Res("Fg"));
            _fill = new Border { Height = 5, CornerRadius = new CornerRadius(2.5), Background = Ui.Tokens.Res("Fg"), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            Track = new Border
            {
                Height = 5,
                Margin = new Thickness(10, 0, 10, 0),
                CornerRadius = new CornerRadius(2.5),
                Background = Ui.Tokens.Res("Fill2"),
                VerticalAlignment = VerticalAlignment.Center,
                Child = _fill,
            };
            Track.Unloaded += (_, _) => _frames?.Dispose();
            Track.SizeChanged += (_, _) => _fill.Width = Math.Max(0, _level.Value * Track.ActualWidth);
            // Right-aligned in a fixed slot so the track doesn't jump between "9" and "100".
            _value = Label("", Ui.Tokens.Res("Fg"));
            _value.MinWidth = 24;
            _value.TextAlignment = TextAlignment.Right;
            Row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Row.ColumnDefinitions.Add(new ColumnDefinition());
            Row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(Track, 1);
            Grid.SetColumn(_value, 2);
            Row.Children.Add(Icon);
            Row.Children.Add(Track);
            Row.Children.Add(_value);
        }

        readonly TextBlock _value;

        TextBlock Icon { get; }
        Border Track { get; }
        public Grid Row { get; } = new() { VerticalAlignment = VerticalAlignment.Center };

        public void Set(HudPayload p)
        {
            double level = p.Muted ? 0 : p.Level;
            Icon.Text = p.Kind == HudKind.Brightness ? (level < 0.5 ? "" : "")
                : p.Muted || level <= 0 ? ""
                : level < 0.34 ? ""
                : level < 0.67 ? "" : "";
            _fill.Background = _value.Foreground = Ui.Tokens.Res(p.Muted ? "Fg3" : "Fg");
            _value.Text = Math.Round(level * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            if (_fill.Width == 0 && _level.Value == 0) _level.Snap(level);
            _level.AnimateTo(level, 0.3, 0.86);
            _frames ??= Motion.Animator.Subscribe(_ =>
            {
                _fill.Width = Math.Max(0, _level.Value * Track.ActualWidth);
                if (_level.IsSettled)
                {
                    _frames?.Dispose();
                    _frames = null;
                }
            });
        }
    }
}

/// <summary>A small determinate ring for progress in the compact pill.</summary>
internal sealed class ProgressRing : FrameworkElement
{
    double _value;

    public ProgressRing()
    {
        Width = Height = 15;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public double Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, 0, 1);
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double t = 2.2, r = (Width - t) / 2;
        var c = new Point(Width / 2, Height / 2);
        dc.DrawEllipse(null, new Pen(Ui.Tokens.Res("Fill2"), t), c, r, r);
        if (_value <= 0) return;
        if (_value >= 0.999)
        {
            dc.DrawEllipse(null, new Pen(Ui.Tokens.Res("AccentBrush"), t), c, r, r);
            return;
        }
        double a = -Math.PI / 2 + _value * 2 * Math.PI;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X, c.Y - r), false, false);
            ctx.ArcTo(new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a)), new Size(r, r), 0, _value > 0.5, SweepDirection.Clockwise, true, false);
        }
        dc.DrawGeometry(null, new Pen(Ui.Tokens.Res("AccentBrush"), t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
    }
}
