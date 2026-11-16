using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Shammy.Core.Cards;
using Shammy.Infrastructure;
using Shammy.Modules;
using Shammy.Playback;
using Shammy.Shelf;
using Shammy.Views.Cards;

namespace Shammy.Views;

/// <summary>
/// The open notch's built-in views as Focus Wall list details — the Shelf (files parked on the notch, dragged out
/// to use) and Alerts (notification history) — plus the now-playing tile used while no module provides music.
/// </summary>
public partial class ExpandedView
{
    // ---- Shelf ----------------------------------------------------------------------------------

    StackPanel? _shelfRows;
    CardSheet? _shelfDetail;
    Button? _shelfClear;
    Point _dragStart;
    ShelfItem? _pressed;

    /// <summary>The list cards (Shelf, Alerts) are 600 wide; their rows cap at about five, then scroll.</summary>
    const double ListSheetWidth = 600, ListMaxHeight = 5 * 44;

    static ScrollViewer ListScroll(UIElement rows) => new()
    {
        Content = rows,
        MaxHeight = ListMaxHeight,
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Focusable = false,
    };

    CardSheet BuildShelfDetail()
    {
        var d = new CardSheet("Shelf", ListSheetWidth);
        _shelfClear = d.Action(Sheet.Cap("Clear", "Clear the shelf", () => _s?.Shelf.Clear()));
        _shelfRows = new StackPanel();
        d.Body.Children.Add(ListScroll(_shelfRows));
        _shelfDetail = d;
        d.Unloaded += (_, _) =>
        {
            if (_shelfDetail != d) return;
            _shelfDetail = null;
            _shelfRows = null;
        };
        FillShelf();
        return d;
    }

    void RefreshShelf()
    {
        if (_s is null) return;
        if (_shelfDetail is not null) FillShelf();
    }

    void FillShelf()
    {
        if (_s is null || _shelfDetail is not { } d || _shelfRows is null) return;
        var items = _s.Shelf.Items;
        int n = items.Count;
        d.Status = n == 0 ? null : n == 1 ? "1 file · drag out to use" : $"{n} files · drag out to use";
        if (_shelfClear is not null) _shelfClear.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;

        _shelfRows.Children.Clear();
        if (n == 0)
        {
            _shelfRows.Children.Add(EmptyLine("Nothing on the shelf yet.", "Drop files on the notch to park them here."));
            return;
        }
        foreach (var item in items) _shelfRows.Children.Add(ShelfRow(item));
    }

    /// <summary>An empty list card's one line: a bold start and a quiet rest.</summary>
    static FrameworkElement EmptyLine(string bold, string rest)
    {
        var line = Sheet.Meta("", 14);
        line.Inlines.Add(CardKit.Run(bold + " ", FontWeights.Bold, CardKit.Ink));
        line.Inlines.Add(new System.Windows.Documents.Run(rest));
        line.Margin = new Thickness(2, 0, 0, 2);
        return line;
    }

    FrameworkElement ShelfRow(ShelfItem item)
    {
        var icon = new Image { Width = 20, Height = 20, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        icon.SetBinding(Image.SourceProperty, new Binding(nameof(ShelfItem.Icon)) { Source = item, Mode = BindingMode.OneWay });
        var name = CardKit.Text(item.Name, 13.5, FontWeights.SemiBold);
        var meta = CardKit.Text("", 12, FontWeights.Medium, secondary: true);
        meta.SetBinding(TextBlock.TextProperty, new Binding(nameof(ShelfItem.Details)) { Source = item, Mode = BindingMode.OneWay });
        var remove = CardKit.CloseButton(() => _s?.Shelf.Remove(item), "Remove from shelf");
        remove.Visibility = Visibility.Hidden;
        remove.Margin = new Thickness(10, 0, 0, 0);
        var right = CardKit.Row(0, meta, remove);
        var row = WallDetail.ListRow(icon, name, right, 36);
        row.Cursor = Cursors.Hand;
        row.ToolTip = item.Path;
        CardKit.Name(row, $"{item.Name}. Drag out to use, double-click to open");
        row.MouseEnter += (_, _) => remove.Visibility = Visibility.Visible;
        row.MouseLeave += (_, _) => remove.Visibility = Visibility.Hidden;
        row.MouseLeftButtonDown += (_, e) =>
        {
            if (e.Handled) return;
            _pressed = item;
            _dragStart = e.GetPosition(this);
            if (e.ClickCount == 2)
            {
                OpenFile(item.Path);
                _pressed = null;
            }
        };
        row.MouseMove += (s, e) => DragOut(s, e, item);
        row.MouseLeftButtonUp += (_, _) => _pressed = null;
        row.MouseRightButtonUp += (_, e) =>
        {
            ShowMenu(row, menu =>
            {
                menu("Open", () => OpenFile(item.Path));
                menu("Show in folder", () => ShelfStore.ShowInFolder(item.Path));
                foreach (var action in ShelfActions.Where(a => a.AppliesTo?.Invoke(item) ?? true)) menu(action.Label, () => action.Run(item));
                menu("Remove from shelf", () => _s?.Shelf.Remove(item));
            });
            e.Handled = true;
        };
        return row;
    }

    /// <summary>A file dragged off a shelf row is a real file drop (copy, move or link) wherever it lands.</summary>
    void DragOut(object sender, MouseEventArgs e, ShelfItem item)
    {
        if (_pressed != item || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pressed = null;
        var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
        ShelfDragging?.Invoke(true);
        try
        {
            var result = DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            if (result == DragDropEffects.Move) _s?.Shelf.Remove(item);
        }
        finally
        {
            ShelfDragging?.Invoke(false);
        }
    }

    static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Opening a shelf item failed", ex); }
    }

    /// <summary>Keeps the notch open while a card's own drag or menu is up, as a shelf drag does; pair true with false.</summary>
    internal void HoldOpen(bool on) => ShelfDragging?.Invoke(on);

    /// <summary>A right-click menu that keeps the notch open while it's up (the pointer leaves the notch to reach it).</summary>
    void ShowMenu(UIElement target, Action<Action<string, Action>> fill)
    {
        var menu = new ContextMenu { PlacementTarget = target };
        fill((header, run) =>
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => run();
            menu.Items.Add(mi);
        });
        ShelfDragging?.Invoke(true);
        menu.Closed += (_, _) => ShelfDragging?.Invoke(false);
        menu.IsOpen = true;
    }

    // ---- Alerts ---------------------------------------------------------------------------------

    StackPanel? _alertRows;
    CardSheet? _alertsDetail;
    /// <summary>All, Phone or Laptop: whose notifications the list shows.</summary>
    string _alertFilter = "All";

    bool Shown(Notifications.ShammyNotification x) => _alertFilter == "All" || x.IsFromPhone == (_alertFilter == "Phone");
    readonly List<Button> _alertFilters = new();
    Button? _alertsClear;
    TextBlock? _alertsNote;

    CardSheet BuildAlertsDetail()
    {
        var d = new CardSheet("Alerts", ListSheetWidth);
        _alertFilters.Clear();
        foreach (var f in new[] { "All", "Phone", "Laptop" })
        {
            var b = d.Action(Sheet.Cap(f, $"Show {f.ToLowerInvariant()} notifications", () => { _alertFilter = f; RebuildAlertRows(); }));
            b.Tag = f;
            _alertFilters.Add(b);
        }
        _alertsClear = d.Action(Sheet.Cap("Clear all", "Clear the notifications shown", () =>
        {
            if (_s is not { } s) return;
            if (_alertFilter == "All") s.Notifications.ClearHistory();
            else foreach (var x in s.Notifications.History.Where(Shown).ToList()) s.Notifications.Dismiss(x);
        }));
        _alertRows = new StackPanel();
        _alertsNote = Sheet.Meta("", 13);
        _alertsNote.Margin = new Thickness(2, 0, 0, 10);
        var column = new StackPanel();
        column.Children.Add(_alertsNote);
        column.Children.Add(ListScroll(_alertRows));
        d.Body.Children.Add(column);
        _alertsDetail = d;
        d.Unloaded += (_, _) =>
        {
            if (_alertsDetail != d) return;
            _alertsDetail = null;
            _alertRows = null;
        };
        RefreshAlertsHeader();
        RebuildAlertRows();
        return d;
    }

    /// <summary>The count ("N new" since the view was last open, with what arrived while it's open), the note and Clear all.</summary>
    void RefreshAlertsHeader()
    {
        if (_s is null || _alertsDetail is not { } d) return;
        var n = _s.Notifications;
        int fresh = _openId == AlertsId ? _alertsNewAtOpen + n.UnreadCount : n.UnreadCount;
        d.Status = n.History.Count == 0 ? null : $"{fresh} new · click one to open its app";
        // Only something worth saying gets a line: not available yet, or hidden while sharing the screen.
        string? note = !n.IsAvailable ? n.Problem ?? "Notifications are starting…"
            : PrivacyState.Presenting ? "Messages are hidden while you share your screen" : null;
        if (_alertsNote is not null)
        {
            _alertsNote.Text = note ?? "";
            _alertsNote.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        }
        if (_alertsClear is not null) _alertsClear.Visibility = n.History.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // a filter whose source has run out goes back to All, so nothing new is hidden under it
        if (_alertFilter != "All" && !n.History.Any(x => x.IsFromPhone == (_alertFilter == "Phone"))) _alertFilter = "All";
        bool fromPhone = n.History.Any(x => x.IsFromPhone);
        foreach (var b in _alertFilters)
        {
            b.Visibility = fromPhone ? Visibility.Visible : Visibility.Collapsed;
            b.Opacity = (string?)b.Tag == _alertFilter ? 1 : 0.5;
        }
    }

    /// <summary>
    /// Rebuild the alert rows so their text (and "5m" ages) is read again: whether the text shows depends on
    /// <see cref="PrivacyState"/> and the "always hide message text" setting, which a row doesn't watch.
    /// </summary>
    void RebuildAlertRows()
    {
        if (_s is null || _alertRows is null) return;
        RefreshAlertsHeader();
        _alertRows.Children.Clear();
        var history = _s.Notifications.History
            .Where(Shown).ToList();
        if (history.Count == 0)
        {
            // The line above already says why when notifications aren't available.
            if (_s.Notifications.IsAvailable) _alertRows.Children.Add(EmptyLine("All caught up.", "New notifications show up here."));
            return;
        }
        foreach (var n in history) _alertRows.Children.Add(AlertRow(n));
    }

    FrameworkElement AlertRow(Notifications.ShammyNotification n)
    {
        var icon = new Grid { Width = 22, Height = 22 };
        icon.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = CardKit.PillBg });
        if (n.IsFromPhone)
            icon.Children.Add(new TextBlock
            {
                Text = "\uE8EA", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, Foreground = CardKit.Ink2,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });
        else if (n.Icon is { } src)
        {
            var img = new Image { Source = src, Width = 18, Height = 18 };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            icon.Children.Add(img);
        }
        var (title, detail) = n.ListText(AlwaysHideText);
        var text = new TextBlock { FontFamily = CardKit.Font, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines.Add(CardKit.Run(title, FontWeights.SemiBold, CardKit.Ink));
        if (detail.Length > 0)
            text.Inlines.Add(CardKit.Run("  " + detail, FontWeights.Medium, CardKit.Ink2));
        var time = CardKit.Text(n.TimeText, 12, FontWeights.Medium, secondary: true);
        var dismiss = CardKit.CloseButton(() => _s?.Notifications.Dismiss(n), "Dismiss");
        dismiss.Visibility = Visibility.Hidden;
        dismiss.Margin = new Thickness(10, 0, 0, 0);
        var row = WallDetail.ListRow(icon, text, CardKit.Row(0, time, dismiss), 36);
        row.Cursor = Cursors.Hand;
        CardKit.Name(row, $"{title}, {detail}, {n.TimeText}. Open");
        row.MouseEnter += (_, _) => dismiss.Visibility = Visibility.Visible;
        row.MouseLeave += (_, _) => dismiss.Visibility = Visibility.Hidden;
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            NotificationOpened?.Invoke(n);
        };
        row.MouseRightButtonUp += (_, e) =>
        {
            ShowMenu(row, menu =>
            {
                menu("Open", () => NotificationOpened?.Invoke(n));
                menu("Remind me in 1 hour", () => NotificationRemindRequested?.Invoke(n));
                menu("Dismiss", () => _s?.Notifications.Dismiss(n));
            });
            e.Handled = true;
        };
        return row;
    }

    /// <summary>What the Alerts view lists right now, rows separated by " || " (test pipe; replies are one line).</summary>
    internal string DescribeAlertRows() => _s is null ? "" : string.Join(" || ",
        _s.Notifications.History.Select(n => (Text: n.ListText(AlwaysHideText), n.TimeText)).Select(r => $"{r.Text.Title} | {r.Text.Detail} | {r.TimeText}"));

    /// <summary>The user's "Always hide message text" setting: the rows show who, never what.</summary>
    bool AlwaysHideText => _s?.Settings.AlwaysHideNotificationText == true;

    // ---- Now playing (no music module) ----------------------------------------------------------

    /// <summary>A peek-only music card for while the Music module is off: what's playing, and play/pause.</summary>
    NotchCard BuildNowPlaying() => new()
    {
        Id = "nowplaying",
        Title = "Now playing",
        Width = 340,
        Order = 20,
        Compact = new Border(),
        Detail = () => new Grid(),
        IsRelevant = () => _s?.Media.HasSession == true,
        Live = () => _s?.Media.IsPlaying == true ? 50 : 0,
        Peek = () => _s?.Media is { HasSession: true } m
            ? new WallPeek(m.IsPlaying ? "Now playing" : "Paused", m.Title, m.Artist, Fields.Music(ArtPalette.Field(m.Art)),
                new WallAction(m.IsPlaying ? "Pause" : "Play", m.TogglePlayPause, Stays: true, Playing: m.IsPlaying))
            : null,
    };

    /// <summary>The dark fade under a music tile's words, so they read on any cover.</summary>
    internal static Brush MusicShade()
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0, 11, 11, 13), 0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(235, 11, 11, 13), 0.42));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(235, 11, 11, 13), 1));
        b.Freeze();
        return b;
    }
}
