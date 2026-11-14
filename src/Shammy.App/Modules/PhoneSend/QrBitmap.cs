using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.QrCode;
using ZXing.QrCode.Internal;
using ZXing.Rendering;

namespace Shammy.Modules.PhoneSend;

internal static class QrBitmap
{
    /// <summary>
    /// Renders <paramref name="text"/> as a QR code bitmap. <paramref name="margin"/> is the quiet zone in
    /// modules; ZXing rounds modules to whole pixels, so draw it with nearest-neighbour scaling.
    /// </summary>
    public static BitmapSource Render(string text, int pixels, Color foreground, Color background, int margin)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new QrCodeEncodingOptions
            {
                Width = pixels,
                Height = pixels,
                Margin = margin,
                // M survives a glare spot on a laptop screen without making the code much denser.
                ErrorCorrection = ErrorCorrectionLevel.M,
                // Text with accents, Hindi or emoji goes as UTF-8 bytes. Without an ECI header: phone scanners
                // recognise UTF-8 by itself, and some older ones choke on the header.
                CharacterSet = text.Any(c => c > 127) ? "UTF-8" : null,
                DisableECI = true,
            },
            Renderer = new PixelDataRenderer
            {
                Foreground = new PixelDataRenderer.Color(Argb(foreground)),
                Background = new PixelDataRenderer.Color(Argb(background)),
            },
        };
        var data = writer.Write(text);
        var bitmap = new WriteableBitmap(data.Width, data.Height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, data.Width, data.Height), data.Pixels, data.Width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    static int Argb(Color c) => (c.A << 24) | (c.R << 16) | (c.G << 8) | c.B;
}
