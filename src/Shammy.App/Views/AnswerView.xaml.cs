using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Shammy.Views;

/// <summary>One of a moment's actions: a pill ("Call", "Copy"). <see cref="Run"/> returns a short word back to show
/// ("Copied"), or null; with <see cref="Closes"/> and no word back, the moment closes (another app took over).</summary>
internal sealed record MomentAction(string Label, Func<string?> Run, bool Closes = false);

/// <summary>A quiet "send it" icon on a moment (Notes, Shelf, phone, Stage); returns the word back to show.</summary>
internal sealed record MomentSend(string Glyph, string Tip, Func<Task<string?>> Run);

/// <summary>Circle to search's answer in the island: what it is, the big line, an optional line of detail, and what to do next.</summary>
internal sealed record IslandMoment(string Glyph, string Label, string Title, string? Body, IReadOnlyList<MomentAction> Actions,
    IReadOnlyList<MomentSend> Sends)
{
    /// <summary>A circled colour: the orb shows the colour itself.</summary>
    public Color? Swatch { get; init; }

    /// <summary>A short answer in words (AI Mode's first lines, a translation): a few lines under the title.</summary>
    public string? Answer { get; init; }

    /// <summary>Where <see cref="Answer"/> came from ("Google AI Mode", "Google Translate"), as a small chip by the label.</summary>
    public string? Source { get; init; }

    /// <summary>A "say or type what to do" box under the actions; Enter hands it the words.</summary>
    public Action<string>? Ask { get; init; }

    /// <summary>A speaker button that reads this aloud (Gnani).</summary>
    public string? SpeakText { get; init; }

    /// <summary>The card's width (560 by default; answers with a few lines are wider).</summary>
    public double Width { get; init; } = 560;
}

/// <summary>
/// Instant local answers (arithmetic) that don't need a web search, and confirmations for commands. Focus Wall: the
/// answer as a big tight numeral, Copy as the one accent control, Save as note and Search quiet beside it.
/// </summary>
public partial class AnswerView : UserControl
{
    string _query = "";
    string? _copyText;

    public AnswerView()
    {
        InitializeComponent();
        MomentAskBox.KeyDown += OnAskKey;
        MomentAskBox.TextChanged += (_, _) => MomentAskHint.Visibility = MomentAskBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The moment on show, if any.</summary>
    internal IslandMoment? CurrentMoment => _moment;

    /// <summary>The "say or type" box (the notch hands it the keyboard when it's clicked).</summary>
    internal TextBox AskBox => MomentAskBox;

    internal event Action<string>? SearchWebRequested;

    /// <summary>The plain answer card (a sum, a command's confirmation): 440 × 120.</summary>
    void UsePlain()
    {
        Moment.Visibility = Visibility.Collapsed;
        Plain.Visibility = Visibility.Visible;
        Width = 440;
        Height = 120;
        _moment = null;
    }

    internal void Show(string query, string expression, string result)
    {
        UsePlain();
        _query = query;
        Expression.Text = expression + " =";
        Expression.Visibility = Visibility.Visible;
        Result.Text = result;
        // A sum's answer is a number: light, in the island's display face, like the numbers on its home.
        Result.FontFamily = Ui.Tokens.FontRes("ShammyDisplayFont");
        Result.FontWeight = FontWeights.Light;
        Result.FontSize = FitSize(result, 40, 24);
        Result.LineHeight = Result.FontSize + 4;
        CopyButton.Content = "";
        NoteButton.Content = "";
        GlyphOrb.Visibility = Visibility.Collapsed;
        _copyText = null;
        SearchWebButton.Visibility = Visibility.Visible;
        Actions.Visibility = Visibility.Visible;
    }

    /// <summary>Confirmation for a module command ("Timer set", "Playing …").</summary>
    internal void ShowCommand(Modules.CommandResult r)
    {
        UsePlain();
        Glyph.Text = r.Glyph;
        // Colour only for meaning: red when it failed, white otherwise.
        Glyph.Foreground = (System.Windows.Media.Brush)Application.Current.Resources[r.IsError ? "WallRed" : "WallText"];
        GlyphOrb.Visibility = Visibility.Visible;
        Result.Text = r.Title;
        if (!r.IsError && Shammy.Core.Search.AnswerText.IsValue(r.Title))
        {
            // A value (100%, 9:45 AM, 152.4 cm, ₹9,425) is a number: light, in the display face, like the home's numbers.
            Result.FontFamily = Ui.Tokens.FontRes("ShammyDisplayFont");
            Result.FontWeight = FontWeights.Light;
            Result.FontSize = FitSize(r.Title, 30, 22);
            Result.LineHeight = Result.FontSize + 4;
        }
        else
        {
            WallParts.Face(Result, FontWeights.Bold);
            Result.FontWeight = FontWeights.Bold;
            Result.FontSize = 19;
            Result.LineHeight = 24;
        }
        Expression.Text = r.Detail ?? "";
        Expression.Visibility = string.IsNullOrEmpty(r.Detail) ? Visibility.Collapsed : Visibility.Visible;
        // An answer worth keeping (a converted amount, a time elsewhere) gets Copy and Save as note; the rest need none.
        _copyText = r.CopyText;
        NoteButton.Content = "";
        CopyButton.Content = "";
        SearchWebButton.Visibility = Visibility.Collapsed;
        Actions.Visibility = r.CopyText is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Width the answer has beside the buttons (the card is a fixed 440 wide).</summary>
    const double ResultRoom = 270;

    /// <summary>Long answers step down from the poster size instead of being cut off with an ellipsis.</summary>
    double FitSize(string text, double size, double min)
    {
        var face = new System.Windows.Media.Typeface(Result.FontFamily, FontStyles.Normal, Result.FontWeight, FontStretches.Normal);
        double width = new System.Windows.Media.FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            face, size, Result.Foreground, System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip).Width;
        return width <= ResultRoom ? size : Math.Max(min, Math.Floor(size * ResultRoom / width));
    }

    /// <summary>For the test pipe: the shown answer and whether it offers Copy (or the moment, its actions and send targets).</summary>
    internal string Describe() => _moment is { } m
        ? $"moment glyph={(m.Swatch is null ? "icon" : "swatch")} label={MomentLabel.Text} title={MomentTitle.Text} body={(m.Body ?? "-")} " +
          $"actions=[{string.Join("|", m.Actions.Select(a => a.Label))}] send=[{string.Join("|", m.Sends.Select(s => s.Tip))}] size={Width}x{Height}"
        : $"title={Result.Text} copy={(Actions.Visibility == Visibility.Visible && CopyButton.IsVisible ? _copyText ?? Result.Text : "-")}";

    // ---- Circle to search's moment --------------------------------------------------------------------------

    IslandMoment? _moment;

    /// <summary>The moment's top was clicked: show the full results.</summary>
    internal event Action? MomentExpandRequested;

    /// <summary>An action handed off to another app (a call, a link, the calendar): the moment can go.</summary>
    internal event Action? MomentDone;

    /// <summary>
    /// What was circled, understood (a phone number, a date, a sum, a colour, a short fact), with its actions and the
    /// quiet "send it" icons. 560 wide; taller when there's a line of body text.
    /// </summary>
    internal void ShowMoment(IslandMoment m)
    {
        _moment = m;
        Plain.Visibility = Visibility.Collapsed;
        Moment.Visibility = Visibility.Visible;
        Width = m.Width;

        MomentLabel.Text = m.Label;
        MomentTitle.Text = m.Title;
        MomentTitle.FontSize = m.Title.Length > 26 ? 20 : 23;
        MomentBody.Text = m.Body ?? "";
        MomentBody.Visibility = string.IsNullOrEmpty(m.Body) ? Visibility.Collapsed : Visibility.Visible;
        MomentAnswer.Text = m.Answer ?? "";
        MomentAnswer.Visibility = string.IsNullOrEmpty(m.Answer) ? Visibility.Collapsed : Visibility.Visible;
        MomentSourceText.Text = m.Source ?? "";
        MomentSource.Visibility = string.IsNullOrEmpty(m.Source) ? Visibility.Collapsed : Visibility.Visible;
        MomentAsk.Visibility = m.Ask is null ? Visibility.Collapsed : Visibility.Visible;
        MomentAskBox.Text = "";

        if (m.Swatch is { } swatch)
        {
            // A circled colour is shown as itself.
            MomentOrb.Background = new SolidColorBrush(swatch);
            MomentGlyph.Text = "";
        }
        else
        {
            // The island's light in the orb: the palette's first colour, softly, under a white glyph.
            var c = Theme.PaletteTheme.ToColor(Theme.PaletteTheme.First);
            MomentOrb.Background = new LinearGradientBrush(Color.FromArgb(0x55, c.R, c.G, c.B), Color.FromArgb(0x1C, c.R, c.G, c.B), 45);
            MomentGlyph.Text = m.Glyph;
            MomentGlyph.Foreground = (Brush)Application.Current.Resources["WallText"];
        }

        MomentActions.Children.Clear();
        for (int i = 0; i < m.Actions.Count; i++)
        {
            var action = m.Actions[i];
            var button = new Button
            {
                Content = action.Label,
                Style = (Style)FindResource(i == 0 ? "WallPrimaryButton" : "WallQuietButton"),
                Margin = new Thickness(0, 0, 8, 0),
            };
            AutomationProperties.SetName(button, action.Label);
            button.Click += (_, _) => RunAction(action);
            MomentActions.Children.Add(button);
        }

        SendRow.Children.Clear();
        foreach (var send in m.Sends)
        {
            var button = new Button
            {
                Content = send.Glyph,
                Style = (Style)FindResource("WallQuietButton"),
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontWeight = FontWeights.Normal,
                FontSize = 13,
                Width = 32,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = send.Tip,
            };
            AutomationProperties.SetName(button, send.Tip);
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                string? said;
                try { said = await send.Run(); }
                catch (Exception ex)
                {
                    Infrastructure.Log.Warn($"Sending a circle ({send.Tip}) failed", ex);
                    said = "That didn't work";
                }
                if (!ReferenceEquals(_moment, m)) return;
                button.IsEnabled = true;
                if (said is not null) Say(said);
            };
            SendRow.Children.Add(button);
        }
        if (m.SpeakText is { Length: > 0 } speak)
        {
            var listen = new Button
            {
                Content = "",
                Style = (Style)FindResource("WallQuietButton"),
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontWeight = FontWeights.Normal,
                FontSize = 13,
                Width = 32,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = "Read it aloud",
            };
            AutomationProperties.SetName(listen, "Read it aloud");
            listen.Click += (_, _) => Modules.HostLinks.SayAnyway?.Invoke(speak);
            SendRow.Children.Add(listen);
        }
        FitMoment();
    }

    /// <summary>The card is as tall as what it holds (a short answer can run to a few lines).</summary>
    void FitMoment()
    {
        // Lay out what just changed first: a direct Measure with the same size as last time would reuse the old answer.
        // DesiredSize already includes the margins.
        UpdateLayout();
        Moment.InvalidateMeasure();
        Moment.Measure(new Size(Width, double.PositiveInfinity));
        Height = Math.Max(120, Math.Ceiling(Moment.DesiredSize.Height));
    }

    void OnAskKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || _moment?.Ask is not { } ask) return;
        e.Handled = true;
        var words = MomentAskBox.Text.Trim();
        if (words.Length > 0) ask(words);
    }

    void RunAction(MomentAction action)
    {
        string? said;
        try { said = action.Run(); }
        catch (Exception ex)
        {
            Infrastructure.Log.Warn($"A circle action ({action.Label}) failed", ex);
            said = "That didn't work";
        }
        if (action.Closes && said is null) MomentDone?.Invoke();
        else if (said is not null) Say(said);
    }

    /// <summary>A short word back ("Copied", "Saved to Notes") in the label line, with a check.</summary>
    void Say(string text) => MomentLabel.Text = "✓  " + text;

    void OnMomentExpand(object sender, System.Windows.Input.MouseButtonEventArgs e) => MomentExpandRequested?.Invoke();

    /// <summary>Test pipe: what the moment shows (label | title | answer | actions).</summary>
    internal string DescribeMomentForTest() => _moment is not { } m ? "-"
        : $"{MomentLabel.Text} | {m.Title} | {(m.Answer ?? "").Replace('\n', ' ')} | source={m.Source ?? "-"} | " +
          $"actions={string.Join(",", m.Actions.Select(x => x.Label))} | ask={(m.Ask is null ? "no" : "yes")} | size={Width}x{Height} " +
          $"parts={MomentTop.DesiredSize.Height:0}+{MomentActions.DesiredSize.Height:0}+{MomentAsk.DesiredSize.Height:0} answer={MomentAnswer.DesiredSize.Height:0} title={MomentTitle.DesiredSize.Height:0}";

    /// <summary>Test pipe: runs the moment's action or send target by its label, as a click would.</summary>
    internal async Task<string> RunMomentForTest(string label)
    {
        if (_moment is not { } m) return "no moment";
        if (m.Actions.FirstOrDefault(a => string.Equals(a.Label, label, StringComparison.OrdinalIgnoreCase)) is { } action)
        {
            RunAction(action);
            return MomentLabel.Text;
        }
        if (m.Sends.FirstOrDefault(s => string.Equals(s.Tip, label, StringComparison.OrdinalIgnoreCase)) is { } send)
        {
            var said = await send.Run();
            if (said is not null) Say(said);
            return MomentLabel.Text;
        }
        return "no such action";
    }

    void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_copyText ?? Result.Text.Replace(",", ""));
            CopyButton.Content = "";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another app is holding the clipboard; nothing useful to do.
        }
    }

    void OnSearchWeb(object sender, RoutedEventArgs e) => SearchWebRequested?.Invoke(_query);

    /// <summary>"Save as note": the answer as "12 × 7 = 84"; a listener (Connect) saves it.</summary>
    internal event Func<string, bool>? SaveNoteRequested;

    void OnSaveNote(object sender, RoutedEventArgs e)
    {
        var expression = Expression.Text.TrimEnd('=', ' ');
        var text = expression.Length > 0 ? $"{expression} = {Result.Text}" : Result.Text;
        if (SaveNoteRequested?.Invoke(text) == true) NoteButton.Content = "";
    }
}
