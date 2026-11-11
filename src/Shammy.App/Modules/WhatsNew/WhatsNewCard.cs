using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Shammy.Views.Screens;
using PetFace = Shammy.Views.Screens.PetFace;

namespace Shammy.Modules.WhatsNew;

/// <summary>
/// The note after an update, dropped out of the notch (460 wide): the happy pet in its tile, the new version, a short
/// line of joy, what changed, and Got it. It never takes focus from what you're doing.
/// </summary>
internal sealed class WhatsNewCard : Grid
{
    public const double CardWidth = 460;

    /// <summary>The card's height for this many lines (it was designed at 190 for three).</summary>
    public static double HeightFor(int lines) => 190 - 21 * (3 - Math.Clamp(lines, 1, 3));

    /// <param name="seeAll">Opens the guide on everything that is new; null leaves the link out.</param>
    public WhatsNewCard(string version, IReadOnlyList<string> lines, Action gotIt, Action? seeAll = null)
    {
        Width = CardWidth;
        Height = HeightFor(lines.Count);
        Background = Brushes.Transparent;

        var grid = new Grid { Margin = new Thickness(16, 14, 16, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Children.Add(grid);

        grid.Children.Add(new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(14),
            Background = ScreenKit.Tile,
            VerticalAlignment = VerticalAlignment.Top,
            ClipToBounds = true,
            Child = new PetFace(0.95, FaceMood.Happy) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });

        var words = new StackPanel();
        SetColumn(words, 2);
        grid.Children.Add(words);

        words.Children.Add(ScreenKit.Label($"Updated to {version}"));
        var title = ScreenKit.Display("Look what’s new!", 17, Weight.ExtraBold, -0.015);
        title.Margin = new Thickness(0, 2, 0, 0);
        words.Children.Add(title);

        var list = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var line in lines) list.Children.Add(Line(line));
        words.Children.Add(list);

        var got = ScreenKit.Primary("Got it", gotIt, 30, 12.5, null, 16);
        got.HorizontalAlignment = HorizontalAlignment.Right;
        got.VerticalAlignment = VerticalAlignment.Bottom;
        SetColumn(got, 2);
        grid.Children.Add(got);
        if (seeAll is not null)
        {
            // Three lines can't hold a big update: the guide lists the rest, each with its Try it.
            var all = ScreenKit.Secondary("See everything new", seeAll, 30, 8, 12.5);
            all.HorizontalAlignment = HorizontalAlignment.Right;
            all.VerticalAlignment = VerticalAlignment.Bottom;
            all.Margin = new Thickness(0, 0, 88, 0);
            SetColumn(all, 2);
            grid.Children.Add(all);
        }

        System.Windows.Automation.AutomationProperties.SetName(this, $"Updated to {version}. {string.Join(". ", lines)}");
    }

    static FrameworkElement Line(string text)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new Ellipse
        {
            Width = 4,
            Height = 4,
            Fill = Ui.Tokens.Res("WallAccent"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0),
        });
        var words = ScreenKit.Text(text, 12.5, Weight.Regular, ScreenKit.Fg2);
        words.TextTrimming = TextTrimming.CharacterEllipsis;
        SetColumn(words, 1);
        row.Children.Add(words);
        return row;
    }
}
