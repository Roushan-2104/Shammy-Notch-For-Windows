using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shammy.Core.Launcher;
using Shammy.Views;
using Shammy.Views.Cards;

namespace Shammy.Modules.Launcher;

/// <summary>
/// The Snippets card (was the last old-style tab): a tile with how many are saved, and a card-sheet detail
/// (<see cref="Detail"/>): each trigger as a capsule with what it types today, click to edit, × to delete, and
/// "New" for the add box (";sig Best regards", Enter).
/// </summary>
internal sealed class SnippetsCard
{
    readonly Func<IReadOnlyDictionary<string, string>> _snippets;
    readonly Func<string, string, string?> _add;
    readonly Action<string> _delete;
    readonly WallTile _tile = new() { Field = Fields.List() };
    readonly TrackedText _count = CardKit.Poster("", 40);
    readonly TextBlock _first, _firstLine;
    Detail? _detail;

    /// <param name="add">Saves trigger → text; returns why it couldn't (a clash, a bad trigger) or null.</param>
    public SnippetsCard(Func<IReadOnlyDictionary<string, string>> snippets, Func<string, string, string?> add, Action<string> delete)
    {
        _snippets = snippets;
        _add = add;
        _delete = delete;
        var label = CardKit.Label("Saved");
        label.Margin = new Thickness(0, 6, 0, 0);
        _tile.Art.Children.Add(CardKit.At(CardKit.Column(0, _count, label), HorizontalAlignment.Left, VerticalAlignment.Top, WallTile.Pad, WallTile.Pad));
        _tile.Rest.Children.Add(WallTile.BottomLeft(CardKit.Label("Snippets")));
        var words = WallTile.Words("", "", out _first, out _firstLine);
        _tile.Lit.Children.Add(CardKit.At(words, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, WallTile.Pad, 0, WallTile.Pad, 10));
        Refresh();
    }

    public FrameworkElement Compact => _tile;

    static string OneLine(string text) => text.Replace("\r\n", "\n").Replace("\n", " ↵ ");

    IEnumerable<KeyValuePair<string, string>> Sorted => _snippets().OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase);

    public void Refresh()
    {
        var all = Sorted.ToList();
        _count.Text = all.Count.ToString(CultureInfo.InvariantCulture);
        _first.Text = all.Count == 0 ? "No snippets yet" : "Type a trigger anywhere";
        _firstLine.Text = all.Count == 0 ? "Add one: ;sig Best regards" : string.Join("  ", all.Take(3).Select(s => s.Key));
        CardKit.Name(_tile, $"Snippets, {all.Count} saved. Open snippets");
        FillRows();
    }

    public FrameworkElement BuildDetail()
    {
        var d = new Detail(this);
        _detail = d;
        d.Unloaded += (_, _) =>
        {
            if (_detail == d) _detail = null;
        };
        return d;
    }

    void FillRows() => _detail?.Fill();

    /// <summary>
    /// The opened Snippets card (the card family's shape, 560 wide, as tall as its list): "New" in the header opens
    /// the add box (";sig Best regards", Enter saves). Each snippet is a row: its trigger as an accent-tint capsule,
    /// an arrow, and what it types today (the real date or time, not "{date}"). Click a row to edit it in the same
    /// box; × on hover deletes it. About six rows show; more scroll.
    /// </summary>
    sealed class Detail : CardSheet
    {
        const double SheetWidth = 560, RowHeight = 28, RowGap = 8;
        const string Arrow = "M5 12h14 M13 6l6 6-6 6";
        static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

        readonly SnippetsCard _card;
        readonly TextBox _input = new();
        readonly TextBlock _note = Sheet.Meta("", 12);
        readonly StackPanel _editor = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 12) };
        readonly ContentControl _list = new() { Focusable = false };
        readonly List<(TextBlock Text, string Template)> _live = new();

        public Detail(SnippetsCard card) : base("Snippets", SheetWidth, "type a trigger anywhere")
        {
            _card = card;
            BodyGap = 12;
            Action(Sheet.Cap("New", "New snippet", () => OpenEditor(""), CardKit.Paths.Plus));

            System.Windows.Automation.AutomationProperties.SetName(_input, "Add a snippet: the trigger, a space, then the text");
            _input.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                Save();
            };
            var field = Sheet.Field(_input, ";sig Best regards", CardKit.Paths.Plus);
            field.ToolTip = "Add a snippet: the trigger, a space, then the text. Enter saves.";
            field.MouseLeftButtonUp += (_, _) => _input.Focus();
            _note.Margin = new Thickness(4, 6, 0, 0);
            _note.Text = "The trigger, a space, then the text. Enter saves.";
            _editor.Children.Add(field);
            _editor.Children.Add(_note);

            var body = new StackPanel();
            body.Children.Add(_editor);
            body.Children.Add(_list);
            Body.Children.Add(body);
            // ";time" types the time now, so its preview follows the clock while it's looked at.
            CardKit.TickWhileVisible(this, TimeSpan.FromSeconds(20), UpdateTexts);
            Fill();
        }

        /// <summary>What a snippet types right now; the clipboard isn't read for a preview.</summary>
        static string Today(string template) => OneLine(SnippetText.Expand(template, DateTime.Now, () => "{clipboard}"));

        void UpdateTexts()
        {
            foreach (var (text, template) in _live) text.Text = Today(template);
        }

        public void Fill()
        {
            var all = _card.Sorted.ToList();
            _live.Clear();
            if (all.Count == 0)
            {
                var empty = Sheet.Meta("No snippets yet. Add one with New, like “;sig Best regards”.", 13);
                empty.Margin = new Thickness(4, 2, 0, 0);
                _list.Content = empty;
                return;
            }
            var rows = new StackPanel();
            foreach (var (trigger, text) in all)
            {
                var row = Row(trigger, text);
                if (rows.Children.Count > 0) row.Margin = new Thickness(0, RowGap, 0, 0);
                rows.Children.Add(row);
            }
            _list.Content = new ScrollViewer
            {
                Content = rows,
                MaxHeight = 6 * RowHeight + 5.5 * RowGap + RowHeight / 2,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
            };
        }

        FrameworkElement Row(string trigger, string template)
        {
            var key = Sheet.Chip(trigger, height: RowHeight, font: Mono);
            key.MinWidth = 82;
            key.Padding = new Thickness(12, 0, 12, 0);
            if (key.Child is TextBlock k) k.FontSize = 13;
            var arrow = CardKit.Icon(Arrow, Ui.Tokens.Brush("#73EBEBF5"), 14, 2.2);
            arrow.Margin = new Thickness(12, 0, 12, 0);
            arrow.VerticalAlignment = VerticalAlignment.Center;
            var text = Sheet.Word(Today(template), 14, FontWeights.SemiBold);
            text.ToolTip = template;
            _live.Add((text, template));
            var remove = Sheet.Round($"Delete {trigger}", CardKit.Paths.Close, () => _card._delete(trigger), 24, iconSize: 10);
            remove.Visibility = Visibility.Hidden;
            remove.Margin = new Thickness(8, 0, 0, 0);

            var grid = new Grid { MinHeight = RowHeight, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(key);
            Grid.SetColumn(arrow, 1);
            grid.Children.Add(arrow);
            Grid.SetColumn(text, 2);
            grid.Children.Add(text);
            Grid.SetColumn(remove, 3);
            grid.Children.Add(remove);
            grid.MouseEnter += (_, _) => remove.Visibility = Visibility.Visible;
            grid.MouseLeave += (_, _) => remove.Visibility = Visibility.Hidden;
            // Click a row to edit it: the add box opens with the trigger and its text; Enter saves over it.
            grid.MouseLeftButtonUp += (_, e) =>
            {
                if (e.Handled) return;
                e.Handled = true;
                OpenEditor($"{trigger} {template}");
            };
            grid.ToolTip = "Click to edit";
            CardKit.Name(grid, $"{trigger}: {OneLine(template)}");
            return grid;
        }

        void OpenEditor(string text)
        {
            _editor.Visibility = Visibility.Visible;
            Status = "type a trigger anywhere";
            _input.Text = text;
            _input.CaretIndex = _input.Text.Length;
            _note.Text = text.Length == 0 ? "The trigger, a space, then the text. Enter saves." : "Change the text and press Enter to save it.";
            _note.Foreground = CardKit.Ink2;
            Dispatcher.BeginInvoke(() => _input.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>";sig Best regards" + Enter: the first word is the trigger, the rest what it types.</summary>
        void Save()
        {
            var parts = _input.Text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            string? error = parts.Length < 2 ? "Type the trigger, a space, then the text"
                : !SnippetText.IsValidTrigger(parts[0]) ? $"{parts[0]} can’t be a trigger (start it with ; and no spaces)"
                : _card._add(parts[0].ToLowerInvariant(), parts[1]);
            if (error is null)
            {
                _input.Clear();
                _editor.Visibility = Visibility.Collapsed;
                Status = "saved · type it anywhere";
                Ui.A11y.Announce("Saved · type it anywhere", _input);
                return;
            }
            _note.Text = error;
            _note.Foreground = CardKit.Ink;
            Ui.A11y.Announce(error, _input);
        }
    }
}
