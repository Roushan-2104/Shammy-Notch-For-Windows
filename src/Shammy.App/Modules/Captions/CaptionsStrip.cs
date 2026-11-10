using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Shammy.Core.Captions;
using Shammy.Interop;
using Shammy.Views.Cards;

namespace Shammy.Modules.Captions;

/// <summary>
/// The caption strip: a small window of its own under the notch, so captions can run for an hour while the notch
/// stays free for timers, messages and everything else. Near-black, 560 wide, a tracked label, the language, and
/// two lines of text with the newest words bright. It never takes a click or the keyboard (clicks go to whatever
/// is underneath), and it is not hidden from screen capture: captions may be recorded.
/// </summary>
internal sealed class CaptionsStrip : Window
{
    public const double StripWidth = 560;

    /// <summary>The width a line of caption text may take (the strip less its padding and a little slack).</summary>
    public const double LineWidth = StripWidth - 2 * SidePadding - 6;

    const double SidePadding = 20, TextSize = 17, LineHeight = 23, Corner = 22;

    // Room in the window around the strip for its shadow.
    const double ShadowSide = 40, ShadowTop = 6, ShadowBelow = 52;

    const long WS_EX_TRANSPARENT = 0x20;

    readonly TextBlock _upper, _lower, _language;
    readonly Typeface _face;
    IntPtr _hwnd;

    public CaptionsStrip()
    {
        Title = "Shammy captions";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.Height;
        Width = StripWidth + 2 * ShadowSide;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;

        var semibold = CardKit.Face(FontWeights.SemiBold);
        _face = new Typeface(semibold, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        var label = CardKit.Label("Captions · on this PC", CardKit.Ink2);
        label.HorizontalAlignment = HorizontalAlignment.Left;
        label.VerticalAlignment = VerticalAlignment.Center;
        _language = new TextBlock
        {
            FontFamily = CardKit.Font,
            FontSize = 12.5,
            Foreground = CardKit.Ink2,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var top = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        top.Children.Add(label);
        top.Children.Add(_language);

        _upper = Line(semibold);
        _lower = Line(semibold);
        var stack = new StackPanel();
        stack.Children.Add(top);
        stack.Children.Add(_upper);
        stack.Children.Add(_lower);

        // The surface and its shadow are one element and the words another on top, so the shadow's blur never
        // softens the text.
        var surface = new Border
        {
            Background = CardKit.Hex("#EB000000"),
            CornerRadius = new CornerRadius(Corner),
            Effect = new DropShadowEffect { BlurRadius = 34, ShadowDepth = 14, Direction = 270, Opacity = 0.38, Color = Colors.Black },
        };
        var words = new Border { Padding = new Thickness(SidePadding, 10, SidePadding, 14), Child = stack };
        var strip = new Grid { Width = StripWidth, Margin = new Thickness(ShadowSide, ShadowTop, ShadowSide, ShadowBelow) };
        strip.Children.Add(surface);
        strip.Children.Add(words);
        Content = strip;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            // Click-through, never focused, and out of Alt+Tab and the taskbar.
            Native.SetExStyle(_hwnd, WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        };
        new WindowInteropHelper(this).EnsureHandle();
    }

    static TextBlock Line(FontFamily family) => new()
    {
        FontFamily = family,
        FontWeight = FontWeights.SemiBold,
        FontSize = TextSize,
        Foreground = CardKit.Ink2,
        Height = LineHeight,
        LineHeight = LineHeight,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    /// <summary>How wide a piece of caption text is, for fitting words to a line.</summary>
    public double Measure(string text) =>
        new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _face, TextSize, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;

    public string LanguageName
    {
        get => _language.Text;
        set
        {
            if (_language.Text != value) _language.Text = value;
        }
    }

    /// <summary>What the two lines say right now (for the state pipe).</summary>
    public string Shown => $"{Plain(_upper)} | {Plain(_lower)}";

    static string Plain(TextBlock line) => string.Concat(line.Inlines.OfType<Run>().Select(r => r.Text));

    /// <summary>The caption: words from the newest merge bright, the older ones dim.</summary>
    public void SetView(CaptionView view, int newest)
    {
        Fill(_upper, view.Upper, newest);
        Fill(_lower, view.Lower, newest);
    }

    /// <summary>A plain dim line in place of a caption ("Getting the voice model ready…").</summary>
    public void SetMessage(string message)
    {
        _upper.Inlines.Clear();
        _upper.Inlines.Add(new Run(message) { Foreground = CardKit.Ink2 });
        _lower.Inlines.Clear();
    }

    static void Fill(TextBlock line, IReadOnlyList<CaptionWord> words, int newest)
    {
        line.Inlines.Clear();
        // One run per stretch of the same brightness; the space between two stretches goes with the first.
        int from = 0;
        while (from < words.Count)
        {
            bool bright = words[from].Batch == newest;
            int to = from + 1;
            while (to < words.Count && (words[to].Batch == newest) == bright) to++;
            var text = string.Join(' ', words.Skip(from).Take(to - from).Select(w => w.Text));
            if (to < words.Count) text += " ";
            line.Inlines.Add(new Run(text) { Foreground = bright ? CardKit.Ink : CardKit.Ink2 });
            from = to;
        }
    }

    /// <summary>
    /// Centres the strip on a screen, its top edge <paramref name="below"/> (at 100% scale) under the top of the
    /// area the notch hangs from. Positions are in that screen's own pixels, as the notch's are.
    /// </summary>
    public void Place(IntPtr monitor, bool fromScreenEdge, double below)
    {
        if (_hwnd == IntPtr.Zero) return;
        if (Native.GetDpiForMonitor(monitor, 0, out uint dpi, out _) != 0 || dpi == 0) dpi = 96;
        var info = Native.MonitorInfo(monitor);
        var area = fromScreenEdge ? info.rcMonitor : info.rcWork;
        int y = area.Top + (int)Math.Round((below - ShadowTop) * dpi / 96.0);
        // Twice at most: moving onto a screen with another scale changes the window's width in pixels.
        for (int pass = 0; pass < 2; pass++)
        {
            if (!Native.GetWindowRect(_hwnd, out var now)) return;
            int x = area.Left + (area.Width - now.Width) / 2;
            if (now.Left == x && now.Top == y) return;
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    /// <summary>Keeps the strip above a window that has just gone fullscreen under it.</summary>
    public void StayOnTop()
    {
        if (_hwnd != IntPtr.Zero) Native.BringToTopmost(_hwnd);
    }
}
