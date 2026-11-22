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

internal sealed record WingStep(string Text, string Tag, int TagColor, bool Failed, bool Running, string SessionId);

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
    /// <summary>How many sessions are working (a step's project tag shows only when it's more than one).</summary>
    public int Live { get; init; }
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
    public const double FullWidth = 300, MinFullWidth = 210, LineMaxWidth = 320, Gap = 10;

    static readonly Brush Soft = Ui.Tokens.Brush("#12FFFFFF"),
        Amber = Ui.Tokens.Brush("#F2A65A"), Red = Ui.Tokens.Brush("#FF8A8A"), RedText = Ui.Tokens.Brush("#FFB3B3"),
        Green = Ui.Tokens.Brush("#3F8F5F"), Blue = Ui.Tokens.Brush("#7FB8FF"), Dim = Ui.Tokens.Brush("#6B6B73");

    /// <summary>Session tag colours: told apart by lightness as well as hue.</summary>
    static readonly Brush[] TagColors =
    {
        Ui.Tokens.Brush("#9CC7FF"), Ui.Tokens.Brush("#F5B97A"), Ui.Tokens.Brush("#9BE3B4"), Ui.Tokens.Brush("#E7A6E8"),
    };

    readonly Border _slab = new();
    readonly Ellipse _dot = new();
    readonly ScaleTransform _pop = new(1, 1);
    readonly TranslateTransform _slide = new();
    bool _hiding;
    string _lastTop = "";
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
        // A fixed width with the slab held to the right: the window never grows sideways, so changing between the one
        // line and the full wing never makes it jump (it only grows downwards, from its top).
        SizeToContent = SizeToContent.Height;
        Width = LineMaxWidth + 2 * GlowRoom;
        WindowStartupLocation = WindowStartupLocation.Manual;

        // The island's own glass, tinted by the screen like it (live brushes: they follow the island's colour).
        _slab.Background = IslandSurface.Fill;
        _slab.BorderBrush = IslandSurface.Rim;
        _slab.BorderThickness = new Thickness(1);
        _slab.CornerRadius = new CornerRadius(18);
        _slab.HorizontalAlignment = HorizontalAlignment.Right;
        _slab.RenderTransformOrigin = new Point(1, 0);
        _slab.RenderTransform = new TransformGroup { Children = { _pop, _slide } };
        _slab.Effect = _glow;
        // A simple dot in the gap between the wing and the island, level with the island's middle; it comes and goes
        // with the slab.
        _dot.Width = _dot.Height = 5;
        _dot.Fill = IslandSurface.Fill;
        _dot.Stroke = IslandSurface.Rim;
        _dot.StrokeThickness = 1;
        _dot.HorizontalAlignment = HorizontalAlignment.Right;
        _dot.VerticalAlignment = VerticalAlignment.Top;
        _dot.IsHitTestVisible = false;
        _dot.SetBinding(OpacityProperty, new System.Windows.Data.Binding(nameof(Opacity)) { Source = _slab });
        SetDotLevel(18);
        // Room for the glow, so it isn't cut at the window's edge.
        var room = new Grid();
        room.Children.Add(new Border { Padding = new Thickness(GlowRoom), Child = _slab });
        room.Children.Add(_dot);
        Content = room;

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

    /// <summary>Puts the dot level with the middle of the closed island (<paramref name="islandHalf"/> DIPs below its top).</summary>
    public void SetDotLevel(double islandHalf) =>
        _dot.Margin = new Thickness(0, GlowRoom + islandHalf - 2.5, GlowRoom - Gap / 2 - 2.5, 0);

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
            if (IsVisible && !_hiding) Disappear();
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
            // A new look (one line to the full wing, working to done) settles in instead of snapping.
            if (moved && !newQuestion && IsVisible && !_hiding) Morph();
        }
        else if (_clock is not null) _clock.Text = view.Clock;
        if (!IsVisible || _hiding)
        {
            bool fresh = !IsVisible;
            _hiding = false;
            IsHitTestVisible = true;
            if (fresh)
            {
                // Laid out and placed before it's seen, so it never flashes at the wrong spot.
                Opacity = 0;
                base.Show();
                UpdateLayout();
                Placed?.Invoke();
                Opacity = 1;
            }
            Appear();
        }
        else if (newQuestion) Pop();
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
        v.Mode, v.Key, v.Live, v.Header, v.Label, v.Title, v.Detail, v.Progress, v.CanOpen, v.MultiSelect, v.SessionId,
        string.Join("\u001e", v.Tasks.Select(t => t.Mark + t.Text)),
        string.Join("\u001e", v.Steps.Select(s => $"{s.Text}{s.Failed}{s.Running}{s.Tag}{s.TagColor}")),
        string.Join("\u001e", v.Options.Select(o => o.Label + o.Description)));

    /// <summary>The endless motions on show (the turning spark, the breathing node): stopped when the wing is redrawn,
    /// so no clock is left ticking on parts that are gone.</summary>
    static readonly List<Action> Loops = new();

    void Build()
    {
        _clock = null;
        StopLoops();
        // Questions keep a set width (their words wrap); everything else is only as wide as its words.
        bool asking = _view.Mode is WingMode.Permission or WingMode.Question or WingMode.EndQuestion;
        _slab.Width = asking ? FullWidth : double.NaN;
        _slab.MinWidth = _view.Mode == WingMode.Full ? MinFullWidth : 0;
        _slab.MaxWidth = _view.Mode == WingMode.Line ? LineMaxWidth : FullWidth;
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

    /// <summary>A point on the thread: done (a small filled node), now (Claude's clay, glowing), next (hollow), failed (red).</summary>
    enum Node { Done, Now, Next, Failed }

    sealed record ThreadItem(string Text, Node Node, string Sub, WingStep? Step);

    /// <summary>
    /// Working: the spark, where and how long, then the work as a thread. With a task list the thread is the plan (done,
    /// the one underway with its latest step under it, what's next); without one it's the last few steps, oldest first,
    /// the newest at the bottom. Only as wide as its words need.
    /// </summary>
    UIElement FullBody()
    {
        var stack = new StackPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(ClaudeMark(16));
        var header = Text(_view.Header, 13, Weight.SemiBold, ScreenKit.Fg);
        header.Margin = new Thickness(8, 0, 0, 0);
        header.MaxWidth = 150;
        title.Children.Add(header);
        var clock = _clock = Text(_view.Clock, 12, Weight.Regular, ScreenKit.Fg2);
        clock.Margin = new Thickness(7, 1, 10, 0);
        title.Children.Add(clock);
        head.Children.Add(title);
        var slide = SlideButton(slideIn: true);
        Grid.SetColumn(slide, 2);
        head.Children.Add(slide);
        stack.Children.Add(head);

        var items = ThreadItems();
        if (items.Count == 0)
        {
            stack.Children.Add(Margin8(Text("Getting started…", 13, Weight.Regular, ScreenKit.Fg2)));
            return stack;
        }
        stack.Children.Add(Thread(items));
        return stack;
    }

    List<ThreadItem> ThreadItems()
    {
        var latest = _view.Steps.FirstOrDefault();
        var items = new List<ThreadItem>();
        if (_view.Tasks.Count > 0)
        {
            foreach (var t in _view.Tasks)
            {
                var node = t.Mark switch { TaskMark.Done => Node.Done, TaskMark.Now => Node.Now, _ => Node.Next };
                // The latest step sits under the task underway (in red if it failed).
                string sub = node == Node.Now && latest is not null ? latest.Text : "";
                if (node == Node.Now && latest?.Failed == true) node = Node.Failed;
                items.Add(new ThreadItem(t.Text, node, sub, node is Node.Now or Node.Failed ? latest : null));
            }
            return items;
        }
        // Oldest first, so the thread reads downwards and the newest step glows at its end.
        for (int i = _view.Steps.Count - 1; i >= 0; i--)
        {
            var s = _view.Steps[i];
            var node = s.Failed ? Node.Failed : i == 0 && s.Running ? Node.Now : Node.Done;
            items.Add(new ThreadItem(s.Text, node, "", s));
        }
        return items;
    }

    /// <summary>The thread: a thin line down the left with a node per item; the rows beside it.</summary>
    UIElement Thread(List<ThreadItem> items)
    {
        // A tag only says something when more than one session is working.
        bool tags = _view.Live > 1;
        bool newTop = NewTop();
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < items.Count; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var line = new Rectangle { Width = 2, RadiusX = 1, RadiusY = 1, Fill = Thread_, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 11, 0, 11) };
        Grid.SetRowSpan(line, items.Count);
        grid.Children.Add(line);

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var node = NodeMark(it.Node);
            Grid.SetRow(node, i);
            grid.Children.Add(node);

            bool lit = it.Node is Node.Now or Node.Failed;
            var words = new StackPanel { Margin = new Thickness(8, 3, 6, 3), VerticalAlignment = VerticalAlignment.Center };
            var text = Text(it.Text, 13, lit ? Weight.SemiBold : Weight.Regular,
                it.Node == Node.Failed ? RedText : it.Node == Node.Next ? ScreenKit.Fg2 : ScreenKit.Fg);
            if (it.Node == Node.Done) text.Opacity = 0.72;
            words.Children.Add(text);
            if (it.Sub.Length > 0 && it.Sub != it.Text) words.Children.Add(Text(it.Sub, 11.5, Weight.Regular, it.Node == Node.Failed ? Red : ScreenKit.Fg2));
            UIElement row = words;
            if (lit && newTop) Arrive(words);
            if (it.Step is { } s)
            {
                // A tap takes you to that session's window.
                var press = Tap(words, () => StepClicked?.Invoke(s.SessionId), 8);
                System.Windows.Automation.AutomationProperties.SetName(press, it.Text);
                row = press;
            }
            Grid.SetRow(row, i);
            Grid.SetColumn(row, 1);
            grid.Children.Add(row);

            if (tags && it.Step is { } st)
            {
                var tag = Text(st.Tag, 11, Weight.SemiBold, TagColors[st.TagColor % TagColors.Length]);
                Grid.SetRow(tag, i);
                Grid.SetColumn(tag, 2);
                grid.Children.Add(tag);
            }
        }
        return grid;
    }

    static readonly Brush Thread_ = Ui.Tokens.Brush("#24FFFFFF"), DoneNode = Ui.Tokens.Brush("#A3A3AD"), NowHalo = MakeHalo();

    static Brush MakeHalo()
    {
        var b = new RadialGradientBrush(Color.FromArgb(0x80, 0xD9, 0x77, 0x57), Color.FromArgb(0, 0xD9, 0x77, 0x57));
        b.Freeze();
        return b;
    }

    /// <summary>Stops the endless motions on show (the turning spark).</summary>
    static void StopLoops()
    {
        foreach (var stop in Loops) stop();
        Loops.Clear();
    }

    static FrameworkElement NodeMark(Node node)
    {
        if (node != Node.Now)
        {
            return new Ellipse
            {
                Width = 7, Height = 7, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 8.5, 0, 0),
                Fill = node == Node.Done ? DoneNode : node == Node.Failed ? Red : IslandSurface.Fill,
                Stroke = node == Node.Next ? Dim : null, StrokeThickness = 1.4,
            };
        }
        // Underway: a clay node with a soft, still halo (the turning spark is the motion; one is enough).
        var g = new Grid { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0) };
        g.Children.Add(new Ellipse { Width = 16, Height = 16, Fill = NowHalo });
        g.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = Clay });
        return g;
    }

    // ---- Slid in: one line beside the island ----------------------------------------------------------

    UIElement LineRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Height = 36 };
        var s = _view.Steps.FirstOrDefault();
        row.Children.Add(s?.Failed == true ? Dot(Red) : ClaudeMark(18));
        var words = Text(s?.Text ?? _view.Header, 13, Weight.Regular, s?.Failed == true ? RedText : ScreenKit.Fg);
        words.Margin = new Thickness(8, 0, 8, 0);
        words.MaxWidth = LineMaxWidth - 90;
        if (NewTop()) Arrive(words);
        row.Children.Add(words);
        // Which project, only when more than one is working.
        if (s is not null && _view.Live > 1)
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
        row.Children.Add(ClaudeMark(16));
        var words = Text(_view.Title, 13, Weight.SemiBold, ScreenKit.Fg);
        words.Margin = new Thickness(8, 0, 0, 0);
        words.MaxWidth = FullWidth - 60;
        row.Children.Add(words);
        return row;
    }

    UIElement DoneRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Ellipse { Width = 20, Height = 20, Fill = _view.Label == "error" ? Red : Green, VerticalAlignment = VerticalAlignment.Center });
        var words = new StackPanel { Margin = new Thickness(10, 0, 0, 0), MaxWidth = FullWidth - 60 };
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

    static Pressable Tap(UIElement child, Action click, double radius)
    {
        var p = new Pressable(Brushes.Transparent, Ui.Tokens.Brush("#0FFFFFFF")) { Child = child, CornerRadius = new CornerRadius(radius) };
        p.Click += click;
        return p;
    }

    /// <summary>Claude's clay (from Anthropic's press kit).</summary>
    static readonly Brush Clay = Ui.Tokens.Brush("#D97757");

    /// <summary>The Claude Spark, Anthropic's official mark (press kit, "Claude Spark - Clay.svg"), drawn as is.</summary>
    static readonly Geometry ClaudeSpark = Freeze(Geometry.Parse(
        "M18.7657 62.4437L37.1822 52.1167L37.4857 51.2122L37.1822 50.7085H36.2715L33.1852 50.5208L22.6615 50.2391L13.5545 49.8636L4.70044 49.3942L2.47428 48.9248L0.399902 46.1553L0.602281 44.794L2.47428 43.5266L5.15579 43.7613L11.0754 44.1837L19.98 44.794L26.4055 45.1695L35.9679 46.1553H37.4857L37.6881 45.545L37.1822 45.1695L36.7774 44.794L27.5692 38.5508L17.6021 31.9791L12.3908 28.1769L9.60812 26.2524L8.19147 24.4686L7.58433 20.5256L10.1141 17.7091L13.5545 17.9438L14.4146 18.1785L17.9056 20.8542L25.343 26.6279L35.0572 33.7629L36.4739 34.9364L37.0443 34.5514L37.1316 34.2792L36.4739 33.1996L31.212 23.6706L25.596 13.9539L23.0663 9.91695L22.4086 7.52296C22.1538 6.51831 22.0038 5.68714 22.0038 4.65957L24.8877 0.716544L26.5067 0.200195L30.4025 0.716544L32.0215 2.12477L34.4501 7.66379L38.3458 16.3478L44.4172 28.1769L46.188 31.6975L47.1493 34.9364L47.5035 35.9222H48.1106V35.3589L48.6166 28.6933L49.5273 20.5256L50.438 10.0108L50.7415 7.05356L52.2088 3.48605L55.1433 1.56148L57.42 2.64112L59.292 5.31674L59.039 7.05356L57.926 14.2824L55.7504 25.5952L54.3337 33.1996H55.1433L56.1046 32.2138L59.9497 27.1442L66.3752 19.0704L69.2085 15.8784L72.5478 12.3579L74.6728 10.668H78.7203L81.6548 15.0804L80.3394 19.6337L76.1906 24.8911L72.7502 29.3504L67.8172 35.9595L64.7562 41.2734L65.0307 41.7118L65.7681 41.6489L76.8989 39.255L82.9197 38.1753L90.1041 36.9549L93.3422 38.457L93.6963 40.006L92.4315 43.151L84.7411 45.0287L75.7353 46.8594L62.3244 50.0164L62.1759 50.1358L62.3512 50.3958L68.399 50.9432L70.9794 51.084H77.3037L89.0922 51.9759L92.1785 53.9944L93.9999 56.4822L93.6963 58.4068L88.9404 60.8008L82.5655 59.2987L67.6401 55.7312L62.5301 54.4638H61.8217V54.8862L66.0717 59.064L73.9139 66.1051L83.6786 75.2116L84.1845 77.4648L82.9197 79.2485L81.6042 79.0608L73.0032 72.5829L69.6639 69.6726L62.1759 63.3356H61.67V63.9928L63.3902 66.5276L72.5478 80.2812L73.0032 84.5059L72.3454 85.8672L69.9675 86.7121L67.3871 86.2427L61.9735 78.6852L56.4587 70.2359L52.0064 62.6315L51.4687 62.971L48.8189 91.2654L47.6047 92.7206L44.7714 93.8002L42.3934 92.0164L41.1286 89.1061L42.3934 83.3324L43.9113 75.8219L45.1255 69.8604L46.2386 62.4437L46.9184 59.9661L46.8583 59.8003L46.3153 59.8916L40.7238 67.5603L32.2239 79.0608L25.4948 86.2427L23.8758 86.8999L21.0931 85.4447L21.3461 82.863L22.9145 80.5629L32.2239 68.7338L37.8399 61.3641L41.4594 57.1337L41.4242 56.5218L41.2244 56.5048L16.489 72.6299L12.0873 73.1932L10.1647 71.4094L10.4176 68.4991L11.3283 67.5603L18.7657 62.4437Z"));

    static Geometry Freeze(Geometry g)
    {
        g.Freeze();
        return g;
    }

    /// <summary>
    /// Claude's mark, slowly turning while it works: a four-point spark with a small companion dot. The turn is an
    /// ambient motion, so it's held to a gentle frame rate (this window is redrawn for every frame it moves).
    /// </summary>
    static UIElement ClaudeMark(double size)
    {
        var spark = new Path
        {
            Data = ClaudeSpark,
            Fill = Clay,
            Stretch = Stretch.Uniform,
            Width = size,
            Height = size,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        System.Windows.Automation.AutomationProperties.SetName(spark, "Claude is working");
        if (MotionPrefs.Lively)
        {
            var turn = new RotateTransform();
            spark.RenderTransform = turn;
            Loops.Add(() => turn.BeginAnimation(RotateTransform.AngleProperty, null));
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2.8)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(spin, 24);
            turn.BeginAnimation(RotateTransform.AngleProperty, spin);
        }
        return spark;
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

    static IEasingFunction EaseOut => new CubicEase { EasingMode = EasingMode.EaseOut };

    static DoubleAnimation To(double from, double to, double ms, IEasingFunction? ease = null) =>
        new(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease ?? EaseOut };

    /// <summary>It slides out of the island's side and fades in.</summary>
    void Appear()
    {
        if (!MotionPrefs.Lively)
        {
            _slab.BeginAnimation(OpacityProperty, null);
            _slide.BeginAnimation(TranslateTransform.XProperty, null);
            _slab.Opacity = 1;
            _slide.X = 0;
            return;
        }
        // From wherever it is: shown again halfway through fading out, it turns round instead of starting over.
        bool fresh = _slab.Opacity < 0.02;
        _slab.BeginAnimation(OpacityProperty, To(fresh ? 0 : _slab.Opacity, 1, 200));
        _slide.BeginAnimation(TranslateTransform.XProperty, To(fresh ? 22 : _slide.X, 0, 280));
        _pop.BeginAnimation(ScaleTransform.ScaleXProperty, To(fresh ? 0.94 : _pop.ScaleX, 1, 280));
        _pop.BeginAnimation(ScaleTransform.ScaleYProperty, To(fresh ? 0.94 : _pop.ScaleY, 1, 280));
    }

    /// <summary>It slides back into the island and fades, then the window hides.</summary>
    void Disappear()
    {
        // Nothing keeps turning behind a hidden wing.
        StopLoops();
        if (!MotionPrefs.Lively)
        {
            Hide();
            return;
        }
        _hiding = true;
        // Fading out, it no longer takes clicks.
        IsHitTestVisible = false;
        var fade = To(_slab.Opacity, 0, 170, new CubicEase { EasingMode = EasingMode.EaseIn });
        fade.Completed += (_, _) =>
        {
            if (!_hiding) return;
            _hiding = false;
            Hide();
        };
        _slab.BeginAnimation(OpacityProperty, fade);
        _slide.BeginAnimation(TranslateTransform.XProperty, To(_slide.X, 16, 170, new CubicEase { EasingMode = EasingMode.EaseIn }));
    }

    /// <summary>A new look settles in: a soft fade and a hint of growth from the island's side.</summary>
    void Morph()
    {
        if (!MotionPrefs.Lively) return;
        _slab.BeginAnimation(OpacityProperty, To(0.55, 1, 180));
        _pop.BeginAnimation(ScaleTransform.ScaleXProperty, To(0.97, 1, 220));
        _pop.BeginAnimation(ScaleTransform.ScaleYProperty, To(0.97, 1, 220));
    }

    /// <summary>The newest step changed since the last draw (only then does it get its arrival).</summary>
    bool NewTop()
    {
        string top = _view.Steps.Count > 0 ? _view.Steps[0].SessionId + "\n" + _view.Steps[0].Text : "";
        if (top == _lastTop) return false;
        bool had = _lastTop.Length > 0;
        _lastTop = top;
        return had && top.Length > 0;
    }

    /// <summary>A step that just arrived eases down into its place at the top.</summary>
    static void Arrive(UIElement row)
    {
        if (!MotionPrefs.Lively) return;
        var move = new TranslateTransform(0, -6);
        row.RenderTransform = move;
        row.BeginAnimation(OpacityProperty, To(0, row.Opacity, 240));
        move.BeginAnimation(TranslateTransform.YProperty, To(-6, 0, 260));
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
