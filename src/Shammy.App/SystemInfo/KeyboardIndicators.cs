using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Core.Activities;
using Shammy.Core.Notch;
using Shammy.Interop;
using Shammy.Modules;
using Shammy.Views;

namespace Shammy.SystemInfo;

/// <summary>
/// Short live pills for Caps Lock ("Caps Lock on") and input-language switches ("ENG → हिन्दी"). Driven by
/// the keyboard hook's events for those keys only; nothing polls.
/// </summary>
internal sealed class KeyboardIndicators
{
    /// <summary>Feedback for a key you just pressed: under the volume HUD (90), over notifications (80).</summary>
    public const int Priority = 85;
    const string Id = "keyboard";
    const int VK_CAPITAL = 0x14;
    static readonly TimeSpan Shown = TimeSpan.FromSeconds(3);

    readonly Dispatcher _ui;
    readonly Action<Activity> _post;
    bool? _caps;
    IntPtr _layoutBefore;

    public KeyboardIndicators(GlobalKeys keys, Dispatcher ui, Action<Activity> post)
    {
        _ui = ui;
        _post = post;
        _caps = (Native.GetKeyState(VK_CAPITAL) & 1) != 0;
        keys.CapsLockPressed += () => After(TimeSpan.FromMilliseconds(100), ReadCaps);
        keys.LanguageComboStarted += () => _layoutBefore = ForegroundLayout();
        keys.LanguageComboEnded += () => After(TimeSpan.FromMilliseconds(250), ReadLayout);
    }

    /// <summary>The current keyboard language's short name ("ENG").</summary>
    public static string CurrentLabel() => KeyboardPills.Label(LanguageId(ForegroundLayout()));

    void After(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    /// <summary>Each press flips the state; the read confirms it (or, if it lags, the flip wins).</summary>
    void ReadCaps()
    {
        bool read = (Native.GetKeyState(VK_CAPITAL) & 1) != 0;
        bool on = KeyboardPills.ResolveCaps(read, _caps);
        _caps = on;
        ShowCaps(on);
    }

    void ReadLayout()
    {
        var before = _layoutBefore;
        var after = ForegroundLayout();
        if (before == IntPtr.Zero || after == IntPtr.Zero || LanguageId(before) == LanguageId(after)) return;
        ShowLanguage(KeyboardPills.Label(LanguageId(before)), KeyboardPills.Label(LanguageId(after)));
    }

    public void ShowCaps(bool on) =>
        _post(new Activity(Id, ActivityKind.Custom, Priority, new KeyPill(KeyPill.CapsKey, KeyboardPills.CapsText(on), on), DateTime.UtcNow + Shown));

    public void ShowLanguage(string from, string to) =>
        _post(new Activity(Id, ActivityKind.Custom, Priority, new KeyPill(null, KeyboardPills.SwitchText(from, to), false), DateTime.UtcNow + Shown));

    static IntPtr ForegroundLayout()
    {
        var thread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        return Native.GetKeyboardLayout(thread);
    }

    static int LanguageId(IntPtr layout) => (int)(layout.ToInt64() & 0xFFFF);
}

/// <summary>The pill: a key cap (or the keyboard glyph) on the left, the text on the right.</summary>
internal sealed class KeyPill(string? keyCap, string text, bool lit) : ICompactContent
{
    public const string CapsKey = "A";

    public double Build(ContentControl left, ContentControl right)
    {
        var fg = Ui.Tokens.Res("CardText");
        if (keyCap is not null)
        {
            left.Content = new Border
            {
                Width = 17,
                Height = 17,
                CornerRadius = new CornerRadius(4),
                BorderBrush = fg,
                BorderThickness = new Thickness(1.4),
                Background = lit ? fg : Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = keyCap,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = lit ? Brushes.Black : fg,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
        }
        else
        {
            left.Content = CompactView.Glyph("", 15, fg);
        }
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center,
        };
        right.Content = label;
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Ceiling(Math.Max(label.DesiredSize.Width, 17)) + 2;
    }
}
