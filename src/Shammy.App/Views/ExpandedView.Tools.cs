using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Shammy.Core.Wheel;
using Shammy.Infrastructure;
using Shammy.Modules;
using Shammy.Modules.Wheel;
using Shammy.Views.Cards;

namespace Shammy.Views;

/// <summary>
/// The open island's bottom row: six round tools, the same moment-aware set the wheel shows (call tools in a call,
/// music controls with music, writing tools in an editor, otherwise your own beads). The moment is decided by the
/// Core's <see cref="WheelMoments"/> from the same signals the wheel reads, and each tool runs exactly as its
/// droplet does (<see cref="HostLinks.RunWheelAction"/>). The first carries a thin accent ring; a tool lifts a
/// little under the pointer; Tab or ←/→ walk them and Enter runs one.
/// </summary>
public partial class ExpandedView
{
    const double ToolSize = 46, ToolSlot = 88;

    IReadOnlyList<string> _toolIds = Array.Empty<string>();
    readonly List<Button> _toolButtons = new();
    readonly Dictionary<string, TextBlock> _toolGlyphs = new();
    WheelMoment _toolMoment = WheelMoment.Usual;
    WheelMoment? _forcedToolMoment;
    string _toolWhy = "";
    static (string? Process, string? Title) _lastFront;

    void RefreshTools()
    {
        _toolMoment = DecideToolMoment(out _toolWhy);
        var ids = _toolMoment == WheelMoment.Usual ? Beads() : WheelMoments.Droplets(_toolMoment);
        if (!ids.SequenceEqual(_toolIds)) BuildTools(ids);
        // Play/pause shows what it would do.
        if (_toolGlyphs.TryGetValue("playpause", out var glyph)) glyph.Text = GlyphNow("playpause");
    }

    /// <summary>The user's own six beads, as the wheel arranged them (the defaults while the wheel is off).</summary>
    static IReadOnlyList<string> Beads()
    {
        var slots = WheelLink.Current?.Slots is { Count: > 0 } s ? s
            : WheelSlots.Arrange(null, new Dictionary<string, IReadOnlyList<DateTime>>(), DateTime.Now);
        return slots.Take(WheelGeometry.Beads).Select(x => x.ActionId).ToList();
    }

    string GlyphNow(string id) => id == "playpause" && _s?.Media.IsPlaying == true ? "" : WheelModule.GlyphFor(id);

    void BuildTools(IReadOnlyList<string> ids)
    {
        _toolIds = ids.ToList();
        ToolRow.Children.Clear();
        _toolButtons.Clear();
        _toolGlyphs.Clear();
        for (int i = 0; i < ids.Count; i++)
        {
            string id = ids[i];
            string name = WheelSlots.Find(id)?.Name ?? id;
            var glyph = CardKit.FluentGlyph(GlyphNow(id), 18);
            var button = CardKit.Quiet(name, glyph, () => RunTool(id), ToolSize);
            System.Windows.Automation.AutomationProperties.SetHelpText(button, $"Tool {i + 1} of {ids.Count}");
            var circle = new Grid { Width = ToolSize, Height = ToolSize, HorizontalAlignment = HorizontalAlignment.Center };
            circle.Children.Add(button);
            if (i == 0)
                circle.Children.Add(new Border
                {
                    Margin = new Thickness(-3),
                    CornerRadius = new CornerRadius(ToolSize / 2 + 3),
                    BorderBrush = CardKit.Accent,
                    BorderThickness = new Thickness(1.5),
                    IsHitTestVisible = false,
                });
            var label = CardKit.Text(name, 11.5, FontWeights.Medium, secondary: true);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.MaxWidth = ToolSlot - 6;
            label.Margin = new Thickness(0, 5, 0, 0);
            var slot = new StackPanel { Width = ToolSlot, RenderTransform = new TranslateTransform(), Background = Brushes.Transparent };
            slot.Children.Add(circle);
            slot.Children.Add(label);
            slot.MouseEnter += (_, _) => Lift(slot, -2);
            slot.MouseLeave += (_, _) => Lift(slot, 0);
            ToolRow.Children.Add(slot);
            _toolButtons.Add(button);
            _toolGlyphs[id] = glyph;
        }
    }

    /// <summary>The tool rises a little under the pointer (a quick ease; Calm motion just sets it).</summary>
    static void Lift(FrameworkElement slot, double y)
    {
        var t = (TranslateTransform)slot.RenderTransform;
        if (ReducedMotion)
        {
            t.BeginAnimation(TranslateTransform.YProperty, null);
            t.Y = y;
            return;
        }
        t.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(y, TimeSpan.FromMilliseconds(140)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    void RunTool(string id)
    {
        if (_s is null) return;
        try
        {
            if (!HostLinks.RunWheelAction(id, OpenByClick, _s)) Log.Warn($"The island's {id} tool isn't a wheel action");
        }
        catch (Exception ex)
        {
            Log.Warn($"The island's {id} tool failed", ex);
        }
        if (id is "playpause" or "next" or "previous") Dispatcher.BeginInvoke(() => RefreshHome(), System.Windows.Threading.DispatcherPriority.Background);
    }

    // ---- Keyboard -----------------------------------------------------------------------------------

    int ToolIndexOf(DependencyObject? d)
    {
        for (; d is not null && d != this; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is Button b && _toolButtons.IndexOf(b) is var i and >= 0) return i;
        return -1;
    }

    /// <summary>Focuses tool <paramref name="index"/>, wrapping round the ends (−1 is the last).</summary>
    void FocusTool(int index)
    {
        if (_toolButtons.Count == 0) return;
        int n = _toolButtons.Count;
        _toolButtons[((index % n) + n) % n].Focus();
    }

    // ---- The moment -----------------------------------------------------------------------------------

    /// <summary>The wheel's moment, from the signals the wheel reads: a call app on the mic, the app in front, music playing.</summary>
    WheelMoment DecideToolMoment(out string why)
    {
        if (_forcedToolMoment is { } forced)
        {
            why = "set by home-tool";
            return forced;
        }
        if (WheelLink.Current is { FollowMoment: false })
        {
            why = "following the moment is off";
            return WheelMoment.Usual;
        }
        bool call = QuietState.Describe().Split('+').Contains("call");
        var (process, title) = ForegroundApp();
        bool playing = _s?.Media.IsPlaying == true;
        why = $"call={call} front={process ?? "-"} music={playing}";
        return WheelMoments.Decide(new MomentSignals(call, process, title, playing));
    }

    /// <summary>The app in front; while Shammy itself is in front (the island has the keyboard) the one before it.</summary>
    static (string? Process, string? Title) ForegroundApp()
    {
        try
        {
            var front = Interop.Native.GetForegroundWindow();
            if (front != IntPtr.Zero)
            {
                Interop.Native.GetWindowThreadProcessId(front, out var pid);
                if (pid != 0 && pid != (uint)Environment.ProcessId)
                {
                    using var p = Process.GetProcessById((int)pid);
                    _lastFront = (p.ProcessName, Modules.MeetingSystem.MeetingSystemNative.WindowTitle(front));
                }
            }
        }
        catch (Exception)
        {
            // The app in front closed as we looked, or it runs elevated: keep the last one seen.
        }
        return _lastFront;
    }

    // ---- The test pipe -------------------------------------------------------------------------------

    /// <summary>
    /// "home-tool &lt;n&gt;" presses tool n (1–6) as a click would; "home-tool moment &lt;call|music|writing|usual|auto&gt;"
    /// fixes the row's moment for testing; "home-tool" lists the row.
    /// </summary>
    internal string HomeTool(string arg)
    {
        var a = arg.Trim();
        if (a.StartsWith("moment", StringComparison.OrdinalIgnoreCase))
        {
            var m = a[6..].Trim();
            if (m.Equals("auto", StringComparison.OrdinalIgnoreCase)) _forcedToolMoment = null;
            else if (WheelMoments.Parse(m) is { } forced) _forcedToolMoment = forced;
            else return "error: home-tool moment <call|music|writing|usual|auto>";
            RefreshTools();
            return $"moment={WheelMoments.Name(_toolMoment)} tools=[{string.Join(",", _toolIds)}]";
        }
        if (_openId is not null) GoHome(animate: IsVisible);
        RefreshTools();
        if (a.Length == 0) return $"moment={WheelMoments.Name(_toolMoment)} ({_toolWhy}) tools=[{string.Join(",", _toolIds)}]";
        if (!int.TryParse(a, out var n) || n < 1 || n > _toolButtons.Count)
            return $"error: home-tool <1-{_toolButtons.Count}> (tools=[{string.Join(",", _toolIds)}])";
        _toolButtons[n - 1].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        return $"ran {_toolIds[n - 1]} (tool {n} of {_toolIds.Count}, moment={WheelMoments.Name(_toolMoment)}, runner={(HostLinks.WheelRunner is null ? "fallback" : "wheel")})";
    }
}
