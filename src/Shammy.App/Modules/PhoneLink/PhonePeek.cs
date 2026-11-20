using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Views.Cards;
using Shammy.Views.Screens;

namespace Shammy.Modules.PhoneLink;

/// <summary>
/// The small card that drops from the notch for the phone (a ringing call, a file that arrived), shaped like Claude
/// Code's approval card: an icon tile, a kicker, one title, one quiet line, and two buttons, the right one the accent.
/// It never takes focus; the buttons work by mouse.
/// </summary>
internal sealed class PhonePeek : Grid
{
    public const double CardWidth = Ui.Tokens.PopupWidth, CardHeight = 128;

    public PhonePeek(string icon, string kicker, string title, string line, (string Text, Action Click) quiet, (string Text, Action Click) primary)
    {
        Width = CardWidth;
        Height = CardHeight;
        Background = Brushes.Transparent;
        Describe = $"{kicker} | {title} | {line}";

        var rows = new Grid { Margin = new Thickness(16, 14, 16, 16) };
        rows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });
        rows.RowDefinitions.Add(new RowDefinition());
        Children.Add(rows);

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        var glyph = CardKit.Icon(icon, ScreenKit.Fg, 22, 2.2);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(new Border
        {
            Width = 56,
            Height = 56,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(Ui.Tokens.DetailRadius),
            Background = ScreenKit.Tile,
            BorderBrush = ScreenKit.Hairline,
            BorderThickness = new Thickness(1),
            Child = glyph,
        });
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(ScreenKit.Label(kicker));
        var big = Views.WallParts.PopupTitle(title);
        big.Margin = new Thickness(0, 3, 0, 0);
        words.Children.Add(big);
        if (line.Length > 0)
        {
            var quietLine = ScreenKit.Text(line, 12.5, Weight.Regular, ScreenKit.Fg2);
            quietLine.TextTrimming = TextTrimming.CharacterEllipsis;
            quietLine.Margin = new Thickness(0, 3, 0, 0);
            words.Children.Add(quietLine);
        }
        SetColumn(words, 1);
        head.Children.Add(words);
        rows.Children.Add(head);

        var second = ScreenKit.Secondary(quiet.Text, quiet.Click, 30, fontSize: 12.5, background: ScreenKit.Tile);
        second.Padding = new Thickness(16, 0, 16, 0);
        var first = ScreenKit.Primary(primary.Text, primary.Click, 30, 12.5, null, 16);
        var buttons = ScreenKit.Row(8, second, first);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.VerticalAlignment = VerticalAlignment.Bottom;
        SetRow(buttons, 1);
        rows.Children.Add(buttons);
        AutomationProperties.SetName(this, line.Length > 0 ? $"{kicker}. {title}. {line}" : $"{kicker}. {title}");
    }

    /// <summary>What the card says, for the test pipe.</summary>
    public string Describe { get; }
}
