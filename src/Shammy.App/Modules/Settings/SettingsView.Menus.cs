using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shammy.Core.Notch;
using Shammy.Core.OnlineVoice;
using Shammy.Core.Productivity;
using Shammy.Core.Search;
using Shammy.Core.Settings;
using Shammy.Core.Updates;
using Shammy.Infrastructure;
using Shammy.Modules.Connect;
using Shammy.Modules.OnlineVoice;
using Shammy.Search;
using Shammy.Views.Screens;
using PetFace = Shammy.Views.Screens.PetFace;

namespace Shammy.Modules.Settings;

internal partial class SettingsView
{
    // ---- Menus -----------------------------------------------------------------------------------

    /// <summary>A menu button (microphone, currency, language) whose list floats over the panel.</summary>
    FrameworkElement Dropdown(IReadOnlyList<string> options, int selected, double width, Action<int> chosen)
    {
        var label = ScreenKit.Text(selected >= 0 && selected < options.Count ? options[selected] : "—", 12, Weight.SemiBold);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(label);
        var chevron = ScreenKit.Icon(ScreenKit.Paths.Chevron, ScreenKit.Fg2, 12, 2.4);
        chevron.Margin = new Thickness(8, 0, 0, 0);
        SetColumn(chevron, 1);
        grid.Children.Add(chevron);
        var button = new Pressable(Views.Cards.Sheet.CapFill, Views.Cards.Sheet.CapHover, Views.Cards.Sheet.GlassEdge, ScreenKit.HairlineStrong)
        {
            Width = width,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(12, 0, 10, 0),
            Child = grid,
            PressScale = 0.99,
        };
        button.Click += () =>
        {
            if (_menuOwner == button)
            {
                CloseMenu();
                return;
            }
            OpenMenu(button, options, selected, i =>
            {
                selected = i;
                label.Text = options[i];
                chosen(i);
            });
        };
        return button;
    }

    FrameworkElement? _menuOwner;

    void OpenMenu(FrameworkElement owner, IReadOnlyList<string> options, int selected, Action<int> chosen)
    {
        CloseMenu();
        _menuOwner = owner;
        var list = new StackPanel();
        for (int i = 0; i < options.Count; i++)
        {
            int at = i;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(ScreenKit.Text(options[i], 12, Weight.Medium));
            if (i == selected)
            {
                var tick = ScreenKit.Icon(ScreenKit.Paths.Check, ScreenKit.Fg, 12, 2.6);
                SetColumn(tick, 1);
                row.Children.Add(tick);
            }
            var opt = new Pressable(Brushes.Transparent, Views.Cards.Sheet.CapFill) { Height = 30, CornerRadius = new CornerRadius(15), Padding = new Thickness(10, 0, 10, 0), Child = row, PressScale = 1 };
            opt.Click += () =>
            {
                CloseMenu();
                chosen(at);
            };
            list.Children.Add(opt);
        }
        var menu = new Border
        {
            Width = owner.ActualWidth,
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(17),
            Background = ScreenKit.TileHover,
            BorderBrush = ScreenKit.HairlineHover,
            BorderThickness = new Thickness(1),
            Child = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Focusable = false },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 14, Direction = 270, Opacity = 0.5, Color = Colors.Black },
        };
        // Below the button when it fits, otherwise above it; long lists scroll.
        var pos = owner.TranslatePoint(new Point(0, 0), this);
        double below = ActualHeight - (pos.Y + owner.ActualHeight + 4), above = pos.Y - 4;
        double want = Math.Min(options.Count * 30 + 10, 200);
        bool down = below >= Math.Min(want, 120) || below >= above;
        menu.MaxHeight = Math.Max(60, Math.Min(want, down ? below : above));
        Canvas.SetLeft(menu, pos.X);
        if (down) Canvas.SetTop(menu, pos.Y + owner.ActualHeight + 4);
        else
        {
            menu.Measure(new Size(owner.ActualWidth, menu.MaxHeight));
            Canvas.SetTop(menu, pos.Y - 4 - menu.DesiredSize.Height);
        }
        _overlay.Children.Add(menu);
        _overlay.Background = Brushes.Transparent;
        ScreenKit.Rise(menu, 0, down ? -4 : 4);
    }

    void CloseMenu()
    {
        if (_menuOwner is null) return;
        _menuOwner = null;
        _overlay.Children.Clear();
        _overlay.Background = null;
    }
}
