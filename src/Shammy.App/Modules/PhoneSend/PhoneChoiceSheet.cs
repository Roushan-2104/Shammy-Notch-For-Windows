using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Views.Cards;

namespace Shammy.Modules.PhoneSend;

/// <summary>
/// "What should go to your phone?": the choices that exist right now as glass rows, best first (the first one wears
/// the accent tint). Each row is a real button, named for screen readers; Tab and Enter work, and so do the number
/// keys. Esc or Back goes back a step, or closes. The shelf's files open as a second step in the same sheet.
/// </summary>
internal class PhoneChoiceSheet : CardSheet
{
    public const double SheetWidth = Ui.Tokens.PopupWidth;

    /// <summary>One row. <see cref="Pick"/> does it and says what happened (the test pipe shows that).</summary>
    public sealed record Row(string Id, string Icon, string Title, string Detail, Func<string> Pick);

    /// <summary>Extra icons in the kit's 24×24 stroked style.</summary>
    public static class Icons
    {
        public const string Image = "M4 5h16v14H4z M4 16l5-5 4 4 2.5-2.5L20 17 M15.5 8.5a1.5 1.5 0 1 1 0 3a1.5 1.5 0 1 1 0-3z";
        public const string Screenshot = "M4 9V5h4 M16 5h4v4 M20 15v4h-4 M8 19H4v-4 M9 9h6v6H9z";
        public const string Globe = "M12 3a9 9 0 1 1 0 18a9 9 0 1 1 0-18z M3 12h18 M12 3c3 3.2 3 14.8 0 18 M12 3c-3 3.2-3 14.8 0 18";
        public const string File = "M6 3h8l4 4v14H6z M14 3v4h4";
        public const string Zip = "M6 3h8l4 4v14H6z M14 3v4h4 M11 7h2 M11 10h2 M11 13h2 M10 16h4v3h-4z";
    }

    static readonly ControlTemplate Bare = MakeBare();

    readonly StackPanel _rows = new();
    readonly Action _close;
    readonly List<Row> _current = new();
    Action? _back;

    public PhoneChoiceSheet(Action close) : base("Send to phone", SheetWidth)
    {
        _close = close;
        Background = Brushes.Transparent;
        Focusable = true;
        FocusVisualStyle = null;
        BodyGap = 12;
        BackRequested = GoBack;
        Body.Children.Add(new StackPanel { Children = { Top, _rows } });
        PreviewKeyDown += OnKey;
        Loaded += (_, _) => FocusFirst();
    }

    /// <summary>Anything above the rows (the linked phone's battery, controls and music); empty by default.</summary>
    public StackPanel Top { get; } = new();

    /// <summary>The rows on screen now, top to bottom.</summary>
    public IReadOnlyList<Row> Rows => _current;

    /// <summary>True while a second step (the shelf's files) is showing.</summary>
    public bool InStep => _back is not null;

    /// <summary>Shows <paramref name="rows"/>; with <paramref name="back"/>, Back and Esc return to the previous step instead of closing.</summary>
    /// <param name="accentFirst">Tint the first row as the one thing to do (false when no row is a recommendation).</param>
    public void Show(string? status, IReadOnlyList<Row> rows, Action? back = null, bool accentFirst = true)
    {
        Status = status;
        _back = back;
        _current.Clear();
        _current.AddRange(rows);
        _rows.Children.Clear();
        for (int i = 0; i < rows.Count; i++) _rows.Children.Add(RowButton(rows[i], i, accentFirst));
        if (IsLoaded) Dispatcher.BeginInvoke(FocusFirst, DispatcherPriority.Input);
    }

    /// <summary>Picks the row at <paramref name="index"/> (0-based), as a click would.</summary>
    public string Pick(int index) =>
        index >= 0 && index < _current.Count ? _current[index].Pick() : $"error: no choice {index + 1} (there are {_current.Count})";

    void GoBack()
    {
        if (_back is { } back) back();
        else _close();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            GoBack();
            return;
        }
        int n = e.Key is >= Key.D1 and <= Key.D9 ? e.Key - Key.D1 : e.Key is >= Key.NumPad1 and <= Key.NumPad9 ? e.Key - Key.NumPad1 : -1;
        if (n < 0 || n >= _current.Count) return;
        e.Handled = true;
        _current[n].Pick();
    }

    void FocusFirst()
    {
        if (_rows.Children.Count > 0 && _rows.Children[0] is UIElement first) first.Focus();
        else Focus();
    }

    Button RowButton(Row row, int index, bool accentFirst)
    {
        bool best = index == 0 && accentFirst;
        var iconDisc = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            Background = best ? Ink.Tint : Sheet.CapFill,
            Child = CardKit.Icon(row.Icon, best ? Ink.Soft : CardKit.Ink, 16, 2.2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ((FrameworkElement)iconDisc.Child).HorizontalAlignment = HorizontalAlignment.Center;
        ((FrameworkElement)iconDisc.Child).VerticalAlignment = VerticalAlignment.Center;

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 10, 0) };
        words.Children.Add(Sheet.Word(row.Title, 14));
        if (row.Detail.Length > 0)
        {
            var detail = Sheet.Meta(row.Detail, 12.5);
            detail.Margin = new Thickness(0, 1, 0, 0);
            words.Children.Add(detail);
        }

        // The number key that picks it, quietly, for keyboard users.
        var key = Sheet.Meta((index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), 12);
        key.Foreground = CardKit.Ink2;
        key.IsHitTestVisible = false;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(iconDisc);
        Grid.SetColumn(words, 1);
        grid.Children.Add(words);
        Grid.SetColumn(key, 2);
        grid.Children.Add(key);

        Brush rest = best ? Ink.Tint : Sheet.GlassFill, hover = best ? Ink.Tint : Sheet.CapHover;
        var face = new Border
        {
            CornerRadius = new CornerRadius(18),
            Background = rest,
            BorderBrush = best ? Ink.TintEdge : Sheet.GlassEdge,
            BorderThickness = new Thickness(best ? 1.5 : 1),
            Padding = new Thickness(10, 9, 14, 9),
            Child = grid,
            SnapsToDevicePixels = true,
        };
        var button = new Button
        {
            Content = face,
            Template = Bare,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, index == 0 ? 0 : 8, 0, 0),
            ToolTip = row.Detail.Length > 0 ? row.Detail : row.Title,
        };
        AutomationProperties.SetName(button, row.Detail.Length > 0 ? $"{row.Title}: {row.Detail}" : row.Title);
        AutomationProperties.SetAcceleratorKey(button, (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Ui.FocusKit.Button(button);
        Ui.Press.Attach(button);
        button.MouseEnter += (_, _) =>
        {
            face.Background = hover;
            if (best) face.BorderBrush = Ink.Soft;
        };
        button.MouseLeave += (_, _) =>
        {
            face.Background = rest;
            if (best) face.BorderBrush = Ink.TintEdge;
        };
        button.Click += (_, e) =>
        {
            e.Handled = true;
            row.Pick();
        };
        return button;
    }

    static ControlTemplate MakeBare()
    {
        var t = new ControlTemplate(typeof(ButtonBase)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        t.Seal();
        return t;
    }
}
