using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Shammy.Core.Productivity;

namespace Shammy.Views;

/// <summary>
/// The time, always on the island: "3:24" with a small "pm" (or "15:24" on a 24-hour clock), and with
/// <see cref="ShowDate"/> the short date after it ("Sun 15 Nov"). It ticks once a minute, on the minute, and only
/// while it's on screen.
/// </summary>
internal sealed class IslandClock : StackPanel
{
    readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _ampm = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 1, 0, 0) };
    readonly TextBlock? _date;
    readonly DispatcherTimer _tick = new(DispatcherPriority.Background);

    /// <summary>What a pill keeps free at its right end for the time and its hairline.</summary>
    public const double Room = 76;

    /// <param name="rule">A hairline before the time, so on a pill it never reads as part of the activity's words.</param>
    public IslandClock(double size = 13, bool showDate = false, bool rule = false)
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        if (rule)
            Children.Add(new System.Windows.Shapes.Rectangle { Width = 1, Height = 16, Fill = Ui.Tokens.Res("WallHairline"), Margin = new Thickness(10, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center });
        _time.FontSize = size;
        _time.SetResourceReference(TextBlock.FontFamilyProperty, "WallFontSemiBold");
        _time.FontWeight = FontWeights.SemiBold;
        _time.Foreground = Ui.Tokens.Res("WallText");
        _ampm.FontSize = Math.Round(size * 0.72);
        _ampm.FontWeight = FontWeights.Medium;
        _ampm.Foreground = Ui.Tokens.Res("WallText2");
        // A running timer's ring, just before its countdown: a faint track and the time left as an amber arc.
        _ring.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = RingTrack, StrokeThickness = 1.6 });
        _ring.Children.Add(_arc);
        Children.Add(_ring);
        Children.Add(_time);
        Children.Add(_ampm);
        if (showDate)
        {
            _date = new TextBlock
            {
                FontSize = size - 2,
                FontWeight = FontWeights.Medium,
                Foreground = Ui.Tokens.Res("WallText2"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 1, 0, 0),
            };
            Children.Add(_date);
        }
        _tick.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Refresh();
            else _tick.Stop();
        };
    }

    public bool ShowDate => _date is not null;

    /// <summary>What it shows ("3:24 pm", "3:24 pm Sun 15 Nov"), for tests and the log.</summary>
    public string Text => (_time.Text + (_ampm.Text.Length > 0 && _ampm.Visibility == Visibility.Visible ? " " + _ampm.Text : "") + (_date is null ? "" : " " + _date.Text)).Trim();

    string? _timer;
    double _left = -1;

    const double RingSize = 12;
    readonly Grid _ring = new() { Width = RingSize, Height = RingSize, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    readonly System.Windows.Shapes.Path _arc = new() { Stroke = TimerInk, StrokeThickness = 1.6, StrokeStartLineCap = System.Windows.Media.PenLineCap.Round, StrokeEndLineCap = System.Windows.Media.PenLineCap.Round };

    /// <summary>
    /// A running timer's countdown ("12:40") in the time's place while a song keeps the island, with a thin ring before
    /// it that empties as the time runs out (<paramref name="left"/>, 1 to 0); null puts the time back. Called once a
    /// second by the pet's refresh, so it never needs its own tick.
    /// </summary>
    public void ShowTimer(string? countdown, double? left = null)
    {
        double l = countdown is null || left is null ? -1 : Math.Round(Math.Clamp(left.Value, 0, 1), 3);
        if (_timer == countdown && _left == l) return;
        if (l != _left)
        {
            _left = l;
            _ring.Visibility = l < 0 ? Visibility.Collapsed : Visibility.Visible;
            if (l >= 0) _arc.Data = Arc(l);
        }
        if (_timer == countdown) return;
        _timer = countdown;
        _time.Foreground = countdown is null ? Ui.Tokens.Res("WallText") : TimerInk;
        Refresh();
    }

    /// <summary>The arc for <paramref name="left"/> of the ring, clockwise from the top.</summary>
    static System.Windows.Media.Geometry Arc(double left)
    {
        double r = RingSize / 2 - 0.8, c = RingSize / 2;
        if (left >= 0.999) return new System.Windows.Media.EllipseGeometry(new Point(c, c), r, r);
        if (left <= 0.001) return System.Windows.Media.Geometry.Empty;
        double a = left * 2 * Math.PI;
        var end = new Point(c + r * Math.Sin(a), c - r * Math.Cos(a));
        var fig = new System.Windows.Media.PathFigure { StartPoint = new Point(c, c - r), IsClosed = false };
        fig.Segments.Add(new System.Windows.Media.ArcSegment(end, new Size(r, r), 0, left > 0.5, System.Windows.Media.SweepDirection.Clockwise, true));
        var g = new System.Windows.Media.PathGeometry { Figures = { fig } };
        g.Freeze();
        return g;
    }

    static readonly System.Windows.Media.Brush TimerInk = Ui.Tokens.Brush("#F5B97A"), RingTrack = Ui.Tokens.Brush("#40F5B97A");

    public void Refresh()
    {
        if (_timer is { } countdown)
        {
            _time.Text = countdown;
            _ampm.Visibility = Visibility.Collapsed;
            if (_date is not null) _date.Text = "";
            _tick.Stop();
            return;
        }
        var now = DateTime.Now;
        var (time, suffix) = ClockPreference.Parts(now.Hour, now.Minute);
        _time.Text = time;
        _ampm.Text = suffix;
        _ampm.Visibility = suffix.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_date is not null) _date.Text = now.ToString("ddd d MMM", CultureInfo.InvariantCulture);
        // The next minute's turn, a hair after it.
        _tick.Interval = TimeSpan.FromMilliseconds(60_050 - now.Second * 1000 - now.Millisecond);
        if (IsVisible) _tick.Start();
    }
}
