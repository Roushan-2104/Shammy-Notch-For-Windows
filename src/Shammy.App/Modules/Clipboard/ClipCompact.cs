using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Views;

namespace Shammy.Modules.ClipboardHistory;

/// <summary>
/// "Just copied" preview: a copy glyph (or thumbnail) on the left, the text or image size on the right, in white on
/// the notch (no accent: it is news, not a control). While the screen is shared it only says "Copied", with no thumbnail.
/// </summary>
internal sealed class CopyPreview : ICompactContent
{
    const double MaxEar = 160;

    string _text;
    ImageSource? _thumb;
    TextBlock? _label;
    ContentControl? _left;

    public CopyPreview(string text, ImageSource? thumb)
    {
        _text = text;
        _thumb = thumb;
    }

    static bool Private => Infrastructure.PrivacyState.Presenting;

    string Shown => Shammy.Core.Connect.PrivacyText.CopyPreview(_text, Private);

    public double Build(ContentControl left, ContentControl right)
    {
        _left = left;
        _label = WallParts.Text(Shown, 12.5, FontWeights.SemiBold);
        _label.MaxWidth = MaxEar;
        right.Content = _label;
        SetLeft();
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // Later in-place updates trim to this width rather than spilling into the centre cut-out.
        _label.MaxWidth = Math.Clamp(Math.Ceiling(_label.DesiredSize.Width), 24, MaxEar);
        return _label.MaxWidth;
    }

    /// <summary>Copies in quick succession update the pill in place instead of re-announcing.</summary>
    public void Set(string text, ImageSource? thumb)
    {
        _text = text;
        _thumb = thumb;
        if (_label is not null) _label.Text = Shown;
        SetLeft();
    }

    void SetLeft()
    {
        if (_left is null) return;
        _left.Content = _thumb is not null && !Private
            ? ClipImages.Tile(_thumb, 20, 20, 6)
            : WallParts.Glyph("", 15);
    }
}

/// <summary>A new screenshot: thumbnail on the left, a tracked "SCREENSHOT" and its size on the right. Click opens the card.</summary>
internal sealed class ScreenshotPreview : ICompactContent
{
    readonly ImageSource _thumb;
    readonly string _size;

    public ScreenshotPreview(ImageSource thumb, string size)
    {
        _thumb = thumb;
        _size = size;
    }

    public double Build(ContentControl left, ContentControl right)
    {
        left.Content = ClipImages.Tile(_thumb, 26, 20, 5);
        var title = WallParts.Label("Screenshot", "WallText", 10);
        title.Tracking = 0.1;
        var size = WallParts.Text(_size, 10.5, FontWeights.Normal, "WallText2");
        title.HorizontalAlignment = size.HorizontalAlignment = HorizontalAlignment.Right;
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(title);
        stack.Children.Add(size);
        right.Content = stack;
        return 84;
    }
}
