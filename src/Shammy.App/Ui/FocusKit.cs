using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Shammy.Ui;

/// <summary>
/// Keyboard reach for the kit's controls. A hover never gives the notch the keyboard; one click on the open notch
/// does (NotchWindow's keyboard mode), and from then on Tab, the arrows, Enter, Space and Esc work. Every kit
/// control is a tab stop that wears the same thin accent ring (WallControls.xaml's WallFocusRing) when the
/// keyboard reaches it, and never when it was clicked.
/// </summary>
internal static class FocusKit
{
    /// <summary>From XAML: a styled button keeps a text box's keyboard when it is clicked (see <see cref="Button{T}"/>).</summary>
    public static readonly DependencyProperty KeepsTextFocusProperty = DependencyProperty.RegisterAttached("KeepsTextFocus", typeof(bool), typeof(FocusKit),
        new PropertyMetadata(false, (d, e) =>
        {
            if (e.NewValue is true && e.OldValue is not true && d is ButtonBase b) KeepTextFocus(b);
        }));

    public static bool GetKeepsTextFocus(DependencyObject d) => (bool)d.GetValue(KeepsTextFocusProperty);

    public static void SetKeepsTextFocus(DependencyObject d, bool value) => d.SetValue(KeepsTextFocusProperty, value);

    /// <summary>The ≈1.5 px accent ring drawn just outside a focused control (a pill or circle shape).</summary>
    public static Style? Ring => Application.Current?.TryFindResource("WallFocusRing") as Style;

    /// <summary>Makes <paramref name="element"/> a tab stop with the accent focus ring.</summary>
    public static T Focusable<T>(T element) where T : FrameworkElement
    {
        element.Focusable = true;
        KeyboardNavigation.SetIsTabStop(element, true);
        element.FocusVisualStyle = Ring;
        return element;
    }

    /// <summary>
    /// A button that is a tab stop but doesn't take the keyboard from a text box when it is clicked (the Notes
    /// box keeps its caret when "Add" is pressed); from the keyboard it focuses as usual.
    /// </summary>
    public static T Button<T>(T button) where T : ButtonBase
    {
        Focusable(button);
        KeepTextFocus(button);
        return button;
    }

    static void KeepTextFocus(ButtonBase button) =>
        button.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (Keyboard.FocusedElement is not (TextBoxBase or PasswordBox) || !button.Focusable) return;
            button.Focusable = false;
            button.Dispatcher.BeginInvoke(() => button.Focusable = true, System.Windows.Threading.DispatcherPriority.Input);
        };

    /// <summary>Enter or Space on a focused <paramref name="element"/> (not a Button: it has its own) runs <paramref name="run"/>.</summary>
    public static void Activates(UIElement element, Action run)
    {
        element.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space) || e.OriginalSource != element) return;
            e.Handled = true;
            run();
        };
    }
}
