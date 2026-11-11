using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Shammy.Core.Launcher;
using Shammy.Views;
using Shammy.Views.Cards;

namespace Shammy.Modules.Launcher;

/// <summary>
/// The Snippets card (was the last old-style tab): a tile with how many are saved, and a Wall list detail — each
/// trigger with what it types, × to delete, and a box in the corner to add one (";sig Best regards", Enter).
/// </summary>
internal sealed class SnippetsCard
{
    readonly Func<IReadOnlyDictionary<string, string>> _snippets;
    readonly Func<string, string, string?> _add;
    readonly Action<string> _delete;
    readonly WallTile _tile = new() { Field = Fields.List() };
    readonly TrackedText _count = CardKit.Poster("", 40);
    readonly TextBlock _first, _firstLine;
    WallDetail? _detail;
    StackPanel? _rows;

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
        var d = new WallDetail(Fields.List(), "", "Snippets", "Type a trigger anywhere, get the text");
        _detail = d;
        _rows = new StackPanel();
        d.Body.Children.Add(WallDetail.Scroll(_rows));
        d.Corner = AddBox(d);
        d.Unloaded += (_, _) =>
        {
            if (_detail != d) return;
            _detail = null;
            _rows = null;
        };
        FillRows();
        return d;
    }

    void FillRows()
    {
        if (_detail is not { } d || _rows is null) return;
        var all = Sorted.ToList();
        d.Kicker.Text = $"Snippets · {all.Count} saved";
        d.Art.Children.OfType<StackPanel>().ToList().ForEach(s => d.Art.Children.Remove(s));
        var label = CardKit.Label("Saved");
        label.Margin = new Thickness(0, 8, 0, 0);
        d.ArtBottomLeft(CardKit.Poster(all.Count.ToString(CultureInfo.InvariantCulture), 96), label);
        _rows.Children.Clear();
        foreach (var (trigger, text) in all) _rows.Children.Add(Row(trigger, text));
        if (all.Count == 0)
        {
            var empty = CardKit.Text("No snippets yet — add one in the box above", 13, FontWeights.Medium, secondary: true);
            empty.Margin = new Thickness(0, 8, 0, 0);
            _rows.Children.Add(empty);
        }
    }

    FrameworkElement Row(string trigger, string text)
    {
        var key = CardKit.Text(trigger, 13, FontWeights.Bold);
        key.MinWidth = 64;
        var body = CardKit.Text(OneLine(text), 13, FontWeights.Medium, secondary: true);
        body.ToolTip = text;
        var remove = CardKit.Quiet($"Delete {trigger}", CardKit.Icon(CardKit.Paths.Close, CardKit.Ink, 10, 2.4), () => _delete(trigger), 22);
        remove.Visibility = Visibility.Hidden;
        var row = WallDetail.ListRow(key, body, remove);
        row.MouseEnter += (_, _) => remove.Visibility = Visibility.Visible;
        row.MouseLeave += (_, _) => remove.Visibility = Visibility.Hidden;
        CardKit.Name(row, $"{trigger}: {OneLine(text)}");
        return row;
    }

    /// <summary>";sig Best regards" + Enter: the first word is the trigger, the rest what it types.</summary>
    FrameworkElement AddBox(WallDetail d)
    {
        var input = new TextBox { Style = Ui.Tokens.StyleRes("BareTextBox"), FontSize = 13, FontFamily = CardKit.Face(FontWeights.Medium), Foreground = CardKit.Ink, CaretBrush = CardKit.Accent };
        var hint = CardKit.Text(";sig Best regards", 13, FontWeights.Medium, secondary: true);
        hint.IsHitTestVisible = false;
        input.TextChanged += (_, _) => hint.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            var parts = input.Text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            string? error = parts.Length < 2 ? "Type the trigger, a space, then the text"
                : !SnippetText.IsValidTrigger(parts[0]) ? $"{parts[0]} can't be a trigger (start it with ; and no spaces)"
                : _add(parts[0].ToLowerInvariant(), parts[1]);
            if (error is null)
            {
                input.Clear();
                d.Line.Text = "Saved · type it anywhere";
                d.Line.Foreground = CardKit.Ink2;
            }
            else
            {
                d.Line.Text = error;
                d.Line.Foreground = CardKit.Alarm;
            }
        };
        var plus = CardKit.Icon(CardKit.Paths.Plus, CardKit.Ink2, 14, 2.2);
        plus.Margin = new Thickness(0, 0, 8, 0);
        var row = new DockPanel();
        DockPanel.SetDock(plus, Dock.Left);
        row.Children.Add(plus);
        row.Children.Add(new Grid { Children = { hint, input } });
        var box = new Border
        {
            Width = 240,
            Height = 32,
            CornerRadius = new CornerRadius(10),
            Background = CardKit.PillBg,
            BorderBrush = CardKit.Hairline,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(11, 0, 11, 0),
            Child = row,
            Cursor = Cursors.IBeam,
            ToolTip = "Add a snippet: the trigger, a space, then the text. Enter saves.",
        };
        box.MouseLeftButtonUp += (_, _) => input.Focus();
        return box;
    }
}
