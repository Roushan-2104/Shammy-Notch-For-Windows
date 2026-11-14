using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shammy.Core.Search;
using Shammy.Interop;

namespace Shammy.Search;

/// <summary>A frozen frame of one monitor, in physical pixels.</summary>
internal sealed class ScreenShot
{
    public required BitmapSource Image { get; init; }
    public required byte[] Pixels { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required Native.RECT Bounds { get; init; }

    public int Stride => Width * 4;

    public BitmapSource Crop(Box region)
    {
        var r = region.Clamp(Width, Height);
        int w = Math.Max(1, (int)r.W), h = Math.Max(1, (int)r.H);
        // A copy of just the region: a CroppedBitmap would keep the whole frozen frame (8-33 MB) alive with it.
        var pixels = new byte[w * h * 4];
        Image.CopyPixels(new System.Windows.Int32Rect((int)r.X, (int)r.Y, w, h), pixels, w * 4, 0);
        var crop = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        crop.Freeze();
        return crop;
    }

    /// <summary>PNG of a region, scaled so its longest side is at most <paramref name="maxSide"/>.</summary>
    public byte[] EncodePng(Box region, int maxSide = 1000)
    {
        BitmapSource img = Crop(region);
        double scale = Math.Min(1, (double)maxSide / Math.Max(img.PixelWidth, img.PixelHeight));
        if (scale < 1) img = new TransformedBitmap(img, new ScaleTransform(scale, scale));
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}

internal static class ScreenCapture
{
    /// <summary>
    /// Copies the monitor under the cursor with GDI. CAPTUREBLT includes layered windows (like the notch
    /// itself). DRM-protected video comes out black, which is Windows' rule, not a bug.
    /// </summary>
    public static ScreenShot CaptureMonitorAtCursor()
    {
        Native.GetCursorPos(out var cursor);
        var bounds = Native.MonitorBounds(Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST));
        int w = bounds.Width, h = bounds.Height;

        var screenDc = Native.GetDC(IntPtr.Zero);
        var memDc = Native.CreateCompatibleDC(screenDc);
        var bmp = Native.CreateCompatibleBitmap(screenDc, w, h);
        var old = bmp == IntPtr.Zero ? IntPtr.Zero : Native.SelectObject(memDc, bmp);
        try
        {
            if (bmp == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleBitmap failed");
            if (!Native.BitBlt(memDc, 0, 0, w, h, screenDc, bounds.Left, bounds.Top, Native.SRCCOPY | Native.CAPTUREBLT))
                throw new InvalidOperationException($"BitBlt failed ({Marshal.GetLastWin32Error()})");
            Native.SelectObject(memDc, old);
            old = IntPtr.Zero;

            var pixels = new byte[w * h * 4];
            var bmi = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,
                biPlanes = 1,
                biBitCount = 32,
            };
            if (Native.GetDIBits(memDc, bmp, 0, (uint)h, pixels, ref bmi, 0) == 0)
                throw new InvalidOperationException("GetDIBits failed");
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255; // GDI leaves alpha at 0

            var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            image.Freeze();
            return new ScreenShot { Image = image, Pixels = pixels, Width = w, Height = h, Bounds = bounds };
        }
        finally
        {
            // A selected bitmap can't be deleted; put the original back first on the failure paths.
            if (old != IntPtr.Zero) Native.SelectObject(memDc, old);
            if (bmp != IntPtr.Zero) Native.DeleteObject(bmp);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
