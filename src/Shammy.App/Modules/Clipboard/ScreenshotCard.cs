using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shammy.Core.Clipboard;

namespace Shammy.Modules.ClipboardHistory;

/// <summary>A screenshot Shammy noticed, from a file in the Screenshots folder or a snip on the clipboard.</summary>
internal sealed class Capture
{
    public Capture(BitmapSource preview, int width, int height, DateTime at)
    {
        Preview = preview;
        Width = width;
        Height = height;
        At = at;
    }

    /// <summary>A down-scaled copy for files; the image itself for clipboard snips.</summary>
    public BitmapSource Preview { get; }

    /// <summary>Full-resolution pixels, only for clipboard snips (files are re-read when needed).</summary>
    public BitmapSource? Full { get; init; }

    public byte[]? Png { get; set; }
    public string? Path { get; set; }

    /// <summary>Shammy wrote this file itself, so it may delete it. Files in the user's Screenshots folder are never deleted.</summary>
    public bool Owned { get; set; }

    public bool FromClipboard => Full is not null;
    public int Width { get; }
    public int Height { get; }
    public DateTime At { get; }
    public long Bytes { get; set; }
    public bool HasFile => Path is not null && File.Exists(Path);
}

/// <summary>
/// The card a screenshot opens: a preview that can be dragged out as a file (into a chat, a folder...)
/// and Copy / Copy text (OCR) / Search with Lens / Send to phone / Add to shelf / Open / Delete-or-Show-in-folder.
/// Focus Wall: the shot is the artwork, Copy is the accent pill, the rest are quiet.
/// </summary>
internal sealed class ScreenshotCard : Grid
{
    // Taller than the first version: seven actions wrap two to a row where they fit.
    public const double CardWidth = Ui.Tokens.PopupWidth, CardHeight = 232;
    const double BoxWidth = 204, BoxHeight = 200;

    readonly ClipboardModule _module;
    readonly Capture _capture;
    readonly Border _preview;
    readonly TextBlock _details;
    readonly WrapPanel _buttons = new();
    Point _downAt;
    bool _pressed;

    public ScreenshotCard(ClipboardModule module, Capture capture)
    {
        _module = module;
        _capture = capture;
        Margin = new Thickness(16);
        AllowDrop = true;
        // Swallow drags over the card: otherwise dragging the preview out starts over the notch and turns it into the shelf drop zone.
        DragEnter += BlockDrop;
        DragOver += BlockDrop;
        Drop += BlockDrop;

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BoxWidth) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        ColumnDefinitions.Add(new ColumnDefinition());

        double scale = Math.Min(BoxWidth / Math.Max(1, capture.Width), BoxHeight / Math.Max(1, capture.Height));
        _preview = ClipImages.Tile(capture.Preview, Math.Max(24, capture.Width * scale), Math.Max(24, capture.Height * scale), Views.WallParts.SmallRadius);
        // The screenshot is the artwork; a hairline keeps a white page from bleeding into the notch.
        _preview.BorderBrush = Views.WallParts.Res("WallHairline");
        _preview.BorderThickness = new Thickness(1);
        System.Windows.Automation.AutomationProperties.SetName(_preview, "Screenshot preview. Drag into a chat or folder, or click to open");
        _preview.HorizontalAlignment = HorizontalAlignment.Center;
        _preview.Cursor = Cursors.Hand;
        _preview.ToolTip = "Drag into a chat or folder · click to open";
        _preview.MouseLeftButtonDown += OnPreviewDown;
        _preview.MouseMove += OnPreviewMove;
        _preview.MouseLeftButtonUp += OnPreviewUp;
        Children.Add(_preview);

        var details = new StackPanel();
        SetColumn(details, 2);
        var title = Views.WallParts.PopupTitle("Screenshot");
        title.Margin = new Thickness(0, 0, 26, 0);
        _details = Views.WallParts.Text("", 12, FontWeights.Normal, "WallText2");
        _details.Margin = new Thickness(0, 2, 0, 10);
        details.Children.Add(title);
        details.Children.Add(_details);
        details.Children.Add(_buttons);
        Children.Add(details);

        var close = Views.Cards.CardKit.CloseButton(_module.CloseCard);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Margin = new Thickness(0, -2, -4, 0);
        SetColumn(close, 2);
        Children.Add(close);

        Refresh();
    }

    /// <summary>Rebuild the details and buttons (a file may have been saved or attached since).</summary>
    public void Refresh()
    {
        var parts = new List<string> { ClipText.Dimensions(_capture.Width, _capture.Height) };
        if (_capture.Bytes > 0) parts.Add(Views.CompactView.FormatBytes(_capture.Bytes));
        parts.Add(ClipText.Relative(_capture.At, DateTime.Now));
        _details.Text = string.Join(" · ", parts);
        _details.ToolTip = _capture.Path;

        _buttons.Children.Clear();
        AddButton("", "Copy", "", "Copied", _module.CopyCapture, primary: true);
        AddButton("", "Copy text", null, null, _module.CopyCaptureText);
        AddButton("", "Search with Lens", null, null, _module.SearchCaptureWithLens);
        AddButton("", "Send to phone", null, null, _module.SendCaptureToPhone);
        AddButton("", "Add to shelf", "", "On the shelf", _module.AddCaptureToShelf);
        AddButton("", "Open", null, null, _module.OpenCapture);
        if (_capture.HasFile && _capture.Owned) AddButton("", "Delete", null, null, _module.DeleteCapture);
        else if (_capture.HasFile) AddButton("", "Show in folder", null, null, _module.ShowCaptureInFolder);
    }

    /// <summary>A pill; on success it can switch to a done state (glyph + label) as feedback. Copy is the one accent pill.</summary>
    void AddButton(string glyph, string label, string? doneGlyph, string? doneLabel, Func<Capture, bool> run, bool primary = false)
    {
        TextBlock icon = null!, text = null!;
        Button pill = null!;
        pill = Views.WallParts.GlyphButton(glyph, label, () =>
        {
            if (!run(_capture) || doneLabel is null) return;
            icon.Text = doneGlyph;
            text.Text = doneLabel;
            System.Windows.Automation.AutomationProperties.SetName(pill, doneLabel);
        }, primary, out icon, out text, height: 26);
        pill.Margin = new Thickness(0, 0, 4, 4);
        _buttons.Children.Add(pill);
    }

    void OnPreviewDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = true;
        _downAt = e.GetPosition(this);
    }

    void OnPreviewMove(object sender, MouseEventArgs e)
    {
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _downAt;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pressed = false;
        // Chat apps and Explorer take a real file, so a clipboard-only snip is saved first.
        var path = _module.EnsureFile(_capture);
        if (path is null) return;
        Refresh();
        DragDrop.DoDragDrop(_preview, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy);
    }

    void OnPreviewUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        _module.OpenCapture(_capture);
    }

    static void BlockDrop(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }
}
