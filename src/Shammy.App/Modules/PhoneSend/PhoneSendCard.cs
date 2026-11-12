using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shammy.Core.PhoneSend;
using Shammy.Motion;
using Shammy.Views;

namespace Shammy.Modules.PhoneSend;

/// <summary>
/// The transfer card: QR code on the left (the artwork; dark-on-white so every scanner reads it); a tracked label,
/// the file in bold, instructions and then live progress on the right. Built in code like the compact views so the
/// module stays one folder of .cs files.
/// </summary>
internal sealed class PhoneSendCard : Grid
{
    public const double CardWidth = Ui.Tokens.PopupWidth, CardHeight = 230;
    const double Tile = 190;

    // Inverted (light-on-dark) codes look more at home in the notch, but plenty of phone scanners
    // (ZXing-based apps, older camera stacks) only read dark-on-light. Scanning has to just work.
    const bool InvertedQr = false;

    readonly TextBlock _hint, _url, _progressText, _percentText, _received;
    readonly Border _track, _fill;
    readonly StackPanel _waiting, _progress, _sent;
    readonly SpringValue _level = new(0, 0.001);
    IDisposable? _frames;

    public PhoneSendCard(string url, string fileName, long size)
    {
        Width = CardWidth;
        Height = CardHeight;
        Background = Brushes.Transparent;
        Focusable = true;
        FocusVisualStyle = null;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Tile + 20) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Children.Add(BuildTile(url));

        var right = new Grid { Margin = new Thickness(0, 18, 18, 18) };
        SetColumn(right, 2);
        for (int i = 0; i < 6; i++)
            right.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        Children.Add(right);

        // Header: a tracked label, plus close.
        var header = WallParts.Label("Send to phone");
        header.HorizontalAlignment = HorizontalAlignment.Left;
        header.Margin = new Thickness(0, 2, 34, 0);
        right.Children.Add(header);
        var close = Views.Cards.CardKit.CloseButton(() => CloseRequested?.Invoke(), "Stop sharing");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Margin = new Thickness(0, -3, -4, 0);
        right.Children.Add(close);

        var name = WallParts.PopupTitle(fileName, wrap: true);
        name.TextWrapping = TextWrapping.Wrap;
        name.MaxHeight = 48;
        name.Margin = new Thickness(0, 10, 0, 0);
        name.ToolTip = fileName;
        SetRow(name, 1);
        right.Children.Add(name);

        var sizeText = WallParts.Text(ByteSize.Format(size), 12.5, FontWeights.Normal, "WallText2");
        sizeText.Margin = new Thickness(0, 3, 0, 0);
        SetRow(sizeText, 2);
        right.Children.Add(sizeText);

        _received = WallParts.Text("", 12, FontWeights.SemiBold);
        _received.Margin = new Thickness(0, 0, 0, 8);
        _received.Visibility = Visibility.Collapsed;
        SetRow(_received, 4);
        right.Children.Add(_received);

        // Bottom: instructions, swapped for progress and then "Sent".
        var status = new Grid { MinHeight = 44, VerticalAlignment = VerticalAlignment.Bottom };
        SetRow(status, 5);
        right.Children.Add(status);

        _hint = WallParts.Text("Scan with your phone on the same Wi-Fi", 12, FontWeights.Normal, "WallText2");
        _hint.TextWrapping = TextWrapping.Wrap;
        // The token is the only thing that lets someone in, so the card shows just the address;
        // the QR code carries the full link.
        _url = WallParts.Text(MaskToken(url), 11, FontWeights.Normal, "WallText2");
        _url.Margin = new Thickness(0, 4, 0, 0);
        _waiting = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        _waiting.Children.Add(_hint);
        _waiting.Children.Add(_url);
        status.Children.Add(_waiting);

        // Progress is a white meter on a dark track; the percentage is the big number.
        _fill = new Border { Height = 4, Width = 0, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Background = WallParts.Res("WallText") };
        _track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = WallParts.Res("WallTrack"), Child = _fill };
        _progressText = WallParts.Text("", 11.5, FontWeights.Normal, "WallText2");
        _percentText = WallParts.Text("", 22, FontWeights.ExtraBold, tabular: true);
        _percentText.HorizontalAlignment = HorizontalAlignment.Right;
        _percentText.VerticalAlignment = VerticalAlignment.Bottom;
        var sending = WallParts.Label("Sending");
        sending.HorizontalAlignment = HorizontalAlignment.Left;
        sending.VerticalAlignment = VerticalAlignment.Bottom;
        sending.Margin = new Thickness(0, 0, 0, 3);
        var top = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        top.Children.Add(sending);
        top.Children.Add(_percentText);
        var labels = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        labels.Children.Add(_progressText);
        _progress = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 2) };
        _progress.Children.Add(top);
        _progress.Children.Add(_track);
        _progress.Children.Add(labels);
        status.Children.Add(_progress);

        var check = WallParts.Glyph("", 18);
        var sentWord = WallParts.Text("Sent", 22, FontWeights.ExtraBold);
        sentWord.Margin = new Thickness(8, 0, 0, 0);
        _sent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed };
        _sent.Children.Add(check);
        _sent.Children.Add(sentWord);
        status.Children.Add(_sent);

        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            CloseRequested?.Invoke();
        };
        Loaded += (_, _) => Focus();
        Unloaded += (_, _) => StopFrames();
    }

    /// <summary>The close button or Escape.</summary>
    public event Action? CloseRequested;

    public void SetProgress(long sent, long total)
    {
        if (_sent.Visibility == Visibility.Visible) return;
        _waiting.Visibility = Visibility.Collapsed;
        _progress.Visibility = Visibility.Visible;
        double f = total <= 0 ? 1 : Math.Clamp(sent / (double)total, 0, 1);
        _progressText.Text = $"{ByteSize.Format(sent)} of {ByteSize.Format(total)}";
        _percentText.Text = $"{f:P0}";
        _level.AnimateTo(f, 0.35, 1);
        _frames ??= Animator.Subscribe(_ =>
        {
            _fill.Width = Math.Max(0, _level.Value * _track.ActualWidth);
            if (_level.IsSettled) StopFrames();
        });
    }

    public void ShowSent()
    {
        StopFrames();
        _waiting.Visibility = Visibility.Collapsed;
        _progress.Visibility = Visibility.Collapsed;
        _sent.Visibility = Visibility.Visible;
    }

    public void ShowReceived(string text)
    {
        _received.Text = text;
        _received.Visibility = Visibility.Visible;
    }

    void StopFrames()
    {
        _frames?.Dispose();
        _frames = null;
    }

    internal static string MaskToken(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Authority}/••••" : "";

    static UIElement BuildTile(string url)
    {
        var fg = InvertedQr ? Colors.White : Colors.Black;
        var tileColor = InvertedQr ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Colors.White;
        var image = new Image
        {
            Source = RenderQr(url, fg, tileColor),
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        System.Windows.Automation.AutomationProperties.SetName(image, "QR code: scan it with your phone's camera to download");
        return new Border
        {
            Width = Tile,
            Height = Tile,
            CornerRadius = new CornerRadius(WallParts.Radius),
            Background = new SolidColorBrush(tileColor),
            Padding = new Thickness(8),
            Margin = new Thickness(20, 0, 0, 0),
            Child = image,
        };
    }

    static System.Windows.Media.Imaging.BitmapSource? RenderQr(string url, Color fg, Color bg)
    {
        // Several whole pixels per module at the drawn size, so nearest-neighbour scaling stays crisp.
        try { return QrBitmap.Render(url, 348, fg, bg, margin: 2); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ZXing.WriterException)
        {
            Infrastructure.Log.Warn("[phonesend] QR render failed", ex);
            return null;
        }
    }
}
