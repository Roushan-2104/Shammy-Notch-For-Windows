using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Shammy.Core.Hover;
using Shammy.Interop;
using Shammy.Views.Cards;

namespace Shammy.Modules.Hover;

/// <summary>
/// The small dark glass bubble that follows the pointer while Left Alt is held: a label in the palette's accent tint
/// over the value (a light number, or bold words). Its own tiny topmost window: click-through, never activated (it can
/// never take focus) and hidden from screen captures, so it never shows up in the strip OCR reads or in a recording.
/// </summary>
internal sealed class HoverBubbleWindow : Window
{
    const long WS_EX_TRANSPARENT = 0x20;

    /// <summary>Where the bubble sits from the pointer (DIPs): to the right of the cursor and below the strip OCR reads.</summary>
    const double OffsetX = 14, OffsetY = 46;

    readonly Border _card;
    readonly StackPanel _stack;
    IntPtr _hwnd;

    public HoverBubbleWindow()
    {
        Title = "Shammy hover bubble";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _stack = new StackPanel { Orientation = Orientation.Vertical };
        _card = new Border
        {
            // Near-black glass with a faint edge, as in the mockup; a soft shadow lifts it off any page.
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x08, 0x08, 0x0C)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14, 9, 14, 10),
            MinWidth = 150,
            MaxWidth = 360,
            Margin = new Thickness(12, 6, 12, 18),
            Child = _stack,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 8, Direction = 270, Opacity = 0.45, Color = Colors.Black },
        };
        Content = _card;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.SetExStyle(_hwnd, WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
            Native.SetWindowDisplayAffinity(_hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
        };
        new WindowInteropHelper(this).EnsureHandle();
    }

    /// <summary>What the bubble says right now (for the test pipe): "Label | Value", or "-" while hidden.</summary>
    public string Describe => IsVisible && _stack.Children.Count > 0 ? _said : "-";

    string _said = "-";

    /// <summary>Shows the bubble's words near the pointer (screen pixels).</summary>
    public void Show(HoverBubble bubble, int x, int y)
    {
        _stack.Children.Clear();
        _stack.Children.Add(CardKit.Label(bubble.Label, Ink.A, 11));
        UIElement value;
        if (bubble.IsNumber) value = Sheet.Num(bubble.Value, 24, Ink.Soft);
        else if (bubble.Kind == HoverKind.Meaning)
        {
            var words = Sheet.Word(bubble.Value, 15, FontWeights.SemiBold);
            words.TextTrimming = TextTrimming.None;
            words.TextWrapping = TextWrapping.Wrap;
            words.LineHeight = 21;
            value = words;
        }
        else
        {
            var words = Sheet.Word(bubble.Value, bubble.Value.Length > 28 ? 15 : 18);
            words.TextTrimming = TextTrimming.None;
            words.TextWrapping = TextWrapping.Wrap;
            value = words;
        }
        ((FrameworkElement)value).Margin = new Thickness(0, 3, 0, 0);
        _stack.Children.Add(value);
        if (bubble.Detail is { Length: > 0 } detail) _stack.Children.Add(Sheet.Meta(detail, 12));
        _said = bubble.Label + " | " + bubble.Value + (bubble.Detail is null ? "" : " | " + bubble.Detail);
        if (!IsVisible) base.Show();
        UpdateLayout();
        Follow(x, y);
    }

    /// <summary>Moves the bubble with the pointer, keeping it on the pointer's screen (above the pointer near the bottom).</summary>
    public void Follow(int x, int y)
    {
        if (_hwnd == IntPtr.Zero || !IsVisible) return;
        var monitor = Native.MonitorFromPoint(new Native.POINT(x, y), Native.MONITOR_DEFAULTTONEAREST);
        var work = Native.MonitorInfo(monitor).rcWork;
        double scale = Native.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
        int w = (int)Math.Ceiling(ActualWidth * scale), h = (int)Math.Ceiling(ActualHeight * scale);
        // The card's own margin leaves room for its shadow; place the card itself, not the margin, by the pointer.
        int marginLeft = (int)Math.Round(_card.Margin.Left * scale), marginTop = (int)Math.Round(_card.Margin.Top * scale);
        int left = x + (int)Math.Round(OffsetX * scale) - marginLeft;
        int top = y + (int)Math.Round(OffsetY * scale) - marginTop;
        if (top + h > work.Bottom) top = y - (int)Math.Round(OffsetY * scale) - h + (int)Math.Round(_card.Margin.Bottom * scale);
        if (left + w > work.Right) left = work.Right - w;
        left = Math.Max(work.Left, left);
        top = Math.Max(work.Top, top);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, left, top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>Gone (Alt let go, or nothing to say here).</summary>
    public void Clear()
    {
        if (IsVisible) Hide();
        _stack.Children.Clear();
        _said = "-";
    }
}
