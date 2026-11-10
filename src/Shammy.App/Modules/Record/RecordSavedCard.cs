using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shammy.Core.Record;
using Shammy.Views;
using Shammy.Views.Screens;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Shammy.Modules.Record;

/// <summary>
/// The card a finished recording gets, the video's twin of the screenshot card: a preview (click to play), the file
/// name, its length and size, and Open / Show in folder / Delete, then Send to phone / Add to shelf. Open is the one
/// accent pill.
/// </summary>
internal sealed class RecordSavedCard : Grid
{
    public const double CardWidth = 460, CardHeight = 168;
    const double BoxWidth = 160, BoxHeight = 90;

    internal sealed record Actions(Func<bool> Open, Func<bool> ShowInFolder, Func<bool>? SendToPhone, Func<bool> AddToShelf, Func<bool> Delete, Action Close);

    readonly Border _preview;

    public RecordSavedCard(RecordResult result, Actions actions)
    {
        Width = CardWidth;
        Height = CardHeight;
        Background = Brushes.Transparent;
        var path = result.Path ?? "";

        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BoxWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Children.Add(grid);

        _preview = new Border
        {
            Width = BoxWidth,
            Height = BoxHeight,
            CornerRadius = new CornerRadius(WallParts.SmallRadius),
            Background = WallParts.Res("WallTile"),
            BorderBrush = WallParts.Res("WallHairline"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.Hand,
            ToolTip = "Click to play",
            Child = WallParts.Glyph("", 26, "WallText2"),
        };
        AutomationProperties.SetName(_preview, "Recording preview. Click to play");
        _preview.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            actions.Open();
        };
        grid.Children.Add(_preview);

        var details = new StackPanel();
        SetColumn(details, 2);
        grid.Children.Add(details);

        var label = WallParts.Label(RecordText.SavedLabel(result.Reason));
        label.Margin = new Thickness(0, 0, 26, 0);
        label.HorizontalAlignment = HorizontalAlignment.Left;
        details.Children.Add(label);

        var name = WallParts.Text(System.IO.Path.GetFileName(path), 14, FontWeights.Bold);
        name.Margin = new Thickness(0, 4, 0, 0);
        name.ToolTip = path;
        details.Children.Add(name);

        var facts = WallParts.Text(RecordText.Details(result.Length, result.Bytes), 12, FontWeights.Normal, "WallText2");
        facts.Margin = new Thickness(0, 2, 0, 10);
        details.Children.Add(facts);

        // Two even rows: what you do with the file, then where else it can go. Delete is the small bin beside them.
        var first = new WrapPanel();
        var second = new WrapPanel();
        details.Children.Add(first);
        details.Children.Add(second);
        Add(first, "", "Open", null, null, actions.Open, primary: true);
        Add(first, "", "Show in folder", null, null, actions.ShowInFolder);
        var bin = WallParts.Icon("", "Delete", () => actions.Delete(), 26, 11);
        bin.Margin = new Thickness(0, 0, 4, 4);
        first.Children.Add(bin);
        if (actions.SendToPhone is { } phone) Add(second, "", "Send to phone", null, null, phone);
        Add(second, "", "Add to shelf", "", "On the shelf", actions.AddToShelf);

        var close = WallParts.Icon("", "Close", actions.Close, 24, 10);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Margin = new Thickness(0, -4, -4, 0);
        SetColumn(close, 2);
        grid.Children.Add(close);

        AutomationProperties.SetName(this, $"{RecordText.SavedLabel(result.Reason)}. {System.IO.Path.GetFileName(path)}, {RecordText.Details(result.Length, result.Bytes)}");
        if (path.Length > 0) _ = LoadPreviewAsync(path);
    }

    /// <summary>A pill; on success it can switch to a done state (glyph + label) as feedback.</summary>
    static void Add(WrapPanel buttons, string glyph, string label, string? doneGlyph, string? doneLabel, Func<bool> run, bool primary = false)
    {
        TextBlock icon = null!, text = null!;
        Button pill = null!;
        pill = WallParts.GlyphButton(glyph, label, () =>
        {
            if (!run() || doneLabel is null) return;
            icon.Text = doneGlyph;
            text.Text = doneLabel;
            AutomationProperties.SetName(pill, doneLabel);
        }, primary, out icon, out text, height: 26);
        pill.Margin = new Thickness(0, 0, 4, 4);
        buttons.Children.Add(pill);
    }

    /// <summary>The picture Explorer shows for the video; the film glyph stays if Windows has none.</summary>
    async Task LoadPreviewAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.VideosView, 320);
            if (thumbnail is null || thumbnail.Size == 0 || thumbnail.Type != ThumbnailType.Image) return;
            using var copy = new MemoryStream();
            await thumbnail.AsStreamForRead().CopyToAsync(copy);
            copy.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = copy;
            image.EndInit();
            image.Freeze();
            _preview.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            _preview.Child = null;
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Warn("[record] No preview for the recording", ex);
        }
    }
}

/// <summary>Why a recording could not start or be saved, in plain words, with OK (380 wide, like the start card).</summary>
internal sealed class RecordFailedCard : Grid
{
    public const double CardWidth = 380, CardHeight = 124;

    public RecordFailedCard(string? failure, Action close)
    {
        Width = CardWidth;
        Height = CardHeight;
        Background = Brushes.Transparent;

        var rows = new Grid { Margin = new Thickness(16, 14, 16, 16) };
        Children.Add(rows);

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        var title = ScreenKit.Display(RecordText.FailureTitle(failure), 17, Weight.ExtraBold, -0.015);
        title.HorizontalAlignment = HorizontalAlignment.Left;
        words.Children.Add(title);
        var why = ScreenKit.Para(RecordText.Failure(failure), 12.5, ScreenKit.Fg2, 17);
        why.Margin = new Thickness(0, 6, 0, 0);
        words.Children.Add(why);
        rows.Children.Add(words);

        var ok = ScreenKit.Secondary("OK", close, 30, 15, 12.5);
        ok.HorizontalAlignment = HorizontalAlignment.Right;
        ok.VerticalAlignment = VerticalAlignment.Bottom;
        rows.Children.Add(ok);

        AutomationProperties.SetName(this, $"{RecordText.FailureTitle(failure)}. {RecordText.Failure(failure)}");
    }
}
