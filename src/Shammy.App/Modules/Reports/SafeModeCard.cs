using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Core.Reports;
using Shammy.Views;
using Shammy.Views.Screens;

namespace Shammy.Modules.Reports;

/// <summary>
/// Safe mode's note on the start after a second crash in one feature in a day: that feature is paused until the next
/// update, everything else works. Send report (when there's a crash to send), Turn it back on, or Later.
/// </summary>
internal sealed class SafeModeCard : Grid
{
    public const double CardWidth = Ui.Tokens.PopupWidth, CardHeight = 140;

    public SafeModeCard(IReadOnlyList<PausedFeature> paused, Action? sendReport, Action turnOn, Action later)
    {
        Width = CardWidth;
        Height = CardHeight;
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
            CornerRadius = new CornerRadius(Ui.Tokens.DetailRadius),
            Background = ScreenKit.Tile,
            VerticalAlignment = VerticalAlignment.Top,
            ClipToBounds = true,
            Child = new PetFace(0.95, FaceMood.Sleep) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });

        var words = new StackPanel();
        SetColumn(words, 2);
        grid.Children.Add(words);
        words.Children.Add(WallParts.Label("SAFE MODE"));
        var title = WallParts.PopupTitle(Title(paused));
        title.Margin = new Thickness(0, 3, 0, 0);
        words.Children.Add(title);
        var line = WallParts.Text(
            $"{(paused.Count == 1 ? "It" : "They")} closed Shammy twice today, so {(paused.Count == 1 ? "it’s" : "they’re")} off until the next update. Everything else works as usual.",
            12.5, FontWeights.Normal, "WallText2");
        line.TextWrapping = TextWrapping.Wrap;
        line.TextTrimming = TextTrimming.None;
        line.Margin = new Thickness(0, 3, 0, 0);
        words.Children.Add(line);

        var footer = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 10, 0, 0) };
        footer.Children.Add(WallParts.Quiet("Later", later, 30));
        var main = sendReport is not null ? WallParts.Primary("Send report", sendReport, 30) : WallParts.Primary("OK", later, 30);
        main.MinWidth = 76;
        DockPanel.SetDock(main, Dock.Right);
        var on = WallParts.Quiet("Turn it back on", turnOn, 30);
        on.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(on, Dock.Right);
        footer.Children.Add(main);
        footer.Children.Add(on);
        words.Children.Add(footer);

        AutomationProperties.SetName(this, $"Safe mode. {Title(paused)}. {line.Text}");
    }

    /// <summary>"Dictation is paused", "Dictation and Music are paused".</summary>
    internal static string Title(IReadOnlyList<PausedFeature> paused) => paused.Count switch
    {
        0 => "Nothing is paused",
        1 => $"{paused[0].Name} is paused",
        _ => $"{string.Join(", ", paused.Take(paused.Count - 1).Select(p => p.Name))} and {paused[^1].Name} are paused",
    };
}
