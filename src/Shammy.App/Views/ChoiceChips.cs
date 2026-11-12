using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace Shammy.Views;

/// <summary>
/// The "Did you mean" row: two or three Shammy actions and Search Google last, as chips numbered 1–4. The first
/// is the accent chip (Enter picks it). With the Cat theme, each chip leads with a paw instead of its glyph.
/// Every chip is a named button for screen readers; nothing here closes by itself.
/// </summary>
internal sealed class ChoiceChips : WrapPanel
{
    public const double RowWidth = 396;
    const double ChipHeight = 32;

    public ChoiceChips(IReadOnlyList<(string Title, string Glyph)> chips, bool cat, Action<int> pick)
    {
        Margin = new Thickness(56, 2, 16, 12);
        Width = RowWidth - 40;
        for (int i = 0; i < chips.Count && i < 4; i++)
        {
            int at = i;
            bool first = i == 0;
            var chip = new Border
            {
                Height = ChipHeight,
                CornerRadius = new CornerRadius(ChipHeight / 2),
                Padding = new Thickness(8, 0, 13, 0),
                Margin = new Thickness(0, 0, 8, 8),
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1),
                Focusable = false,
            };
            chip.SetResourceReference(Border.BackgroundProperty, first ? "WallAccent" : "WallRaised");
            chip.SetResourceReference(Border.BorderBrushProperty, first ? "WallAccent" : "WallHairline");
            string text = first ? "WallOnAccent" : "WallText";

            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var key = WallParts.Text((i + 1).ToString(), 11, FontWeights.SemiBold, text);
            key.Margin = new Thickness(2, 0, 6, 0);
            key.Opacity = 0.75;
            key.SetResourceReference(TextBlock.ForegroundProperty, text);
            row.Children.Add(key);
            if (cat) row.Children.Add(Paw(first ? "WallOnAccent" : i == chips.Count - 1 ? "WallText2" : "PawPink"));
            else
            {
                var glyph = new TextBlock { Text = chips[i].Glyph, FontFamily = Ui.Tokens.FontRes("IconFont"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
                glyph.SetResourceReference(TextBlock.ForegroundProperty, text);
                row.Children.Add(glyph);
            }
            var title = new TextBlock { Text = chips[i].Title, FontSize = 13.5, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 210, TextTrimming = TextTrimming.CharacterEllipsis };
            title.SetResourceReference(TextBlock.FontFamilyProperty, "WallFont");
            title.SetResourceReference(TextBlock.ForegroundProperty, text);
            row.Children.Add(title);
            chip.Child = row;

            AutomationProperties.SetName(chip, $"{i + 1}: {chips[i].Title}");
            chip.ToolTip = $"Press {i + 1}{(first ? " or Enter" : "")}";
            chip.MouseEnter += (_, _) => { if (!first) chip.SetResourceReference(Border.BackgroundProperty, "WallRaisedHover"); };
            chip.MouseLeave += (_, _) => { if (!first) chip.SetResourceReference(Border.BackgroundProperty, "WallRaised"); };
            chip.MouseLeftButtonDown += (_, e) => e.Handled = true;
            chip.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                pick(at);
            };
            Children.Add(chip);
        }
    }

    /// <summary>The height this row needs at its width.</summary>
    public double NeededHeight()
    {
        Measure(new Size(Width + Margin.Left + Margin.Right, double.PositiveInfinity));
        return Math.Ceiling(DesiredSize.Height);
    }

    /// <summary>A paw: one pad and four toe beans, 15 px.</summary>
    static FrameworkElement Paw(string brush)
    {
        var g = new GeometryGroup();
        g.Children.Add(new EllipseGeometry(new Point(12, 15.5), 5.2, 4.4));
        g.Children.Add(new EllipseGeometry(new Point(5.6, 10.4), 2.1, 2.6));
        g.Children.Add(new EllipseGeometry(new Point(9.4, 6.6), 2.1, 2.7));
        g.Children.Add(new EllipseGeometry(new Point(14.6, 6.6), 2.1, 2.7));
        g.Children.Add(new EllipseGeometry(new Point(18.4, 10.4), 2.1, 2.6));
        var path = new Path { Data = g, Width = 15, Height = 15, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        if (brush == "PawPink") path.Fill = Ui.Tokens.Paw;
        else path.SetResourceReference(Shape.FillProperty, brush);
        return path;
    }
}
