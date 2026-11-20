using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shammy.Core.Reports;
using Shammy.Views;
using Shammy.Views.Screens;

namespace Shammy.Modules.Reports;

/// <summary>
/// The offer to send a report, dropped out of the notch: what happened, an optional note, Send / Don't send, and "See
/// what's sent", which shows the report exactly as it would leave the PC (already cleaned). After an answer it says
/// what happened and closes. It never takes focus until you click into the note.
/// </summary>
internal sealed class ReportCard : Grid
{
    public const double CardWidth = Ui.Tokens.PopupWidth;
    public const double AskHeight = 198, PreviewHeight = 440, DoneHeight = 76;

    readonly Report _report;
    readonly Func<Report, string, Task<bool>> _send;
    readonly Action<double, bool> _resize;
    readonly Action<bool> _answered;
    readonly Action _close;
    readonly TextBox _note;

    /// <param name="send">Sends the report with the note; true once the relay has it.</param>
    /// <param name="resize">Shows the card again at this height (true: it may take focus, for typing the note).</param>
    /// <param name="answered">Sent (true) or declined (false): what's remembered so it isn't asked again.</param>
    /// <param name="close">Closes the card (now, or after the last words).</param>
    public ReportCard(Report report, Func<Report, string, Task<bool>> send, Action<double, bool> resize, Action<bool> answered, Action close)
    {
        _report = report;
        _send = send;
        _resize = resize;
        _answered = answered;
        _close = close;
        Width = CardWidth;
        Background = Brushes.Transparent;
        _note = new TextBox
        {
            Style = Ui.Tokens.StyleRes("BareTextBox"),
            FontSize = 12.5,
            FontFamily = WallParts.Family(FontWeights.Normal),
            MaxLength = 400,
            AcceptsReturn = false,
        };
        AutomationProperties.SetName(_note, report.IsCrash ? "What were you doing? Optional" : "Anything to add? Optional");
        _note.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) _ = SendAsync();
        };
        _noteField = NoteField(report.IsCrash ? "What were you doing? (optional)" : "Anything to add? (optional)");
        ShowAsk();
    }

    readonly Border _noteField;

    /// <summary>Someone is reading or typing: don't close it on them (a note typed and left still goes in time).</summary>
    public bool InUse => IsMouseOver || _note.IsKeyboardFocusWithin;

    // ---- The question ---------------------------------------------------------------------------------------

    void ShowAsk()
    {
        Height = AskHeight;
        Children.Clear();
        var grid = new Grid { Margin = new Thickness(16, 14, 16, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Children.Add(grid);

        grid.Children.Add(Tile(_report.IsCrash ? new PetFace(0.95, FaceMood.Peek) : WallParts.Glyph("", 22), 56)); // warning

        var words = new StackPanel();
        SetColumn(words, 2);
        grid.Children.Add(words);
        words.Children.Add(WallParts.Label(_report.IsCrash ? "SHAMMY CLOSED UNEXPECTEDLY" : "SOMETHING KEEPS GOING WRONG"));
        var title = WallParts.PopupTitle(_report.IsCrash ? "Send a crash report?" : ErrorTitle(_report));
        title.Margin = new Thickness(0, 3, 0, 0);
        words.Children.Add(title);
        var line = WallParts.Text(_report.IsCrash
            ? $"{When(_report.WhenUtc)}. A report helps get it fixed."
            : $"{_report.Times} times today. Send a report so it gets fixed?", 12.5, FontWeights.Normal, "WallText2");
        line.Margin = new Thickness(0, 3, 0, 0);
        words.Children.Add(line);

        // The same box each time, so the note survives a look at what's sent.
        (_noteField.Parent as Panel)?.Children.Remove(_noteField);
        words.Children.Add(_noteField);
        var privacy = WallParts.Text("Names, paths, links, emails and long numbers are removed first.", 11.5, FontWeights.Normal, "WallText2");
        privacy.Margin = new Thickness(0, 7, 0, 0);
        words.Children.Add(privacy);

        var footer = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 10, 0, 0) };
        var see = WallParts.Quiet("See what’s sent", ShowPreview, 30);
        footer.Children.Add(see);
        var send = WallParts.Primary("Send", () => _ = SendAsync(), 30);
        send.MinWidth = 76;
        DockPanel.SetDock(send, Dock.Right);
        var no = WallParts.Quiet("Don’t send", Decline, 30);
        no.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(no, Dock.Right);
        footer.Children.Add(send);
        footer.Children.Add(no);
        words.Children.Add(footer);

        AutomationProperties.SetName(this, _report.IsCrash
            ? $"Shammy closed unexpectedly {When(_report.WhenUtc)}. Send a crash report?"
            : $"{ErrorTitle(_report)}, {_report.Times} times today. Send a report?");
    }

    Border NoteField(string hint)
    {
        var placeholder = WallParts.Text(hint, 12.5, FontWeights.Normal, "WallText2");
        placeholder.IsHitTestVisible = false;
        placeholder.Visibility = _note.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _note.TextChanged += (_, _) => placeholder.Visibility = _note.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // The card arrives without taking focus; a click into the note lets it, so you can type.
        _note.PreviewMouseDown += (_, _) =>
        {
            if (_note.IsKeyboardFocusWithin) return;
            _resize(Height, true);
            Dispatcher.BeginInvoke(() =>
            {
                _note.Focus();
                Keyboard.Focus(_note);
            }, System.Windows.Threading.DispatcherPriority.Input);
        };
        return new Border
        {
            Height = 34,
            CornerRadius = new CornerRadius(12),
            Background = WallParts.Res("WallTile"),
            BorderBrush = WallParts.Res("WallHairline"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 10, 0, 0),
            Cursor = Cursors.IBeam,
            Child = new Grid { Children = { placeholder, _note } },
        };
    }

    // ---- What's sent ----------------------------------------------------------------------------------------

    void ShowPreview()
    {
        Height = PreviewHeight;
        Children.Clear();
        var stack = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        Children.Add(stack);

        var header = new DockPanel();
        var back = WallParts.Icon("", "Back", ShowAskAgain, 30, 12);
        back.Margin = new Thickness(0, 0, 10, 0);
        header.Children.Add(back);
        header.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                WallParts.PopupTitle("What’s sent"),
                WallParts.Text("Exactly this. Nothing else leaves your PC.", 12, FontWeights.Normal, "WallText2"),
            },
        });
        stack.Children.Add(header);

        var facts = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        facts.ColumnDefinitions.Add(new ColumnDefinition());
        void Fact(string name, string value)
        {
            int row = facts.RowDefinitions.Count;
            facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var n = WallParts.Text(name, 12, FontWeights.Normal, "WallText2");
            var v = WallParts.Text(value, 12, FontWeights.Medium);
            n.Margin = v.Margin = new Thickness(0, 1, 0, 1);
            SetRow(n, row);
            SetRow(v, row);
            SetColumn(v, 1);
            facts.Children.Add(n);
            facts.Children.Add(v);
        }
        Fact("What", _report.IsCrash ? $"Crash · {_report.Error}" : _report.Error);
        Fact("Where", string.IsNullOrEmpty(_report.Where) ? _report.Area : $"{_report.Area} · {_report.Where}");
        Fact("When", _report.IsCrash ? When(_report.WhenUtc) : $"Today, {_report.Times} times");
        Fact("Versions", $"Shammy {_report.Version} · {_report.Windows}");
        var note = _note.Text.Trim();
        Fact("Your note", note.Length == 0 ? "None" : $"“{ReportText.Clean(note, Infrastructure.Reports.ProblemWatch.PrivateWords)}”");
        stack.Children.Add(facts);

        var lines = _report.Log.ToList();
        if (_report.Stack.Count > 0)
        {
            lines.Add("");
            lines.Add($"{(_report.IsCrash ? "Crash" : "Error")}: {_report.Error}");
            lines.AddRange(_report.Stack.Select(f => "   at " + f));
        }
        var label = WallParts.Label(_report.Log.Count > 0 ? $"LAST {_report.Log.Count} LOG LINES" : "THE ERROR");
        label.Margin = new Thickness(0, 10, 0, 6);
        stack.Children.Add(label);
        stack.Children.Add(new Border
        {
            Height = 178,
            CornerRadius = new CornerRadius(12),
            Background = WallParts.Res("WallGround"),
            BorderBrush = WallParts.Res("WallHairline"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 6, 8),
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
                Content = new TextBlock
                {
                    Text = string.Join("\n", lines),
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 10.5,
                    Foreground = WallParts.Res("WallText2"),
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        });

        var removed = ReportText.RemovedInWords(ReportText.Removed(lines.Append(note)));
        var said = WallParts.Text(removed.Length > 0 ? $"Removed before showing you: {removed}." : "Nothing personal was found in it.", 11.5, FontWeights.Normal, "WallText2");
        said.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(said);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var no = WallParts.Quiet("Don’t send", Decline, 30);
        no.Margin = new Thickness(0, 0, 8, 0);
        footer.Children.Add(no);
        var send = WallParts.Primary("Send", () => _ = SendAsync(), 30);
        send.MinWidth = 76;
        footer.Children.Add(send);
        stack.Children.Add(footer);

        AutomationProperties.SetName(this, "What’s sent: " + string.Join(". ", lines.Take(3)));
        _resize(PreviewHeight, _note.IsKeyboardFocusWithin);
    }

    void ShowAskAgain()
    {
        ShowAsk();
        _resize(AskHeight, false);
    }

    // ---- Answers --------------------------------------------------------------------------------------------

    bool _sending;

    async Task SendAsync()
    {
        if (_sending) return;
        _sending = true;
        ShowDone("Sending…", "", happy: false, closeAfter: null);
        bool ok = await _send(_report, _note.Text.Trim());
        _sending = false;
        if (ok)
        {
            _answered(true);
            ShowDone("Sent. Thank you!", _report.IsCrash ? "When it’s fixed, the update note will say so." : "Shammy won’t ask about this one again.",
                happy: true, closeAfter: TimeSpan.FromSeconds(4));
        }
        else
        {
            ShowDone("Couldn’t send it right now", "Check the internet, then try again.", happy: false, closeAfter: null,
                ("Try again", () => _ = SendAsync()), ("Close", _close));
        }
    }

    void Decline()
    {
        _answered(false);
        ShowDone("Okay, nothing was sent.", _report.IsCrash ? "The report is deleted from this PC." : "Shammy asks again only if it’s still happening after an update.",
            happy: false, closeAfter: TimeSpan.FromSeconds(3));
    }

    void ShowDone(string title, string line, bool happy, TimeSpan? closeAfter, params (string Label, Action Click)[] buttons)
    {
        Height = DoneHeight;
        Children.Clear();
        var grid = new Grid { Margin = new Thickness(16, 12, 16, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Children.Add(grid);
        grid.Children.Add(Tile(new PetFace(0.75, happy ? FaceMood.Happy : FaceMood.Idle), 44));
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(WallParts.Text(title, 15, FontWeights.Bold));
        if (line.Length > 0) words.Children.Add(WallParts.Text(line, 12, FontWeights.Normal, "WallText2"));
        SetColumn(words, 2);
        grid.Children.Add(words);
        if (buttons.Length > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < buttons.Length; i++)
            {
                var b = i == 0 ? WallParts.Primary(buttons[i].Label, buttons[i].Click, 30) : WallParts.Quiet(buttons[i].Label, buttons[i].Click, 30);
                if (i > 0) b.Margin = new Thickness(8, 0, 0, 0);
                row.Children.Add(b);
            }
            SetColumn(row, 3);
            grid.Children.Add(row);
        }
        AutomationProperties.SetName(this, $"{title}. {line}");
        Ui.A11y.Announce($"{title}. {line}", this);
        _resize(DoneHeight, false);
        if (closeAfter is { } wait)
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = wait };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _close();
            };
            timer.Start();
        }
    }

    // ---- Words ----------------------------------------------------------------------------------------------

    static Border Tile(UIElement content, double size) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(Ui.Tokens.DetailRadius),
        Background = ScreenKit.Tile,
        VerticalAlignment = VerticalAlignment.Top,
        ClipToBounds = true,
        Child = content is FrameworkElement fe ? Centered(fe) : content,
    };

    static FrameworkElement Centered(FrameworkElement fe)
    {
        fe.HorizontalAlignment = HorizontalAlignment.Center;
        fe.VerticalAlignment = VerticalAlignment.Center;
        return fe;
    }

    /// <summary>"Looking up lyrics failed" from "Looking up lyrics failed (HttpRequestException)".</summary>
    internal static string ErrorTitle(Report report)
    {
        var e = report.Error;
        var paren = e.LastIndexOf(" (", StringComparison.Ordinal);
        return paren > 0 ? e[..paren] : e;
    }

    /// <summary>"Today at 9:04 AM", "Yesterday at 8:32 PM", "Nov 18 at 8:32 PM".</summary>
    internal static string When(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var days = (DateTime.Today - local.Date).Days;
        var day = days switch { 0 => "Today", 1 => "Yesterday", _ => local.ToString("MMM d") };
        return $"{day} at {local:h:mm tt}";
    }
}
