using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Shammy.Views;
using Shammy.Views.Cards;
using Shammy.Views.Screens;

namespace Shammy.Search;

/// <summary>
/// Dictation's moments in the island: the live one while you talk (a dot, "Dictating", the script, the mic level,
/// the last two lines of what was heard with a caret, and where it will go), the "Typed into …" pill with Undo, and
/// a short message (password box, no internet) with at most one button.
/// </summary>
internal sealed class DictationCard : Grid
{
    public const double LiveWidth = Ui.Tokens.PopupWidth, LiveHeight = 132, PillWidth = 380, PillHeight = 52,
        MessageWidth = Ui.Tokens.PopupWidth, MessageHeight = 104;

    /// <summary>About two lines of the live words at this size; older words scroll off the front.</summary>
    const int LiveChars = 96;

    TextBlock? _words, _line;
    SpacedText? _script;
    VisualizerBars? _bars;

    DictationCard(double width, double height, string name)
    {
        Width = width;
        Height = height;
        Background = Brushes.Transparent;
        AutomationProperties.SetName(this, name);
    }

    /// <summary>The live moment. <paramref name="level"/> is the microphone, 0–1.</summary>
    public static DictationCard Live(Func<float> level, string app, string howToFinish)
    {
        var card = new DictationCard(LiveWidth, LiveHeight, $"Dictating into {app}");
        card._bars = new VisualizerBars { BarCount = 5, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        card._bars.Source = i => level() * (0.55f + 0.45f * (float)Math.Abs(Math.Sin(Environment.TickCount64 / 90.0 + i * 1.3)));
        card._bars.Color = Ui.Tokens.Text.Color;
        card._bars.Active = true;

        card._script = ScreenKit.Label("EN · Roman", size: 10);
        card._script.Margin = new Thickness(4, 0, 0, 0);
        var head = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(card._bars, Dock.Right);
        head.Children.Add(card._bars);
        head.Children.Add(CardKit.Row(8, CardKit.Dot(8, ScreenKit.Accent), ScreenKit.Label("Dictating", ScreenKit.Fg), card._script));

        card._words = ScreenKit.Text("", 15, Weight.Medium);
        card._words.TextWrapping = TextWrapping.Wrap;
        card._words.TextTrimming = TextTrimming.None;
        card._words.LineHeight = 21;
        card._words.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        card._words.Height = 42;
        card._words.VerticalAlignment = VerticalAlignment.Top;
        card._words.Margin = new Thickness(0, 10, 0, 0);

        card._line = ScreenKit.Text($"into {app} · {howToFinish}", 12, Weight.Regular, ScreenKit.Fg2);
        card._line.Margin = new Thickness(0, 8, 0, 0);

        var column = CardKit.Column(0, head, card._words, card._line);
        column.Margin = new Thickness(20, 16, 20, 14);
        card.Children.Add(column);
        card.SetWords(null, hidden: false);
        return card;
    }

    /// <summary>The words so far (the end of them), with a caret; hidden shows only "Dictating…" (the screen is shared).</summary>
    public void SetWords(string? words, bool hidden)
    {
        if (_words is null) return;
        _words.Inlines.Clear();
        var w = (words ?? "").Trim();
        if (hidden || w.Length == 0)
        {
            _words.Inlines.Add(new Run(hidden ? "Dictating…" : "Start talking") { Foreground = ScreenKit.Fg2 });
        }
        else
        {
            if (w.Length > LiveChars) w = "…" + w[^LiveChars..].TrimStart();
            _words.Inlines.Add(new Run(w));
        }
        // The caret: where the words are going.
        _words.Inlines.Add(new Run(" |") { Foreground = ScreenKit.Accent });
    }

    /// <summary>"EN · ROMAN", or Devanagari once "in Hindi" was said.</summary>
    public void SetScript(bool devanagari)
    {
        if (_script is not null) _script.Text = devanagari ? "हिंदी · देवनागरी" : "EN · ROMAN";
    }

    /// <summary>The quiet line under the words ("into Notepad · let go to type it", then "Typing it in…").</summary>
    public void SetLine(string text, bool listening)
    {
        if (_line is not null) _line.Text = text;
        if (_bars is not null) _bars.Active = listening;
    }

    /// <summary>"✓ Typed into WhatsApp · 14 words" with Undo.</summary>
    public static DictationCard Typed(string app, int words, Action undo)
    {
        string said = $"Typed into {app} · {words} {(words == 1 ? "word" : "words")}";
        var card = new DictationCard(PillWidth, PillHeight, said);
        var check = CardKit.FluentGlyph("", 13, Ui.Tokens.Ok);
        check.Margin = new Thickness(0, 0, 10, 0);
        var undoButton = ScreenKit.Secondary("Undo", undo, 28);
        // The Undo capsule sits concentric in the pill's round end (8 px all round); the words keep their 20.
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(20, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(undoButton, Dock.Right);
        row.Children.Add(undoButton);
        row.Children.Add(CardKit.Row(0, check, ScreenKit.Text(said, 13, Weight.SemiBold)));
        card.Children.Add(row);
        return card;
    }

    /// <summary>A short message ("Dictation needs internet") with an optional button (Try again, Copy).</summary>
    public static DictationCard Message(string title, string detail, string? button = null, Action? click = null)
    {
        var card = new DictationCard(MessageWidth, MessageHeight, $"{title}. {detail}");
        var rows = new Grid { Margin = new Thickness(20, 16, 16, 16) };
        var heading = ScreenKit.PopupTitle(title);
        heading.HorizontalAlignment = HorizontalAlignment.Left;
        var why = ScreenKit.Para(detail, 12.5, ScreenKit.Fg2, 17);
        why.Margin = new Thickness(0, 6, button is null ? 0 : 110, 0);
        var words = CardKit.Column(0, heading, why);
        words.VerticalAlignment = VerticalAlignment.Top;
        rows.Children.Add(words);
        if (button is not null && click is not null)
        {
            var b = ScreenKit.Secondary(button, click, 30, fontSize: 12.5);
            b.HorizontalAlignment = HorizontalAlignment.Right;
            b.VerticalAlignment = VerticalAlignment.Bottom;
            rows.Children.Add(b);
        }
        card.Children.Add(rows);
        return card;
    }

    /// <summary>For the test pipe: what the card says (the live words only as a count: they may be someone's real speech).</summary>
    public string Describe() => _words is null ? AutomationProperties.GetName(this)
        : $"live shown_words={Core.Dictation.DictationText.Words(new TextRange(_words.ContentStart, _words.ContentEnd).Text.TrimEnd(' ', '|'))} script='{_script?.Text}' line='{_line?.Text}'";
}
