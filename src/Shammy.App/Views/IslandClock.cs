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
    public string Text => (_time.Text + (_ampm.Text.Length > 0 ? " " + _ampm.Text : "") + (_date is null ? "" : " " + _date.Text)).Trim();

    public void Refresh()
    {
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
