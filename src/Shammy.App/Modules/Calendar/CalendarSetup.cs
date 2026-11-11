using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Shammy.Modules.Kit;

namespace Shammy.Modules.Calendar;

/// <summary>
/// "Connect Google Calendar": where to find the secret iCal address, a box to paste it, Connect and Cancel.
/// Used as a notch card (from the tray) and inside the calendar detail before anything is connected.
/// </summary>
internal sealed class CalendarSetup : Border
{
    public const double CardWidth = 560, CardHeight = 196;

    readonly CalendarModule _module;
    readonly Action? _close;
    readonly TextBox _box;
    readonly TextBlock _status;
    readonly Border _connect;
    bool _busy;

    public CalendarSetup(CalendarModule module, Action? close, bool compact = false)
    {
        _module = module;
        _close = close;
        Padding = compact ? new Thickness(0) : new Thickness(22, 18, 22, 16);

        var stack = new StackPanel();
        var title = CardKit.Row(10, CardKit.Glyph("", 16, CardKit.Brush(NotchPalette.Calendar)),
            CardKit.Label("Connect Google Calendar", 15, FontWeights.Bold));
        stack.Children.Add(title);

        var help = CardKit.Label("In Google Calendar: Settings › your calendar › Integrate calendar › copy the “Secret address in iCal format”.",
            11.5, FontWeights.Normal, CardKit.Text2);
        help.TextTrimming = TextTrimming.None;
        help.TextWrapping = TextWrapping.Wrap;
        help.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(help);

        _box = new TextBox
        {
            Style = Ui.Tokens.StyleRes("BareTextBox"),
            FontSize = 12.5,
            FontFamily = CardKit.Font,
        };
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) _ = ConnectAsync();
            else if (e.Key == Key.Escape) _close?.Invoke();
        };
        var hint = CardKit.Label("https://calendar.google.com/calendar/ical/…/private-…/basic.ics", 12.5, FontWeights.Normal, CardKit.Brush("#5CFFFFFF"));
        hint.IsHitTestVisible = false;
        _box.TextChanged += (_, _) => hint.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var field = new Border
        {
            Height = 36,
            CornerRadius = new CornerRadius(18),
            Background = CardKit.Raised,
            BorderBrush = CardKit.RaisedStroke,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(0, 12, 0, 0),
            Child = new Grid { Children = { hint, _box } },
        };
        stack.Children.Add(field);

        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = true };
        _connect = CardKit.PillButton("Connect", () => _ = ConnectAsync(), height: 30, background: CardKit.Brush(NotchPalette.Calendar),
            hover: CardKit.Brush("#6AACFF"), foreground: CardKit.Brush("#000000"), fontSize: 13);
        DockPanel.SetDock(_connect, Dock.Right);
        bottom.Children.Add(_connect);
        if (_close is not null)
        {
            var cancel = CardKit.PillButton("Cancel", _close, height: 30, fontSize: 13);
            cancel.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(cancel, Dock.Right);
            bottom.Children.Add(cancel);
        }
        _status = CardKit.Label("Stays on this PC, encrypted. Shammy only reads it.", 11, FontWeights.Normal, CardKit.Text2);
        bottom.Children.Add(_status);
        stack.Children.Add(bottom);

        Child = stack;
    }

    public void FocusBox() => Dispatcher.BeginInvoke(() =>
    {
        _box.Focus();
        Keyboard.Focus(_box);
        // Most people open this with the address already copied.
        try
        {
            if (_box.Text.Length == 0 && Clipboard.ContainsText() && Clipboard.GetText().Contains("calendar.google.com", StringComparison.OrdinalIgnoreCase))
            {
                _box.Text = Clipboard.GetText().Trim();
                _box.SelectAll();
            }
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Clipboard busy; the user pastes by hand.
        }
    }, DispatcherPriority.Input);

    async Task ConnectAsync()
    {
        if (_busy) return;
        _busy = true;
        _status.Foreground = CardKit.Text2;
        _status.Text = "Checking…";
        _connect.Opacity = 0.6;
        var error = await _module.ConnectAsync(_box.Text);
        _busy = false;
        _connect.Opacity = 1;
        if (error is null)
        {
            _status.Text = "Connected";
            _box.Clear();
            _close?.Invoke();
            return;
        }
        _status.Text = error;
        _status.ToolTip = error;
        _status.Foreground = CardKit.Brush(NotchPalette.Alarm);
    }
}
