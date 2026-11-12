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
    WallDetail? _shelfDetail;
    Point _dragStart;
    ShelfItem? _pressed;

    WallDetail BuildShelfDetail()
    {
        var d = new WallDetail(Fields.List(), "", "Shelf");
        _shelfRows = new StackPanel();
        d.Body.Children.Add(WallDetail.Scroll(_shelfRows));
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
        int n = _s.Shelf.Items.Count;
        if (_shelfCount is not null)
        {
            _shelfCount.Text = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _shelfCount.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        if (_shelfDetail is not null) FillShelf();
    }

    void FillShelf()
    {
        if (_s is null || _shelfDetail is not { } d || _shelfRows is null) return;
        var items = _s.Shelf.Items;
        int n = items.Count;
        d.Kicker.Text = n == 0 ? "Shelf · empty" : $"Shelf · {(n == 1 ? "1 file" : $"{n} files")}";
        d.Line.Text = n == 0 ? "Drop files on the notch to keep them here" : "Drag in to park files, drag out to drop them anywhere";
        d.Art.Children.OfType<StackPanel>().ToList().ForEach(s => d.Art.Children.Remove(s));
        d.ArtBottomLeft(CardKit.Poster(n.ToString(System.Globalization.CultureInfo.InvariantCulture), 96), Spaced(CardKit.Label(n == 1 ? "File" : "Files")));
        d.Corner = n > 0 ? CardKit.Pill("Clear", "Clear the shelf", () => _s?.Shelf.Clear()) : null;

        _shelfRows.Children.Clear();
        if (n == 0)
        {
            _shelfRows.Children.Add(Empty("Nothing parked yet"));
            return;
        }
        foreach (var item in items) _shelfRows.Children.Add(ShelfRow(item));
    }

    static FrameworkElement Spaced(FrameworkElement e)
    {
        e.Margin = new Thickness(0, 8, 0, 0);
        return e;
    }

    static TextBlock Empty(string text)
    {
        var t = CardKit.Text(text, 13, FontWeights.Medium, secondary: true);
        t.Margin = new Thickness(0, 8, 0, 0);
        t.HorizontalAlignment = HorizontalAlignment.Left;
        return t;
    }

    FrameworkElement ShelfRow(ShelfItem item)
    {
        var icon = new Image { Width = 20, Height = 20, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        icon.SetBinding(Image.SourceProperty, new Binding(nameof(ShelfItem.Icon)) { Source = item, Mode = BindingMode.OneWay });
        var name = CardKit.Text(item.Name, 13.5, FontWeights.SemiBold);
        var meta = CardKit.Text("", 12, FontWeights.Medium, secondary: true);
        meta.SetBinding(TextBlock.TextProperty, new Binding(nameof(ShelfItem.Details)) { Source = item, Mode = BindingMode.OneWay });
        var remove = CardKit.Quiet("Remove from shelf", CardKit.Icon(CardKit.Paths.Close, CardKit.Ink, 10, 2.4), () => _s?.Shelf.Remove(item), 22);
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
                menu("Show in folder", () => Process.Start(Interop.Native.ExplorerPath, $"/select,\"{item.Path}\""));
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
    WallDetail? _alertsDetail;

    WallDetail BuildAlertsDetail()
    {
        var d = new WallDetail(Fields.List(), "", "Alerts");
        _alertRows = new StackPanel();
        d.Body.Children.Add(WallDetail.Scroll(_alertRows));
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
        d.Kicker.Text = n.History.Count == 0 ? "Alerts" : $"Alerts · {fresh} new";
        d.Line.Text = !n.IsAvailable ? n.Problem ?? "Notifications are starting…"
            : PrivacyState.Presenting ? "Messages are hidden while you share your screen"
            : n.History.Count == 0 ? "Nothing waiting" : "Click one to open its app";
        d.Art.Children.OfType<StackPanel>().ToList().ForEach(s => d.Art.Children.Remove(s));
        d.ArtBottomLeft(CardKit.Poster(fresh.ToString(System.Globalization.CultureInfo.InvariantCulture), 96), Spaced(CardKit.Label("New")));
        d.Corner = n.History.Count > 0 ? CardKit.Pill("Clear all", "Clear all notifications", () => _s?.Notifications.ClearHistory()) : null;
    }

    /// <summary>
    /// Rebuild the alert rows so their text (and "5m" ages) is read again: whether the text shows depends on
    /// <see cref="PrivacyState"/>, which isn't something a row can watch.
    /// </summary>
    void RebuildAlertRows()
    {
        if (_s is null || _alertRows is null) return;
        RefreshAlertsHeader();
        _alertRows.Children.Clear();
        var history = _s.Notifications.History;
        if (history.Count == 0)
        {
            // The line above already says why when notifications aren't available.
            if (_s.Notifications.IsAvailable) _alertRows.Children.Add(Empty("All caught up"));
            return;
        }
        foreach (var n in history) _alertRows.Children.Add(AlertRow(n));
    }

    FrameworkElement AlertRow(Notifications.ShammyNotification n)
    {
        var icon = new Grid { Width = 22, Height = 22 };
        icon.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = CardKit.PillBg });
        if (n.Icon is { } src)
        {
            var img = new Image { Source = src, Width = 18, Height = 18 };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            icon.Children.Add(img);
        }
        var text = new TextBlock { FontFamily = CardKit.Font, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines.Add(CardKit.Run(n.ListTitle, FontWeights.SemiBold, CardKit.Ink));
        if (n.ListDetail.Length > 0)
            text.Inlines.Add(CardKit.Run("  " + n.ListDetail, FontWeights.Medium, CardKit.Ink2));
        var time = CardKit.Text(n.TimeText, 12, FontWeights.Medium, secondary: true);
        var dismiss = CardKit.Quiet("Dismiss", CardKit.Icon(CardKit.Paths.Close, CardKit.Ink, 10, 2.4), () => _s?.Notifications.History.Remove(n), 22);
        dismiss.Visibility = Visibility.Hidden;
        dismiss.Margin = new Thickness(10, 0, 0, 0);
        var row = WallDetail.ListRow(icon, text, CardKit.Row(0, time, dismiss), 36);
        row.Cursor = Cursors.Hand;
        CardKit.Name(row, $"{n.ListTitle}, {n.ListDetail}, {n.TimeText}. Open");
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
                menu("Dismiss", () => _s?.Notifications.History.Remove(n));
            });
            e.Handled = true;
        };
        return row;
    }

    /// <summary>What the Alerts view lists right now, rows separated by " || " (test pipe; replies are one line).</summary>
    internal string DescribeAlertRows() => _s is null ? "" : string.Join(" || ",
        _s.Notifications.History.Select(n => $"{n.ListTitle} | {n.ListDetail} | {n.TimeText}"));

    // ---- Now playing (no music module) ----------------------------------------------------------

    WallTile? _npTile;
    TrackedText? _npState, _npPoster;
    TextBlock? _npTitle, _npArtist;
    ContentControl? _npPlay;

    /// <summary>A plain music tile for while the Music module is off: what's playing, and play/pause.</summary>
    NotchCard BuildNowPlaying()
    {
        var tile = new WallTile();
        _npTile = tile;
        _npState = CardKit.Label("Now playing", null, 10);
        var pill = new Border { Background = CardKit.Hex("#990B0B0D"), CornerRadius = new CornerRadius(999), Padding = new Thickness(8, 4, 8, 4), Child = _npState };
        tile.Art.Children.Add(CardKit.At(pill, HorizontalAlignment.Left, VerticalAlignment.Top, 12, 12));
        _npPoster = CardKit.Poster("", 24);
        tile.Rest.Children.Add(WallTile.BottomLeft(_npPoster));
        var shade = new Border { Height = 96, VerticalAlignment = VerticalAlignment.Bottom, Background = MusicShade() };
        tile.Lit.Children.Add(shade);
        _npTitle = CardKit.Title("", 16);
        _npArtist = CardKit.Text("", 11.5, FontWeights.Medium);
        _npArtist.Foreground = Ui.Tokens.InkOnArt;
        _npPlay = new ContentControl { Focusable = false, VerticalAlignment = VerticalAlignment.Center };
        var words = CardKit.Column(0, _npTitle, _npArtist);
        var line = new DockPanel { Margin = new Thickness(12, 0, 10, 12), VerticalAlignment = VerticalAlignment.Bottom };
        DockPanel.SetDock(_npPlay, Dock.Right);
        line.Children.Add(_npPlay);
        line.Children.Add(words);
        tile.Lit.Children.Add(line);
        return new NotchCard
        {
            Id = "nowplaying",
            Title = "Now playing",
            Width = 340,
            Order = 20,
            Compact = tile,
            Detail = () => new Grid(),
            IsRelevant = () => _s?.Media.HasSession == true,
            Live = () => _s?.Media.IsPlaying == true ? 50 : 0,
            Peek = () => _s?.Media is { HasSession: true } m
                ? new WallPeek(m.IsPlaying ? "Now playing" : "Paused", m.Title, m.Artist, Fields.Music(ArtPalette.Field(m.Art)),
                    new WallAction(m.IsPlaying ? "Pause" : "Play", m.TogglePlayPause, Stays: true, Playing: m.IsPlaying))
                : null,
        };
    }

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

    void RefreshNowPlaying()
    {
        if (_s is null || _npTile is null) return;
        var m = _s.Media;
        if (!m.HasSession) return;
        _npTile.Field = Fields.Music(ArtPalette.Field(m.Art));
        _npState!.Text = m.IsPlaying ? "Now playing" : "Paused";
        _npPoster!.Text = m.Title;
        _npTitle!.Text = m.Title;
        _npArtist!.Text = m.Artist;
        _npPlay!.Content = CardKit.Primary(m.IsPlaying ? $"Pause {m.Title}" : $"Play {m.Title}", CardKit.PlayPause(m.IsPlaying, 11), () => _s?.Media.TogglePlayPause());
    }
}
