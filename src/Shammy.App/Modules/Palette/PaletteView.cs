using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shammy.Core.Palette;
using Shammy.Views;

namespace Shammy.Modules.Palette;

/// <summary>What one row shows; built by the module, drawn by <see cref="PaletteView"/>.</summary>
internal sealed record PaletteRowView(PaletteItem Item, string Glyph, ImageSource? Icon, FrameworkElement? Control);

/// <summary>
/// The rows under the typing bar, in the Focus Wall look: a quiet list (icon, title, grey subtitle, a tracked hint on
/// the right) where only the chosen row gets the accent outline. Before anything is typed, the recent picks sit as a
/// small rail of tiles above the "Try" row. Rows with a <see cref="PaletteRowView.Control"/> (a slider, a switch)
/// are taller and host it under the title. Nothing here takes keyboard focus, so typing always stays in the box.
/// </summary>
internal sealed class PaletteView : Border
{
    public const double RowHeight = 44, ControlRowHeight = 58, TopPad = 4, BottomPad = 10;

    /// <summary>The recent-picks rail: its label line plus one row of tiles and the gap under it.</summary>
    const double RailLabelHeight = 24, RailTileHeight = 64, RailGap = 8;

    const double Outline = 1.5;

    /// <summary>Everything Select and the in-place updates need for one item, whether drawn as a row or a tile.</summary>
    sealed record Slot(Border Frame, TextBlock Title, Border IconBox, double IconSize, TrackedText? Hint, Border? HintBox, bool Tile);

    readonly StackPanel _rows = new();
    readonly List<Slot> _slots = new();
    IReadOnlyList<PaletteRowView> _shown = Array.Empty<PaletteRowView>();
    Point? _lastMouse;

    public PaletteView()
    {
        Focusable = false;
        Padding = new Thickness(8, TopPad, 8, BottomPad);
        var stack = new StackPanel();
        stack.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(10, 0, 10, 4),
            Background = Brush("WallHairline"),
        });
        stack.Children.Add(_rows);
        Child = stack;
    }

    /// <summary>A row was clicked; the flag is true with Ctrl held.</summary>
    public event Action<int, bool>? RowInvoked;

    /// <summary>The pointer moved onto a row (it really moved: a notch growing under a still pointer doesn't count).</summary>
    public event Action<int>? RowHovered;

    /// <summary>How many leading items are drawn as rail tiles (0 when the rows are an ordinary list).</summary>
    public int RailCount { get; private set; }

    /// <summary>Draws the rows and returns the height they need (0 for none).</summary>
    public double Render(IReadOnlyList<PaletteRowView> rows, int selected)
    {
        _shown = rows;
        _rows.Children.Clear();
        _slots.Clear();
        double height = 0;

        // Before anything is typed the leading items are recent picks: a rail of tiles, not a list.
        int rail = 0;
        while (rail < rows.Count && rail < 3 && rows[rail].Item.Hint == "Recent" && rows[rail].Control is null) rail++;
        RailCount = rail;
        if (rail > 0)
        {
            _rows.Children.Add(BuildRail(rows, rail));
            height += RailLabelHeight + RailTileHeight + RailGap;
        }

        for (int i = rail; i < rows.Count; i++)
        {
            var row = Build(rows[i], i);
            _rows.Children.Add(row);
            height += row.Height;
        }
        Select(selected);
        return rows.Count == 0 ? 0 : height + TopPad + BottomPad + 5;
    }

    /// <summary>Lights a row (-1: none, and then the web row says Enter, since Enter searches as before).</summary>
    public void Select(int selected)
    {
        int enterRow = selected >= 0 ? selected : _shown.Count > 0 && _shown[^1].Item.Kind == PaletteKind.Web ? _shown.Count - 1 : -1;
        for (int i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            bool on = i == selected;
            if (slot.Tile)
            {
                slot.Frame.BorderBrush = on ? Brush("WallAccent") : Brush("WallHairline");
                slot.Frame.Background = Brush(on ? "WallRaised" : "WallTile");
            }
            else
            {
                slot.Frame.BorderBrush = on ? Brush("WallAccent") : Brushes.Transparent;
                slot.Frame.Background = on ? Brush("WallTile") : Brushes.Transparent;
            }
            AutomationProperties.SetItemStatus(slot.Frame, on ? "Selected" : "");
            if (slot.Hint is null || slot.HintBox is null) continue;
            var item = _shown[i].Item;
            bool enter = i == enterRow;
            slot.Hint.Text = enter ? EnterHint(item) : item.Hint;
            slot.Hint.Foreground = Brush(enter ? "WallText" : "WallText2");
            // The row Enter will run wears a keycap; every other hint is plain grey text.
            slot.HintBox.BorderBrush = enter ? Brush("WallHairline") : Brushes.Transparent;
        }
    }

    static string EnterHint(PaletteItem item) => item.Kind switch
    {
        PaletteKind.File => "Enter · Ctrl+Enter folder",
        _ when item.Tag is PaletteEntry { EnterHint: { } hint } => hint(),
        _ when item.Tag is PaletteEntry { Adjustable: true } => "← → · Enter",
        _ => "Enter",
    };

    FrameworkElement BuildRail(IReadOnlyList<PaletteRowView> rows, int count)
    {
        var panel = new StackPanel();
        var label = WallParts.Label("Recent");
        label.HorizontalAlignment = HorizontalAlignment.Left;
        label.Margin = new Thickness(10, 4, 0, 0);
        label.Height = RailLabelHeight - 4;
        label.VerticalAlignment = VerticalAlignment.Top;
        panel.Children.Add(label);

        var tiles = new Grid { Height = RailTileHeight, Margin = new Thickness(2, 0, 2, RailGap) };
        for (int i = 0; i < 3; i++)
        {
            if (i > 0) tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        for (int i = 0; i < count; i++)
        {
            var tile = BuildTile(rows[i], i);
            Grid.SetColumn(tile, i * 2);
            tiles.Children.Add(tile);
        }
        panel.Children.Add(tiles);
        return panel;
    }

    Border BuildTile(PaletteRowView view, int index)
    {
        var item = view.Item;
        var icon = IconBox(view, 26, 22);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.VerticalAlignment = VerticalAlignment.Top;

        var title = WallParts.Text(item.Title, 12.5, FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Bottom;

        var content = new Grid { Margin = new Thickness(10, 9, 10, 8) };
        content.Children.Add(icon);
        content.Children.Add(title);

        var frame = Frame(content, index, WallParts.SmallRadius);
        frame.Height = RailTileHeight;
        frame.ToolTip = item.Subtitle.Length > 0 ? $"{item.Title} · {item.Subtitle}" : item.Title;
        AutomationProperties.SetName(frame, $"Recent: {item.Title}");
        _slots.Add(new Slot(frame, title, icon, 22, null, null, Tile: true));
        return frame;
    }

    Border Build(PaletteRowView view, int index)
    {
        var item = view.Item;
        bool tall = view.Control is not null;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = IconBox(view, 28, 24);
        icon.VerticalAlignment = tall ? VerticalAlignment.Top : VerticalAlignment.Center;
        icon.Margin = new Thickness(0, tall ? 8 : 0, 0, 0);
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        var title = WallParts.Text(item.Title, 13.5, FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Top;
        text.Children.Add(title);
        if (tall)
        {
            view.Control!.Margin = new Thickness(0, 6, 0, 0);
            if (view.Control.Parent is Panel old) old.Children.Remove(view.Control);
            text.Children.Add(view.Control);
        }
        else if (item.Subtitle.Length > 0)
        {
            var sub = WallParts.Text(item.Subtitle, 11.5, FontWeights.Normal, "WallText2");
            sub.Margin = new Thickness(0, 1, 0, 0);
            text.Children.Add(sub);
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var hint = WallParts.Label("", size: 10);
        hint.Tracking = 0.08;
        var hintBox = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            Padding = new Thickness(7, 3, 7, 3),
            VerticalAlignment = tall ? VerticalAlignment.Top : VerticalAlignment.Center,
            Margin = new Thickness(0, tall ? 10 : 0, 0, 0),
            Child = hint,
        };
        Grid.SetColumn(hintBox, 2);
        grid.Children.Add(hintBox);

        var frame = Frame(grid, index, WallParts.SmallRadius);
        frame.Height = tall ? ControlRowHeight : RowHeight;
        frame.Padding = new Thickness(8, 0, 8, 0);
        AutomationProperties.SetName(frame, item.Subtitle.Length > 0 ? $"{item.Title}, {item.Subtitle}" : item.Title);
        _slots.Add(new Slot(frame, title, icon, 24, hint, hintBox, Tile: false));
        return frame;
    }

    /// <summary>The app/file icon, or the row's glyph on a small tile square until (or unless) an icon loads.</summary>
    static Border IconBox(PaletteRowView view, double box, double image) => new()
    {
        Width = box,
        Height = box,
        CornerRadius = new CornerRadius(8),
        Background = view.Icon is null ? Brush("WallRaised") : Brushes.Transparent,
        Child = view.Icon is { } img
            ? new Image { Source = img, Width = image, Height = image, Stretch = Stretch.Uniform }
            : WallParts.Glyph(view.Glyph, box > 27 ? 14 : 13),
    };

    /// <summary>A clickable row or tile; hover selects it only after the pointer really moves.</summary>
    Border Frame(UIElement child, int index, double radius)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(radius),
            BorderThickness = new Thickness(Outline),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = child,
        };
        AutomationProperties.SetAutomationId(border, $"PaletteRow{index}");
        border.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            RowInvoked?.Invoke(index, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
        };
        border.MouseMove += (_, e) =>
        {
            var p = e.GetPosition(this);
            if (_lastMouse is { } last && (last - p).Length < 2) return;
            bool first = _lastMouse is null;
            _lastMouse = p;
            if (!first) RowHovered?.Invoke(index);
        };
        return border;
    }

    /// <summary>
    /// Changes one row's title in place. A slider being dragged lives in that row; rebuilding the rows
    /// would pull it out of the tree and drop the drag.
    /// </summary>
    public void SetTitle(int index, string title)
    {
        if (index >= 0 && index < _slots.Count) _slots[index].Title.Text = title;
    }

    /// <summary>
    /// Swaps a row's glyph for the real icon once it has loaded, in place: same box, so nothing moves,
    /// and no rebuild (which would interrupt a slider drag).
    /// </summary>
    public void SetIcon(int index, ImageSource icon)
    {
        if (index < 0 || index >= _slots.Count) return;
        var slot = _slots[index];
        slot.IconBox.Background = Brushes.Transparent;
        slot.IconBox.Child = new Image { Source = icon, Width = slot.IconSize, Height = slot.IconSize, Stretch = Stretch.Uniform };
    }

    /// <summary>Forget the pointer position, so the first move after the palette opens isn't read as a hover.</summary>
    public void ResetPointer() => _lastMouse = null;

    internal static Brush Brush(string key) => Ui.Tokens.Res(key);
}

/// <summary>A thin slider (0–1) that never takes focus; drag or click anywhere on the track. A white meter on a dark track.</summary>
internal sealed class MiniSlider : Grid
{
    readonly Border _fill;
    readonly Border _thumb;
    double _value;

    public MiniSlider()
    {
        Height = 16;
        Focusable = false;
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = PaletteView.Brush("WallTrack"), VerticalAlignment = VerticalAlignment.Center };
        _fill = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Background = PaletteView.Brush("WallText"),
        };
        _thumb = new Border
        {
            Width = 12,
            Height = 12,
            CornerRadius = new CornerRadius(6),
            Background = PaletteView.Brush("WallText"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Children.Add(track);
        Children.Add(_fill);
        Children.Add(_thumb);
        SizeChanged += (_, _) => Layout();
        MouseLeftButtonDown += (_, e) =>
        {
            CaptureMouse();
            FromPointer(e);
            e.Handled = true;
        };
        MouseMove += (_, e) =>
        {
            if (IsMouseCaptured) FromPointer(e);
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (IsMouseCaptured) ReleaseMouseCapture();
            e.Handled = true;
        };
    }

    /// <summary>Raised for user changes only (drag, click, arrow keys through the palette).</summary>
    public event Action<double>? ValueChanged;

    public double Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, 0, 1);
            Layout();
        }
    }

    public void Nudge(double delta)
    {
        Value = _value + delta;
        ValueChanged?.Invoke(_value);
    }

    void FromPointer(MouseEventArgs e)
    {
        if (ActualWidth <= 0) return;
        Value = e.GetPosition(this).X / ActualWidth;
        ValueChanged?.Invoke(_value);
    }

    void Layout()
    {
        double w = Math.Max(0, ActualWidth);
        _fill.Width = w * _value;
        _thumb.Margin = new Thickness(Math.Clamp(w * _value - 6, 0, Math.Max(0, w - 12)), 0, 0, 0);
    }
}

/// <summary>An on/off switch: white when on (like the Wall's pressed controls), a dark track when off.</summary>
internal sealed class MiniSwitch : Border
{
    readonly Border _knob;
    bool? _on;

    public MiniSwitch()
    {
        Width = 36;
        Height = 20;
        CornerRadius = new CornerRadius(10);
        Focusable = false;
        Cursor = Cursors.Hand;
        HorizontalAlignment = HorizontalAlignment.Left;
        _knob = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Child = _knob;
        State = null;
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_on is { } on) Toggled?.Invoke(!on);
        };
    }

    public event Action<bool>? Toggled;

    /// <summary>Null while unknown (dimmed).</summary>
    public bool? State
    {
        get => _on;
        set
        {
            _on = value;
            Opacity = value is null ? 0.4 : 1;
            Background = PaletteView.Brush(value == true ? "WallText" : "WallTrack");
            _knob.Background = PaletteView.Brush(value == true ? "WallGround" : "WallText2");
            _knob.Margin = new Thickness(value == true ? 19 : 3, 0, 0, 0);
            AutomationProperties.SetItemStatus(this, value switch { true => "On", false => "Off", _ => "Unknown" });
        }
    }
}
