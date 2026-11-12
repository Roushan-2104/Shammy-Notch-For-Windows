using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Infrastructure;
using Shammy.Modules.Kit;

namespace Shammy.Modules.Connect;

/// <summary>
/// What a finished download's pill opens: the file (icon, name, size, folder) and Open, Show in folder, Add to
/// shelf, Send to phone. Built with the card kit; each action closes the card except Add to shelf, which ticks.
/// </summary>
internal sealed class DownloadCard : Grid
{
    public const double CardWidth = Ui.Tokens.PopupWidth, CardHeight = 118;

    readonly string _path;
    readonly Action _close;

    public DownloadCard(string path, Shelf.ShelfStore shelf, Action close)
    {
        _path = path;
        _close = close;
        Width = CardWidth;
        Height = CardHeight;
        Margin = new Thickness(0);
        Background = Brushes.Transparent;

        var body = new DockPanel { Margin = new Thickness(16, 14, 16, 14) };
        Children.Add(body);

        // Top: icon, name, size · folder.
        var head = new DockPanel();
        var icon = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Background = CardKit.Raised, Margin = new Thickness(0, 0, 12, 0) };
        if (Shelf.ShellIcons.Get(path, 64) is { } image) icon.Child = new Image { Source = image, Width = 28, Height = 28 };
        else icon.Child = CardKit.Glyph("", 16, CardKit.Text2);
        head.Children.Add(icon);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(Views.WallParts.PopupTitle(Path.GetFileName(path)));
        words.Children.Add(CardKit.Label(Details(path), 11.5, FontWeights.Medium, CardKit.Text2));
        head.Children.Add(words);
        DockPanel.SetDock(head, Dock.Top);
        body.Children.Add(head);

        // Bottom: the four actions.
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(Action("Open", "", () => Run(() => Open(path))));
        actions.Children.Add(Action("Show in folder", "", () => Run(() => Process.Start(Interop.Native.ExplorerPath, $"/select,\"{path}\""))));
        Border? shelve = null;
        shelve = Action("Add to shelf", "", () =>
        {
            if (!File.Exists(path)) return;
            shelf.Add(new[] { path });
            if (shelve?.Child is StackPanel row && row.Children.OfType<TextBlock>().LastOrDefault() is { } label) label.Text = "On the shelf";
        });
        actions.Children.Add(shelve);
        if (Links.Phone is { } phone)
            actions.Children.Add(Action("Send to phone", "", () => Run(() => _ = phone.SendAsync(new[] { path }))));
        body.Children.Add(actions);
    }

    static Border Action(string text, string glyph, Action onClick)
    {
        var b = CardKit.PillButton(text, onClick, 28, glyph, fontSize: 11.5);
        b.Margin = new Thickness(0, 0, 6, 0);
        return b;
    }

    void Run(Action action)
    {
        _close();
        if (!File.Exists(_path)) return;
        try { action(); }
        catch (Exception ex) { Log.Warn("A download card action failed", ex); }
    }

    static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// <summary>"24.1 MB · Downloads".</summary>
    static string Details(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var folder = Path.GetFileName(Path.GetDirectoryName(path) ?? "") ?? "";
            var size = info.Exists ? Core.DropActions.DropPlan.Size(info.Length) : "Moved or deleted";
            return folder.Length > 0 ? $"{size} · {folder}" : size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "";
        }
    }
}
