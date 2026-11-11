using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Core.Activities;
using Shammy.Core.Music;
using Shammy.Core.Notch;
using Shammy.Infrastructure;
using Shammy.Interop;
using Shammy.Modules;
using Shammy.Motion;
using Shammy.Notifications;
using Shammy.Views;

namespace Shammy.Shell;

public partial class NotchWindow
{
    // ---- Typing in the open notch ----------------------------------------------------------------

    /// <summary>
    /// A text box in the open notch wants the keyboard (it was clicked, or its view was opened by a click). The
    /// notch never activates on hover, so take focus now, keep the notch open while typing (the pointer may wander
    /// off), and remember the window that had focus to hand it back afterwards. Esc goes back (ExpandedView).
    /// </summary>
    void BeginDetailTyping(Control input)
    {
        if (_searchMode is not null || _mode != NotchMode.Expanded) return;
        if (!_detailTyping)
        {
            _detailTyping = true;
            _holdOpen++;
            var current = Native.GetForegroundWindow();
            _typingReturnTo = current == _hwnd ? IntPtr.Zero : current;
            if (_interactive) Activate();
            else SetInteractive(true);
        }
        Dispatcher.BeginInvoke(() =>
        {
            if (!_detailTyping || !input.IsVisible) return;
            input.Focus();
            Keyboard.Focus(input);
        }, DispatcherPriority.Input);
    }

    /// <summary>After the view changed: typing is over unless the keyboard is still in a text box in the notch.</summary>
    void CheckDetailTyping()
    {
        if (!_detailTyping) return;
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox
            && Keyboard.FocusedElement is UIElement { IsVisible: true } focused && Expanded.IsAncestorOf(focused)) return;
        EndDetailTyping(giveBack: true);
    }

    /// <summary>Back to never taking focus; with <paramref name="giveBack"/> the app that had focus gets it back.</summary>
    void EndDetailTyping(bool giveBack)
    {
        if (!_detailTyping) return;
        _detailTyping = false;
        _holdOpen--;
        var back = _typingReturnTo;
        _typingReturnTo = IntPtr.Zero;
        if (_searchMode is null)
        {
            SetInteractive(false);
            if (giveBack && back != IntPtr.Zero && Native.GetForegroundWindow() == _hwnd) Native.ForceForeground(back);
        }
        if (!Root.IsMouseOver && _mode == NotchMode.Expanded) _hoverOut.Start();
    }

    internal string DescribeTyping() =>
        $"typing={_detailTyping} interactive={_interactive} focused={(Keyboard.FocusedElement as FrameworkElement)?.GetType().Name ?? "-"} " +
        $"active={(Native.GetForegroundWindow() == _hwnd ? "notch" : "other")} hold={_holdOpen}";

    /// <summary>Test hook: as if the user clicked into the open view's first text box.</summary>
    internal string FocusFirstInputForTest()
    {
        if (_mode != NotchMode.Expanded) return "error: the notch isn't open";
        return Expanded.RequestFirstTextInput() ? "ok" : "error: no text box in the open view";
    }
}
