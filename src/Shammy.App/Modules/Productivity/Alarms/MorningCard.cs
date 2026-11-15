using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Shammy.Core.Productivity;
using Shammy.Infrastructure;
using Shammy.Views.Screens;

namespace Shammy.Modules.Productivity.Alarms;

/// <summary>
/// After a dawn alarm is stopped (or its missed pill tapped): the date, "Good morning", the first thing on today's
/// calendar and today's reminders, with the temperature on the right, over a faint arc of the day with the sun at
/// now (decorative: not real sunrise times). Shown for a few seconds; a click closes it.
/// </summary>
internal sealed class MorningCard : Grid
{
    public const double CardWidth = 560, CardHeight = 170;
    const double Radius = 30;

    public MorningCard(DateTime now, string? weather, (DateTime Start, string Title)? first, int reminders)
    {
        Width = CardWidth;
        Height = CardHeight;
        ClipToBounds = true;
        var dawn = AlarmSkyLook.For(Sky.Dawn);
        Children.Add(new Border
        {
            CornerRadius = new CornerRadius(0, 0, Radius, Radius),
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops = { new(Color.FromRgb(0x2A, 0x1A, 0x26), 0), new(Color.FromRgb(0x12, 0x0E, 0x15), 0.55), new(Color.FromRgb(0x0A, 0x0A, 0x0D), 1) },
            },
        });
        Children.Add(DayArc(now, dawn));

        var date = now.ToString("dddd d MMMM", CultureInfo.InvariantCulture);
        var meeting = first is { } f
            ? $"{(PrivacyState.Presenting ? "Busy" : f.Title)} · {ClockPreference.Format(f.Start)}, {AlarmText.Until(f.Start - now)}"
            : "Nothing on the calendar";
        var due = reminders switch { 0 => "No reminders today", 1 => "1 reminder today", _ => $"{reminders} reminders today" };
        var temp = AlarmText.Temperature(weather);

        var words = ScreenKit.Column(0,
            ScreenKit.Label(date, Ui.Tokens.Frozen(Color.FromRgb(0xE9, 0xC9, 0xA8))),
            Spaced(ScreenKit.Display("Good morning", 28, Weight.ExtraBold, -0.01), 6),
            Spaced(ScreenKit.Row(18, Detail(meeting), Detail(due)), 10));
        words.Margin = new Thickness(26, 20, 0, 0);
        Children.Add(words);

        if (temp is not null)
        {
            var place = AlarmText.Place(weather);
            var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 26, 0) };
            right.Children.Add(new TextBlock
            {
                Text = temp,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.Light,
                FontSize = 52,
                Foreground = ScreenKit.Fg,
                HorizontalAlignment = HorizontalAlignment.Right,
            });
            if (place.Length > 0)
            {
                var where = ScreenKit.Text(place, 12, Weight.Medium, ScreenKit.Fg2);
                where.HorizontalAlignment = HorizontalAlignment.Right;
                where.MaxWidth = 120;
                right.Children.Add(where);
            }
            Children.Add(right);
        }
        ScreenKit.Rise(words, 200);
    }

    static T Spaced<T>(T e, double top) where T : FrameworkElement
    {
        e.Margin = new Thickness(0, top, 0, 0);
        return e;
    }

    /// <summary>One of the two short lines under "Good morning", kept clear of the temperature on the right.</summary>
    static TextBlock Detail(string text)
    {
        var t = ScreenKit.Text(text, 12.5, Weight.Medium, ScreenKit.Fg2);
        t.MaxWidth = 180;
        return t;
    }

    /// <summary>The day as a dotted arc over a horizon, 6 AM at the left to 6 PM at the right, the sun at now.</summary>
    static Canvas DayArc(DateTime now, AlarmSkyLook dawn)
    {
        const double left = 20, width = 520, ground = 154, rise = 46;
        var art = new Canvas { IsHitTestVisible = false };
        var faint = Ui.Tokens.Frozen(AlarmSkyLook.Alpha(dawn.Glow, 0.18));
        var points = new PointCollection();
        for (int i = 0; i <= 60; i++) points.Add(new Point(left + width * i / 60, ground - rise * Math.Sin(Math.PI * i / 60)));
        art.Children.Add(new Polyline { Points = points, Stroke = faint, StrokeThickness = 1.2, StrokeDashArray = { 1.5, 3.5 } });
        art.Children.Add(new Line { X1 = 0, X2 = CardWidth, Y1 = ground + 0.5, Y2 = ground + 0.5, Stroke = faint, StrokeThickness = 1 });
        double t = Math.Clamp((now.TimeOfDay.TotalHours - 6) / 12, 0.02, 0.98);
        var sun = dawn.Mark(12);
        Canvas.SetLeft(sun, left + width * t - 6);
        Canvas.SetTop(sun, ground - rise * Math.Sin(Math.PI * t) - 6);
        art.Children.Add(sun);
        return art;
    }
}
