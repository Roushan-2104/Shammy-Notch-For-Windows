using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Core.Connect;
using Shammy.Infrastructure;
using Shammy.Views;

namespace Shammy.Modules.Productivity;

/// <summary>
/// A due reminder as it drops out of the notch, laid out like a notification in the Focus Wall look: a bell on a tile,
/// a tracked "REMINDER" label with the due time, the reminder in bold, Done (the one accent control) and Snooze.
/// </summary>
internal sealed class ReminderCard : Grid
{
    public const double CardWidth = Ui.Tokens.PopupWidth, CardHeight = 96;

    public ReminderCard(ReminderItem reminder, Action done, Action snooze, int snoozeMinutes = 10)
    {
        Reminder = reminder;
        Width = CardWidth;
        Height = CardHeight;
        Background = Brushes.Transparent;

        // While the screen is shared the card only says "Reminder"; it follows a share starting or ending.
        var title = WallParts.PopupTitle(PrivacyText.Reminder(reminder.Title, PrivacyState.Presenting), wrap: true);
        title.MaxHeight = 46;
        Action<bool> privacy = presenting => Dispatcher.BeginInvoke(() =>
        {
            title.Text = PrivacyText.Reminder(reminder.Title, presenting);
            AutomationProperties.SetName(this, "Reminder: " + title.Text);
        });
        Loaded += (_, _) => PrivacyState.Changed += privacy;
        Unloaded += (_, _) => PrivacyState.Changed -= privacy;
        AutomationProperties.SetName(this, "Reminder: " + title.Text);

        Children.Add(Layout(
            WallParts.ArtTile(WallParts.Glyph("", 20), 56),
            "Reminder",
            Shammy.Core.Cards.ClockFormat.Time(reminder.Due, Kit.CardKit.H24),
            title,
            line: null,
            WallParts.Primary("Done", done, 30),
            WallParts.Quiet($"Snooze {snoozeMinutes} min", snooze, 30)));
    }

    public ReminderItem Reminder { get; }

    /// <summary>
    /// The pop-up card layout shared with <see cref="Views.StartupCard"/>: artwork tile, a tracked label with an optional
    /// time on its right, a bold title, an optional grey line, and a primary button over a quiet one.
    /// </summary>
    internal static Grid Layout(UIElement art, string label, string? meta, TextBlock title, TextBlock? line, Button primary, Button secondary)
    {
        var grid = new Grid { Margin = new Thickness(16, 12, 16, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(art);

        var header = new DockPanel { LastChildFill = true };
        if (meta is not null)
        {
            var time = WallParts.Text(meta, 11.5, FontWeights.Normal, "WallText2");
            time.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(time, Dock.Right);
            header.Children.Add(time);
        }
        var kicker = WallParts.Label(label);
        kicker.HorizontalAlignment = HorizontalAlignment.Left;
        header.Children.Add(kicker);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(header);
        title.Margin = new Thickness(0, 3, 0, 0);
        text.Children.Add(title);
        if (line is not null)
        {
            line.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(line);
        }
        SetColumn(text, 2);
        grid.Children.Add(text);

        var buttons = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        primary.MinWidth = secondary.MinWidth = 104;
        buttons.Children.Add(primary);
        secondary.Margin = new Thickness(0, 6, 0, 0);
        buttons.Children.Add(secondary);
        SetColumn(buttons, 3);
        grid.Children.Add(buttons);
        return grid;
    }
}
