using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using Shammy.Core.Agents;
using Shammy.Infrastructure;
using Shammy.Interop;
using Shammy.Views.Screens;

namespace Shammy.Modules.Agents;

/// <summary>What the steps wing shows right now; the module builds it, the wing only draws it.</summary>
internal enum WingMode { Hidden, Line, Full, Hint, Done, Permission, Question, EndQuestion }

internal sealed record WingStep(string Text, StepKind Kind, string Tag, int TagColor, bool Failed, bool Running, string SessionId);

internal sealed record WingView
{
    public WingMode Mode { get; init; }
    public string Header { get; init; } = "";
    public string Clock { get; init; } = "";
    public IReadOnlyList<AgentTask> Tasks { get; init; } = Array.Empty<AgentTask>();
    public IReadOnlyList<WingStep> Steps { get; init; } = Array.Empty<WingStep>();
    /// <summary>Line/Hint/Done/Permission/Question/EndQuestion: the small coloured line on top ("Claude asks you · site").</summary>
    public string Label { get; init; } = "";
    /// <summary>The big line: the question, "Run a command?", "Done · 12 steps · 4 min".</summary>
    public string Title { get; init; } = "";
    /// <summary>The command or file (Permission), or the line under Done.</summary>
    public string Detail { get; init; } = "";
    public IReadOnlyList<(string Label, string Description)> Options { get; init; } = Array.Empty<(string, string)>();
    public bool MultiSelect { get; init; }
    public string Progress { get; init; } = "";
    public string SessionId { get; init; } = "";
    /// <summary>Which question this is (the ask and, for several, which of them): a new one starts afresh.</summary>
    public string Key { get; init; } = "";
    public bool CanOpen { get; init; }
    /// <summary>A step just failed: the wing glows briefly (once per failure).</summary>
    public int FailedCount { get; init; }
}

/// <summary>
/// The steps wing: a small slab beside the closed notch, on its left (the calendar keeps the right). It shows Claude
/// Code's task list and its last few steps while you're in another app, slides in to one line when you want, and
/// pops out by itself when Claude needs you (Allow/Deny, or one of its own questions with its choices). Its own
/// topmost window, never activated, except for the moment you type an answer of your own.
/// </summary>
internal sealed class StepsWing : Window
{
    public const double FullWidth = 330, LineMaxWidth = 340, Gap = 8;

    static readonly Brush Slab = Ui.Tokens.Brush("#F20B0B0D"), Edge = Ui.Tokens.Brush("#1FFFFFFF"), Soft = Ui.Tokens.Brush("#12FFFFFF"),
        Amber = Ui.Tokens.Brush("#F2A65A"), Red = Ui.Tokens.Brush("#FF8A8A"), RedText = Ui.Tokens.Brush("#FFB3B3"),
        Green = Ui.Tokens.Brush("#3F8F5F"), Blue = Ui.Tokens.Brush("#7FB8FF"), Dim = Ui.Tokens.Brush("#6B6B73");

    /// <summary>Session tag colours: told apart by lightness as well as hue.</summary>
    static readonly Brush[] TagColors =
    {
        Ui.Tokens.Brush("#9CC7FF"), Ui.Tokens.Brush("#F5B97A"), Ui.Tokens.Brush("#9BE3B4"), Ui.Tokens.Brush("#E7A6E8"),
    };

    readonly Border _slab = new();
    readonly ScaleTransform _pop = new(1, 1);
    readonly DropShadowEffect _glow = new() { Color = Color.FromRgb(0xFF, 0x6B, 0x6B), BlurRadius = 22, ShadowDepth = 0, Opacity = 0 };
    IntPtr _hwnd;
    WingView _view = new();
    int _glowFor;
    bool _typing;
    readonly HashSet<int> _picked = new();

    public StepsWing()
    {
        Title = "Shammy Claude steps";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _slab.Background = Slab;
        _slab.BorderBrush = Edge;
        _slab.BorderThickness = new Thickness(1);
        _slab.CornerRadius = new CornerRadius(18);
        _slab.RenderTransformOrigin = new Point(1, 0);
        _slab.RenderTransform = _pop;
        _slab.Effect = _glow;
        // Room for the glow, so it isn't cut at the window's edge.
        Content = new Border { Padding = new Thickness(12), Child = _slab };

        SizeChanged += (_, _) => Placed?.Invoke();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.SetExStyle(_hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        };
        new WindowInteropHelper(this).EnsureHandle();
    }

    // ---- What the module hears back -------------------------------------------------------------------
    public event Action<bool>? Slide;           // true = slide in to one line
    public event Action<string>? StepClicked;    // session id
    public event Action<bool>? Answered;         // Allow (true) / Deny
    public event Action<IReadOnlyList<string>>? Chosen; // the chosen labels (one, or several for a multiple choice)
    public event Action<string>? Typed;          // an answer of the user's own
    public event Action? OpenWanted;             // take me to the window
    public event Action? Dismissed;              // "Later" on an end-of-reply question
    /// <summary>The wing's size changed: the module places it again beside the island.</summary>
    public event Action? Placed;

    /// <summary>The margin around the slab (DIPs) that holds the glow; the module places the slab, not the window.</summary>
    public const double GlowRoom = 12;

    public bool IsTyping => _typing;

    public WingView View => _view;

    public void SetExcluded(bool exclude)
    {
        if (_hwnd != IntPtr.Zero) Native.SetWindowDisplayAffinity(_hwnd, exclude ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE);
    }

    /// <summary>Moves the window in device pixels.</summary>
    public void PlaceAt(int left, int top)
    {
        if (_hwnd != IntPtr.Zero) Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, left, top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>Draws <paramref name="view"/>. A question that wasn't up before pops out.</summary>
    public void Show(WingView view)
    {
        var before = _view;
        _view = view;
        if (view.Mode == WingMode.Hidden)
        {
            StopTyping();
            if (IsVisible) Hide();
            return;
        }
        bool asking = view.Mode is WingMode.Permission or WingMode.Question or WingMode.EndQuestion;
        bool moved = before.Mode != view.Mode || before.Key != view.Key;
        bool newQuestion = asking && (moved || !IsVisible);
        // Another question, or no question any more (answered in the terminal, timed out): typing and picks end.
        if (moved)
        {
            StopTyping();
            _picked.Clear();
        }
        // Rebuilt only when what's drawn changed: the clock alone ticks in place, so hovers and clicks aren't lost and
        // the running step keeps pulsing. While you type your own answer nothing redraws under you.
        string signature = Signature(view);
        if (!_typing && signature != _signature)
        {
            _signature = signature;
            Build();
        }
        else if (_clock is not null) _clock.Text = view.Clock;
        if (!IsVisible)
        {
            // Laid out and placed before it's seen, so it never flashes at the wrong spot.
            Opacity = 0;
            base.Show();
            UpdateLayout();
            Placed?.Invoke();
            Opacity = 1;
        }
        if (newQuestion) Pop();
        if (view.FailedCount > _glowFor)
        {
            _glowFor = view.FailedCount;
            Glow();
        }
    }

    string _signature = "";
    TextBlock? _clock;

    /// <summary>Everything drawn except the clock.</summary>
    static string Signature(WingView v) => string.Join("\u001f",
        v.Mode, v.Key, v.Header, v.Label, v.Title, v.Detail, v.Progress, v.CanOpen, v.MultiSelect, v.SessionId,
        string.Join("\u001e", v.Tasks.Select(t => t.Mark + t.Text)),
        string.Join("\u001e", v.Steps.Select(s => $"{s.Text}{s.Failed}{s.Running}{s.Tag}{s.TagColor}")),
        string.Join("\u001e", v.Options.Select(o => o.Label + o.Description)));

    void Build()
    {
        _clock = null;
        _slab.Width = _view.Mode == WingMode.Line ? double.NaN : FullWidth;
        _slab.MaxWidth = LineMaxWidth;
        _slab.CornerRadius = new CornerRadius(_view.Mode == WingMode.Line ? 17 : 18);
        _slab.Padding = _view.Mode == WingMode.Line ? new Thickness(14, 0, 4, 0) : new Thickness(16, 12, 14, 12);
        _slab.Child = _view.Mode switch
        {
            WingMode.Line => LineRow(),
            WingMode.Hint => HintRow(),
            WingMode.Done => DoneRow(),
            WingMode.Permission => PermissionBody(),
            WingMode.Question => QuestionBody(),
            WingMode.EndQuestion => EndQuestionBody(),
            _ => FullBody(),
        };
    }

    // ---- Working: the task list on top, the last few steps below ---------------------------------------

    UIElement FullBody()
    {
        var stack = new StackPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Text(_view.Header, 12, Weight.SemiBold, ScreenKit.Fg));
        var clock = _clock = Text(_view.Clock, 12, Weight.Regular, ScreenKit.Fg2);
        clock.Margin = new Thickness(8, 0, 8, 0);
        Grid.SetColumn(clock, 1);
        head.Children.Add(clock);
        var slide = SlideButton(slideIn: true);
        Grid.SetColumn(slide, 2);
        head.Children.Add(slide);
        stack.Children.Add(head);

        if (_view.Tasks.Count > 0)
        {
            var tasks = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var t in _view.Tasks)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                row.Children.Add(TaskIcon(t.Mark));
                var words = Text(t.Text, 13, Weight.Regular, t.Mark == TaskMark.Now ? ScreenKit.Fg : ScreenKit.Fg2);
                words.Margin = new Thickness(8, 0, 0, 0);
                words.MaxWidth = FullWidth - 60;
                row.Children.Add(words);
                tasks.Children.Add(row);
            }
            stack.Children.Add(tasks);
            stack.Children.Add(new Border { Height = 1, Background = Soft, Margin = new Thickness(0, 8, 0, 4) });
        }

        double[] fade = { 1, 0.78, 0.55, 0.36, 0.25 };
        for (int i = 0; i < _view.Steps.Count; i++)
            stack.Children.Add(StepRow(_view.Steps[i], fade[Math.Min(i, fade.Length - 1)]));
        if (_view.Steps.Count == 0) stack.Children.Add(Margin8(Text("Getting started…", 13, Weight.Regular, ScreenKit.Fg2)));
        return stack;
    }

    UIElement StepRow(WingStep s, double opacity)
    {
        var grid = new Grid { Opacity = opacity };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(7), Background = Soft,
            Child = StepIcon(s.Kind, s.Running),
        });
        var words = Text(s.Text, 13, Weight.Regular, s.Failed ? RedText : ScreenKit.Fg);
        words.Margin = new Thickness(9, 0, 6, 0);
        Grid.SetColumn(words, 1);
        grid.Children.Add(words);
        if (s.Failed)
        {
            var failed = Text("failed", 11, Weight.SemiBold, Red);
            failed.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(failed, 2);
            grid.Children.Add(failed);
        }
        var tag = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 2, 7, 2),
            Background = Ui.Tokens.Brush("#14FFFFFF"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Text(s.Tag, 11, Weight.SemiBold, TagColors[s.TagColor % TagColors.Length]),
        };
        Grid.SetColumn(tag, 3);
        grid.Children.Add(tag);
        // A tap takes you to that session's window.
        var press = Tap(grid, () => StepClicked?.Invoke(s.SessionId), 9); press.Padding = new Thickness(6, 4, 6, 4); press.Margin = new Thickness(-6, 0, -6, 0);
        System.Windows.Automation.AutomationProperties.SetName(press, s.Text + " · " + s.Tag);
        return press;
    }

    // ---- Slid in: one line beside the island ----------------------------------------------------------

    UIElement LineRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Height = 36 };
        var s = _view.Steps.FirstOrDefault();
        row.Children.Add(Dot(s?.Failed == true ? Red : Blue));
        var words = Text(s?.Text ?? _view.Header, 13, Weight.Regular, s?.Failed == true ? RedText : ScreenKit.Fg);
        words.Margin = new Thickness(8, 0, 8, 0);
        words.MaxWidth = LineMaxWidth - 110;
        row.Children.Add(words);
        if (s is not null)
        {
            var tag = Text(s.Tag, 11, Weight.SemiBold, TagColors[s.TagColor % TagColors.Length]);
            tag.Margin = new Thickness(0, 0, 6, 0);
            row.Children.Add(tag);
        }
        row.Children.Add(SlideButton(slideIn: false));
        return row;
    }

    UIElement HintRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Dot(Blue));
        var words = Text(_view.Title, 13, Weight.SemiBold, ScreenKit.Fg);
        words.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(words);
        return row;
    }

    UIElement DoneRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Ellipse { Width = 20, Height = 20, Fill = _view.Label == "error" ? Red : Green, VerticalAlignment = VerticalAlignment.Center });
        var words = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        words.Children.Add(Text(_view.Title, 14, Weight.SemiBold, ScreenKit.Fg));
        if (_view.Detail.Length > 0) words.Children.Add(Text(_view.Detail, 12, Weight.Regular, ScreenKit.Fg2));
        row.Children.Add(words);
        if (!_view.CanOpen) return row;
        return Tap(row, () => OpenWanted?.Invoke(), 10);
    }

    // ---- Questions: they pop out ----------------------------------------------------------------------

    UIElement PermissionBody()
    {
        var stack = new StackPanel();
        stack.Children.Add(Text(_view.Label, 12, Weight.SemiBold, Amber));
        var title = Text(_view.Title, 14, Weight.SemiBold, ScreenKit.Fg);
        title.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(title);
        if (_view.Detail.Length > 0)
        {
            stack.Children.Add(new Border
            {
                Margin = new Thickness(0, 8, 0, 0),
                Padding = new Thickness(10, 7, 10, 7),
                CornerRadius = new CornerRadius(8),
                Background = Soft,
                Child = new TextBlock
                {
                    Text = _view.Detail, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
                    Foreground = ScreenKit.Fg, TextTrimming = TextTrimming.CharacterEllipsis,
                },
            });
        }
        var deny = ScreenKit.Secondary("Deny", () => Answered?.Invoke(false), 32, fontSize: 12.5, background: ScreenKit.Tile);
        deny.Padding = new Thickness(16, 0, 16, 0);
        var allow = ScreenKit.Primary("Allow", () => Answered?.Invoke(true), 32, 12.5, null, 16);
        System.Windows.Automation.AutomationProperties.SetName(deny, "Deny");
        System.Windows.Automation.AutomationProperties.SetName(allow, "Allow");
        var buttons = ScreenKit.Row(8, deny, allow);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(buttons);
        return stack;
    }

    UIElement QuestionBody()
    {
        var stack = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(Text(_view.Label, 12, Weight.SemiBold, Amber));
        if (_view.Progress.Length > 0)
        {
            var p = Text(_view.Progress, 11, Weight.Regular, ScreenKit.Fg2);
            Grid.SetColumn(p, 1);
            top.Children.Add(p);
        }
        stack.Children.Add(top);
        var title = Text(_view.Title, 14, Weight.SemiBold, ScreenKit.Fg);
        title.TextWrapping = TextWrapping.Wrap;
        title.TextTrimming = TextTrimming.None;
        title.Margin = new Thickness(0, 6, 0, 4);
        stack.Children.Add(title);

        if (_typing)
        {
            stack.Children.Add(TypingBox());
            return stack;
        }
        for (int i = 0; i < _view.Options.Count; i++)
        {
            int index = i;
            var (label, description) = _view.Options[i];
            stack.Children.Add(Option(label, description, first: i == 0, picked: _picked.Contains(i), () =>
            {
                if (!_view.MultiSelect)
                {
                    Chosen?.Invoke(new[] { label });
                    return;
                }
                if (!_picked.Remove(index)) _picked.Add(index);
                _signature = "";
                Build();
            }));
        }
        stack.Children.Add(Option("Something else…", "Type your own answer", first: false, picked: false, StartTyping));
        if (_view.MultiSelect)
        {
            var done = ScreenKit.Primary("Send", () =>
            {
                var labels = _picked.OrderBy(i => i).Where(i => i < _view.Options.Count).Select(i => _view.Options[i].Label).ToList();
                if (labels.Count > 0) Chosen?.Invoke(labels);
            }, 32, 12.5, null, 16);
            done.HorizontalAlignment = HorizontalAlignment.Right;
            done.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(done);
        }
        return stack;
    }

    UIElement Option(string label, string description, bool first, bool picked, Action click)
    {
        var words = new StackPanel();
        var l = Text(label, 13, Weight.Medium, ScreenKit.Fg);
        l.TextWrapping = TextWrapping.Wrap;
        l.TextTrimming = TextTrimming.None;
        words.Children.Add(l);
        if (description.Length > 0)
        {
            var d = Text(description, 11, Weight.Regular, ScreenKit.Fg2);
            d.TextWrapping = TextWrapping.Wrap;
            d.TextTrimming = TextTrimming.None;
            words.Children.Add(d);
        }
        var box = new Border
        {
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = picked || first ? Blue : Ui.Tokens.Brush("#1FFFFFFF"),
            Background = picked ? Ui.Tokens.Brush("#267FB8FF") : Ui.Tokens.Brush("#0AFFFFFF"),
            Child = words,
        };
        var press = Tap(box, click, 12);
        System.Windows.Automation.AutomationProperties.SetName(press, label);
        return press;
    }

    TextBox? _box;
    IntPtr _returnTo;

    UIElement TypingBox()
    {
        _box = new TextBox
        {
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 13,
            Foreground = ScreenKit.Fg,
            Background = Soft,
            BorderBrush = Blue,
            CaretBrush = ScreenKit.Fg,
            TextWrapping = TextWrapping.Wrap,
            MaxLength = 1000,
        };
        System.Windows.Automation.AutomationProperties.SetName(_box, "Your answer");
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) SendTyped();
            else if (e.Key == Key.Escape) CancelTyping();
        };
        var cancel = ScreenKit.Secondary("Back", CancelTyping, 32, fontSize: 12.5, background: ScreenKit.Tile);
        cancel.Padding = new Thickness(14, 0, 14, 0);
        var send = ScreenKit.Primary("Send", SendTyped, 32, 12.5, null, 16);
        var buttons = ScreenKit.Row(8, cancel, send);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 8, 0, 0);
        var stack = new StackPanel();
        stack.Children.Add(_box);
        stack.Children.Add(buttons);
        return stack;
    }

    /// <summary>Typing needs the keyboard: only now does the wing take focus, and gives it back after.</summary>
    void StartTyping()
    {
        _typing = true;
        // Remembered, so the keyboard goes back to the app you were in once the answer is sent.
        _returnTo = Native.GetForegroundWindow();
        if (_hwnd != IntPtr.Zero) Native.SetExStyle(_hwnd, 0, Native.WS_EX_NOACTIVATE);
        Build();
        Activate();
        _box?.Focus();
    }

    void SendTyped()
    {
        string text = (_box?.Text ?? "").Trim();
        if (text.Length == 0) return;
        StopTyping();
        Typed?.Invoke(text);
    }

    void CancelTyping()
    {
        StopTyping();
        _signature = Signature(_view);
        Build();
    }

    void StopTyping()
    {
        if (!_typing) return;
        _typing = false;
        _box = null;
        // Rebuilt next time even if nothing else changed (the box is gone).
        _signature = "";
        if (_hwnd != IntPtr.Zero) Native.SetExStyle(_hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        if (_returnTo != IntPtr.Zero && _returnTo != _hwnd && IsActive) Native.SetForegroundWindow(_returnTo);
        _returnTo = IntPtr.Zero;
    }

    UIElement EndQuestionBody()
    {
        var stack = new StackPanel();
        stack.Children.Add(Text(_view.Label, 12, Weight.SemiBold, Amber));
        var title = Text(_view.Title, 14, Weight.SemiBold, ScreenKit.Fg);
        title.TextWrapping = TextWrapping.Wrap;
        title.TextTrimming = TextTrimming.None;
        title.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(title);
        var later = ScreenKit.Secondary("Later", () => Dismissed?.Invoke(), 32, fontSize: 12.5, background: ScreenKit.Tile);
        later.Padding = new Thickness(14, 0, 14, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(later);
        if (_view.CanOpen)
        {
            var open = ScreenKit.Primary("Reply in Claude", () => OpenWanted?.Invoke(), 32, 12.5, null, 16);
            open.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(open);
        }
        stack.Children.Add(buttons);
        return stack;
    }

    // ---- Small parts ----------------------------------------------------------------------------------

    UIElement SlideButton(bool slideIn)
    {
        var arrow = new Path
        {
            Data = Geometry.Parse(slideIn ? "M4.5,2.5 L8,6 L4.5,9.5" : "M7.5,2.5 L4,6 L7.5,9.5"),
            Stroke = ScreenKit.Fg, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = 12, Height = 12,
        };
        var face = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Soft, Child = arrow };
        var press = Tap(face, () => Slide?.Invoke(slideIn), 15); press.VerticalAlignment = VerticalAlignment.Center;
        System.Windows.Automation.AutomationProperties.SetName(press, slideIn ? "Slide the steps in to one line" : "Slide the steps out");
        return press;
    }

    static UIElement TaskIcon(TaskMark mark)
    {
        var g = new Grid { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        switch (mark)
        {
            case TaskMark.Done:
                g.Children.Add(new Ellipse { Fill = Green });
                g.Children.Add(new Path { Data = Geometry.Parse("M4,7.2 L6,9.2 L10,4.8"), Stroke = Brushes.White, StrokeThickness = 1.6 });
                break;
            case TaskMark.Now:
                g.Children.Add(new Ellipse { Stroke = Blue, StrokeThickness = 1.6, Margin = new Thickness(1) });
                g.Children.Add(new Ellipse { Fill = Blue, Width = 5, Height = 5 });
                break;
            default:
                g.Children.Add(new Ellipse { Stroke = Dim, StrokeThickness = 1.4, Margin = new Thickness(1) });
                break;
        }
        return g;
    }

    static UIElement StepIcon(StepKind kind, bool running)
    {
        string data = kind switch
        {
            StepKind.Read => "M3,1.5 L7,1.5 L9,3.5 L9,10.5 L3,10.5 Z",
            StepKind.Edit => "M2,10 L3,7 L8,2 L10,4 L5,9 Z",
            StepKind.Run => "M2,3 L5,6 L2,9 M6.5,9.5 L10,9.5",
            StepKind.Find => "M7.5,7.5 L10,10 M8.2,5 A3.2,3.2 0 1 1 1.8,5 A3.2,3.2 0 1 1 8.2,5",
            StepKind.Web => "M6,1.5 A4.5,4.5 0 1 1 6,10.5 A4.5,4.5 0 1 1 6,1.5 M1.5,6 L10.5,6",
            StepKind.Ask => "M4.4,4.4 A1.7,1.7 0 1 1 7.4,5.5 C6.8,5.9 6.5,6.2 6.5,6.8 M6.5,9 L6.5,9.2",
            StepKind.Helper => "M6,2 L6,10 M2,6 L10,6",
            _ => "M3,3 L9,3 L9,9 L3,9 Z",
        };
        var path = new Path
        {
            Data = Geometry.Parse(data), Stroke = Ui.Tokens.Brush("#D4D4D8"), StrokeThickness = 1.2,
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = 12, Height = 12,
        };
        if (running && MotionPrefs.Lively)
        {
            var pulse = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            path.BeginAnimation(OpacityProperty, pulse);
        }
        return path;
    }

    static Pressable Tap(UIElement child, Action click, double radius)
    {
        var p = new Pressable(Brushes.Transparent, Ui.Tokens.Brush("#0FFFFFFF")) { Child = child, CornerRadius = new CornerRadius(radius) };
        p.Click += click;
        return p;
    }

    static UIElement Dot(Brush fill) => new Ellipse { Width = 7, Height = 7, Fill = fill, VerticalAlignment = VerticalAlignment.Center };

    static TextBlock Text(string text, double size, Weight w, Brush brush)
    {
        var t = ScreenKit.Text(text, size, w, brush);
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        t.VerticalAlignment = VerticalAlignment.Center;
        return t;
    }

    static UIElement Margin8(UIElement e)
    {
        if (e is FrameworkElement f) f.Margin = new Thickness(0, 6, 0, 2);
        return e;
    }

    void Pop()
    {
        if (!MotionPrefs.Lively) return;
        var ease = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
        _pop.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
        _pop.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
    }

    void Glow()
    {
        var a = new DoubleAnimation(0, 0.9, TimeSpan.FromMilliseconds(420)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(3) };
        if (!MotionPrefs.Lively) a = new DoubleAnimation(0.9, 0, TimeSpan.FromMilliseconds(1600));
        _glow.BeginAnimation(DropShadowEffect.OpacityProperty, a);
    }

    /// <summary>For the test pipe: what's on show.</summary>
    public string Describe() => !IsVisible ? "hidden"
        : $"{_view.Mode} steps={_view.Steps.Count} tasks={_view.Tasks.Count} title=\"{_view.Title}\" typing={_typing} picked={_picked.Count} w={ActualWidth:0}";
}
