using System.Windows;
using System.Windows.Controls;
using Shammy.Views.Cards;
using Shammy.Views.Screens;

namespace Shammy.Modules.Calendar;

/// <summary>
/// The live pill before a call: a calendar glyph on the left, "Standup in 2 min" and a blue Join on the right.
/// The text updates in place each tick; the whole pill also joins on click.
/// </summary>
internal sealed class MeetingPill : ICompactContent
{
    const double MaxText = 150;

    readonly Action _join;
    string _text;
    TextBlock? _label;

    public MeetingPill(string text, Action join)
    {
        _text = text;
        _join = join;
    }

    public double Build(ContentControl left, ContentControl right)
    {
        left.Content = CardKit.FluentGlyph("", 14, CardKit.Hex(NotchPalette.Calendar));

        _label = CardKit.Text(_text, 12, FontWeights.SemiBold);
        _label.MaxWidth = MaxText;
        var join = ScreenKit.Primary("Join", _join, height: 20, fontSize: 11, icon: CardKit.Paths.Video, padding: 11);
        join.ToolTip = "Join the meeting";
        CardKit.Name(join, "Join the meeting");
        join.Margin = new Thickness(8, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_label);
        row.Children.Add(join);
        right.Content = row;

        row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // Later updates ("in 1 min", "now") trim to this width instead of spilling into the cut-out.
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _label.MaxWidth = Math.Clamp(Math.Ceiling(_label.DesiredSize.Width) + 4, 40, MaxText);
        return Math.Ceiling(row.DesiredSize.Width) + 4;
    }

    public UIElement? BuildBubble() => CardKit.FluentGlyph("", 13, CardKit.Hex(NotchPalette.Calendar));

    public void Set(string text)
    {
        _text = text;
        if (_label is not null) _label.Text = text;
    }
}
