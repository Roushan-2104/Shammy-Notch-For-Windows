using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Shammy.Modules.ClipboardHistory;

/// <summary>An image kept in the history: PNG-encoded (a raw 4K bitmap is 33 MB) plus a small thumbnail.</summary>
internal sealed record ClipImage(byte[] Png, BitmapSource Thumb);

/// <summary>Image plumbing. Everything returned is frozen so it can cross between threads.</summary>
internal static class ClipImages
{
    public const int ThumbWidth = 64;

    /// <summary>The image on the clipboard, preferring PNG (keeps alpha) over the DIB most apps also offer.</summary>
    public static BitmapSource? FromDataObject(IDataObject data)
    {
        if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png)
        {
            var frame = BitmapFrame.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bmp)
        {
            // DIBs usually carry a zero alpha channel, which would make the copy invisible; drop it.
            var copy = new WriteableBitmap(new FormatConvertedBitmap(bmp, PixelFormats.Bgr32, null, 0));
            copy.Freeze();
            return copy;
        }
        return null;
    }

    /// <summary>Hash of the pixels (for de-duplication), the PNG bytes and a thumbnail. Slow for big images: run off the UI thread.</summary>
    public static (string Hash, ClipImage Image) Process(BitmapSource image)
    {
        BitmapSource src = image.Format == PixelFormats.Bgra32 || image.Format == PixelFormats.Bgr32 || image.Format == PixelFormats.Pbgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int stride = src.PixelWidth * 4;
        var pixels = new byte[stride * src.PixelHeight];
        src.CopyPixels(pixels, stride, 0);
        var hash = Convert.ToHexString(SHA256.HashData(pixels))[..20];
        // Encode from the pixels, not the image: a copied PNG is a decoded frame whose decoder belongs to the UI
        // thread even when frozen, and encoding it here threw "a different thread owns it", so the copy was lost.
        var png = EncodePng(BitmapSource.Create(src.PixelWidth, src.PixelHeight, 96, 96, src.Format, null, pixels, stride));
        return (hash, new ClipImage(png, Decode(png, ThumbWidth)));
    }

    public static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>A small frozen copy at most <paramref name="width"/> wide that doesn't keep the big one alive.</summary>
    public static BitmapSource Shrink(BitmapSource image, int width)
    {
        if (image.PixelWidth <= width) return image;
        double scale = (double)width / image.PixelWidth;
        var small = new WriteableBitmap(new TransformedBitmap(image, new ScaleTransform(scale, scale)));
        small.Freeze();
        return small;
    }

    /// <summary>Decode encoded bytes, optionally down-scaled to <paramref name="width"/> pixels wide.</summary>
    public static BitmapSource Decode(byte[] bytes, int width = 0)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = new MemoryStream(bytes);
        if (width > 0) bi.DecodePixelWidth = width;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    /// <summary>Reads an image file without locking it. Throws while the file is still being written.</summary>
    public static (BitmapSource Preview, int Width, int Height, long Bytes) LoadFile(string path, int previewWidth)
    {
        byte[] bytes;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
        }
        if (bytes.Length == 0) throw new IOException("File is still empty");
        var frame = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand).Frames[0];
        int w = frame.PixelWidth, h = frame.PixelHeight;
        return (Decode(bytes, Math.Min(previewWidth, w)), w, h, bytes.Length);
    }

    /// <summary>A rounded image tile, used for ear thumbnails and list rows.</summary>
    public static System.Windows.Controls.Border Tile(ImageSource image, double width, double height, double radius)
    {
        var tile = new System.Windows.Controls.Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(radius),
            Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill },
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Clipboard snips are full-size; the default filter shimmers when shrinking them this much.
        RenderOptions.SetBitmapScalingMode(tile, BitmapScalingMode.HighQuality);
        return tile;
    }
}
