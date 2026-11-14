using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Core.Prompter;
using Shammy.Views.Cards;

namespace Shammy.Modules.Stage;

/// <summary>What the script sheet asks of the module that opened it.</summary>
internal interface IStageScriptHost
{
    /// <summary>Whether a script is already on Stage (so Back goes back to it).</summary>
    bool HasScript { get; }

    /// <summary>The recent scripts to offer, newest first, without the one on Stage now.</summary>
    IReadOnlyList<RecentScript> Recent { get; }

    void Paste();
    void StartTyped(string text);
    void OpenFile();
    void PickRecent(RecentScript script);
    void UseCopied(string text);

    /// <summary>"Keep my script" / "Not now" on the copied-text question.</summary>
    void KeepScript();

    /// <summary>Back (or Esc) from the choices: to the script on Stage, or Stage closes.</summary>
    void Leave();

    void Dropped(IDataObject data);
}

internal enum StageSheetMode { Pick, Type, Offer }

/// <summary>
/// The calm sheet that gets a script onto Stage without commands, in the island's card family (smoked glass,
/// capsules). Three faces: the choices ("Add your script": Paste, Type, Open file, and the recent scripts), a box to
/// type or paste into with Start, and the one-tap question when Stage opens with text copied ("Use the text you
/// copied?"). A .txt or .md file, or text, dropped on it is taken too.
/// </summary>
internal sealed class StageScriptSheet : CardSheet, Shammy.Shell.ICardDrop
{
    public const double SheetWidth = 540;
    const double TypeHeight = 168;

    readonly IStageScriptHost _host;
    readonly DispatcherTimer _hintBack;
    StageSheetMode _mode;
    string _copied = "";
    string _defaultHint = "";
    TextBlock? _hint;
    TextBox? _box;
    TextBlock? _count;
    Button? _start;
    int _recentShown;

    public StageScriptSheet(IStageScriptHost host) : base("Stage", SheetWidth)
    {
        _host = host;
        BodyGap = 12;
        BackRequested = Back;
        AutomationProperties.SetName(this, "Stage script");
        _hintBack = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) => RestoreHint(), Dispatcher) { IsEnabled = false };
        PreviewKeyDown += OnKey;
        Unloaded += (_, _) => _hintBack.Stop();
    }

    public StageSheetMode Mode => _mode;

    /// <summary>The copied text the question is about (Offer face).</summary>
    public string Copied => _copied;

    /// <summary>What is typed in the box (Type face), else "".</summary>
    public string TypedText => _mode == StageSheetMode.Type ? _box?.Text ?? "" : "";

    // ---- The three faces ---------------------------------------------------------------------------------

    /// <summary>The choices: a short line, Paste / Type / Open file, and the recent scripts; <paramref name="notice"/> replaces the hint.</summary>
    public void ShowPick(string? notice = null)
    {
        _mode = StageSheetMode.Pick;
        Status = null;
        Reset();
        bool has = _host.HasScript;
        var column = new StackPanel();

        var heading = Sheet.Word(has ? "New script" : "Add your script", 18, FontWeights.SemiBold);
        heading.HorizontalAlignment = HorizontalAlignment.Left;
        column.Children.Add(heading);

        _defaultHint = has ? "Paste it, type it or open a text file. Back keeps the one you have." : "Paste it, type it or open a text file. You can drop one here too.";
        _hint = Sheet.Meta(notice ?? _defaultHint, 13);
        _hint.TextWrapping = TextWrapping.Wrap;
        _hint.TextTrimming = TextTrimming.None;
        _hint.Margin = new Thickness(0, 4, 0, 0);
        column.Children.Add(_hint);

        var paste = Sheet.Cap("Paste", "Paste your script from the clipboard", _host.Paste, CardKit.Paths.Copy, accent: true);
        var type = Sheet.Cap("Type", "Type or paste your script in a box", () => ShowType(), CardKit.Paths.Note);
        var open = Sheet.Cap("Open file", "Open a .txt or .md file", _host.OpenFile, CardKit.Paths.Folder);
        var actions = Sheet.Row(8, paste, type, open);
        actions.Margin = new Thickness(0, 14, 0, 0);
        column.Children.Add(actions);

        var recent = _host.Recent;
        _recentShown = recent.Count;
        if (recent.Count > 0)
        {
            var label = CardKit.SectionLabel("Recent");
            label.Margin = new Thickness(2, 20, 0, 8);
            label.HorizontalAlignment = HorizontalAlignment.Left;
            column.Children.Add(label);
            var now = DateTime.Now;
            foreach (var script in recent)
            {
                var row = RecentRow(script, now);
                if (column.Children[^1] is Button) row.Margin = new Thickness(0, 6, 0, 0);
                column.Children.Add(row);
            }
        }

        Body.Children.Add(column);
        if (notice is not null) Ui.A11y.Announce(notice, this, "Shammy.Stage");
        FocusSoon(paste);
    }

    /// <summary>A box to type or paste the script into, with Cancel and Start (Ctrl+Enter).</summary>
    public void ShowType(string text = "")
    {
        _mode = StageSheetMode.Type;
        Status = "type your script";
        Reset();

        var box = _box = new TextBox
        {
            Template = ScriptBoxTemplate,
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = CardKit.Ink,
            CaretBrush = CardKit.Ink,
            SelectionBrush = CardKit.Accent,
            FontFamily = CardKit.Face(FontWeights.Medium),
            FontWeight = FontWeights.Medium,
            FontSize = 14,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Top,
            Text = text,
        };
        AutomationProperties.SetName(box, "Your script");
        AutomationProperties.SetHelpText(box, "Ctrl+Enter starts Stage; Esc goes back.");
        var placeholder = CardKit.Text("Type or paste your script…", 14, FontWeights.Medium, secondary: true);
        placeholder.VerticalAlignment = VerticalAlignment.Top;
        placeholder.IsHitTestVisible = false;
        var field = new Grid { Height = TypeHeight };
        field.Children.Add(placeholder);
        field.Children.Add(box);
        var tile = Sheet.Tile(field, new Thickness(14, 12, 14, 12), 18);
        tile.Cursor = Cursors.IBeam;
        tile.MouseLeftButtonUp += (_, _) => box.Focus();

        _count = Sheet.Meta("", 12.5);
        var cancel = Sheet.Cap("Cancel", "Back to the choices (Esc)", () => ShowPick());
        _start = Sheet.Cap("Start", "Start Stage with this script (Ctrl+Enter)", Start, accent: true);
        var bottom = Sheet.Spread(_count, Sheet.Row(8, cancel, _start));
        bottom.Margin = new Thickness(0, 12, 0, 0);

        void Changed()
        {
            int words = PrompterText.Words(box.Text);
            placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _count.Text = words == 0 ? "Ctrl+Enter to start" : StageScripts.WordCount(words);
            _start.IsEnabled = words > 0;
            _start.Opacity = words > 0 ? 1 : 0.45;
        }
        box.TextChanged += (_, _) => Changed();
        Changed();

        var column = new StackPanel();
        column.Children.Add(tile);
        column.Children.Add(bottom);
        Body.Children.Add(column);
        Dispatcher.BeginInvoke(() =>
        {
            if (_box != box) return;
            box.Focus();
            Keyboard.Focus(box);
            box.CaretIndex = box.Text.Length;
        }, DispatcherPriority.Input);
    }

    /// <summary>"Use the text you copied?": its first line and size, Use it, and Keep my script (or Not now).</summary>
    public void ShowOffer(string copied)
    {
        _mode = StageSheetMode.Offer;
        _copied = copied;
        Status = null;
        Reset();
        var (text, _) = PrompterText.Clean(copied);
        var column = new StackPanel();

        var heading = Sheet.Word("Use the text you copied?", 18, FontWeights.SemiBold);
        heading.HorizontalAlignment = HorizontalAlignment.Left;
        column.Children.Add(heading);

        // Its first line as the title and a little of what follows; one line alone is shown as it is, on up to two lines.
        var excerpt = Excerpt(text);
        var title = Sheet.Word(excerpt.Length > 0 ? StageScripts.Title(text) : Cut(text, 160), 14, FontWeights.SemiBold);
        var preview = new StackPanel();
        preview.Children.Add(title);
        if (excerpt.Length > 0)
        {
            var more = Sheet.Meta(excerpt, 13);
            more.TextWrapping = TextWrapping.Wrap;
            more.MaxHeight = 38;
            more.Margin = new Thickness(0, 3, 0, 0);
            preview.Children.Add(more);
        }
        else
        {
            title.TextWrapping = TextWrapping.Wrap;
            title.MaxHeight = 40;
        }
        var words = Sheet.Meta(StageScripts.WordCount(PrompterText.Words(text)), 12.5);
        words.Margin = new Thickness(0, 6, 0, 0);
        preview.Children.Add(words);
        var card = Sheet.Tile(preview, new Thickness(14, 12, 14, 12), 18);
        card.Margin = new Thickness(0, 12, 0, 0);
        column.Children.Add(card);

        var keep = Sheet.Cap(_host.HasScript ? "Keep my script" : "Not now",
            _host.HasScript ? "Keep the script you have (Esc)" : "Not now (Esc)", _host.KeepScript);
        var use = Sheet.Cap("Use it", "Use the copied text as your script", () => _host.UseCopied(_copied), accent: true);
        var buttons = Sheet.Row(8, keep, use);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 14, 0, 0);
        column.Children.Add(buttons);

        Body.Children.Add(column);
        Ui.A11y.Announce("Use the text you copied?", this, "Shammy.Stage");
        FocusSoon(use);
    }

    /// <summary>Start with what is typed (the Type face's Start, or Ctrl+Enter).</summary>
    public void Start()
    {
        if (_mode != StageSheetMode.Type || _box is null || PrompterText.Words(_box.Text) == 0) return;
        _host.StartTyped(_box.Text);
    }

    void Back()
    {
        switch (_mode)
        {
            case StageSheetMode.Type: ShowPick(); break;
            case StageSheetMode.Offer: _host.KeepScript(); break;
            default: _host.Leave(); break;
        }
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Back();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _mode == StageSheetMode.Type && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            Start();
            e.Handled = true;
        }
    }

    void Reset()
    {
        _hintBack.Stop();
        Body.Children.Clear();
        _hint = null;
        _box = null;
        _count = null;
        _start = null;
        _recentShown = 0;
    }

    void FocusSoon(Button button) => Dispatcher.BeginInvoke(() =>
    {
        // Only when the island has the keyboard, and never stealing it from something already focused in here.
        if (button.IsVisible && IsKeyboardFocusWithin == false && Window.GetWindow(this)?.IsActive == true) button.Focus();
    }, DispatcherPriority.Input);

    /// <summary>The lines after the first, as a short preview ("" when there is only one line).</summary>
    static string Excerpt(string text) =>
        Cut(string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1)), 160);

    static string Cut(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + "…";

    // ---- Recent rows ----------------------------------------------------------------------------------------

    Button RecentRow(RecentScript script, DateTime now)
    {
        var meta = StageScripts.Meta(script, now);
        var title = Sheet.Word(script.Title, 14, FontWeights.SemiBold);
        var quiet = Sheet.Meta(meta, 12.5);
        quiet.Margin = new Thickness(12, 0, 0, 0);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(title);
        Grid.SetColumn(quiet, 1);
        grid.Children.Add(quiet);
        var face = Sheet.Tile(grid, new Thickness(14, 0, 14, 0), 16);
        face.Height = 42;
        var button = new Button { Content = face, Template = BareTemplate, Cursor = Cursors.Hand, ToolTip = $"Put “{script.Title}” on Stage" };
        AutomationProperties.SetName(button, $"Use {script.Title}, {meta}");
        Ui.FocusKit.Button(button);
        Ui.Press.Attach(button);
        button.MouseEnter += (_, _) => face.Background = Sheet.CapHover;
        button.MouseLeave += (_, _) => face.Background = Sheet.GlassFill;
        button.Click += (_, e) =>
        {
            _host.PickRecent(script);
            e.Handled = true;
        };
        return button;
    }

    static readonly ControlTemplate BareTemplate = MakeBare();

    static ControlTemplate MakeBare()
    {
        var t = new ControlTemplate(typeof(ButtonBase)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        t.Seal();
        return t;
    }

    /// <summary>A plain multi-line box: the text and caret only, scrolling with the wheel, no bars or borders.</summary>
    static readonly ControlTemplate ScriptBoxTemplate = MakeScriptBox();

    static ControlTemplate MakeScriptBox()
    {
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(FocusableProperty, false);
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        var t = new ControlTemplate(typeof(TextBox)) { VisualTree = host };
        t.Seal();
        return t;
    }

    // ---- Drops --------------------------------------------------------------------------------------------------

    public bool AcceptsDrop(IDataObject data) => StageDrop.Accepts(data);

    public void DropHover()
    {
        if (_mode != StageSheetMode.Pick || _hint is null) return;
        _hint.Text = "Let go to put it on Stage";
        _hintBack.Stop();
        _hintBack.Start();
    }

    void RestoreHint()
    {
        _hintBack.Stop();
        if (_hint is not null) _hint.Text = _defaultHint;
    }

    public void TakeDrop(IDataObject data) => _host.Dropped(data);

    // ---- For the test pipe ----------------------------------------------------------------------------------------

    public string Describe() => _mode switch
    {
        StageSheetMode.Pick => string.Create(CultureInfo.InvariantCulture,
            $"mode=pick hasScript={_host.HasScript} buttons=Paste,Type,Open file recentShown={_recentShown} hint=\"{_hint?.Text}\" height={FitHeight:0}"),
        StageSheetMode.Type => string.Create(CultureInfo.InvariantCulture,
            $"mode=type words={PrompterText.Words(_box?.Text)} start={(_start?.IsEnabled == true ? "on" : "off")} focused={_box?.IsKeyboardFocused == true} height={FitHeight:0}"),
        _ => string.Create(CultureInfo.InvariantCulture,
            $"mode=offer buttons={(_host.HasScript ? "Keep my script" : "Not now")},Use it words={PrompterText.Words(_copied)} title=\"{StageScripts.Title(_copied)}\" height={FitHeight:0}"),
    };
}
