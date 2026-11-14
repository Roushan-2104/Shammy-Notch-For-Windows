using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shammy.Core.Search;
using Shammy.Interop;
using Shammy.Search;

namespace Shammy.Modules.Hover;

/// <summary>The words OCR read in a small strip around the pointer, with the strip's top-left on screen (physical pixels).</summary>
internal sealed record HoverRead(IReadOnlyList<OcrWord> Words, int Left, int Top);

/// <summary>
/// Reads only the words around a point: copies a strip of about 360×80 pixels (larger on a high-DPI screen) and runs
/// Windows' own offline OCR over it. Nothing is kept: the pixels go as soon as OCR is done.
/// </summary>
internal static class HoverReader
{
    const int StripWidth = 360, StripHeight = 80;

    /// <summary>The words around (<paramref name="x"/>, <paramref name="y"/>), or null when OCR isn't available or the copy failed.</summary>
    public static async Task<HoverRead?> ReadAsync(int x, int y)
    {
        if (!OcrService.IsAvailable) return null;
        var monitor = Native.MonitorFromPoint(new Native.POINT(x, y), Native.MONITOR_DEFAULTTONEAREST);
        double scale = Native.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
        var bounds = Native.MonitorBounds(monitor);
        int w = Math.Min(bounds.Width, (int)Math.Round(StripWidth * scale));
        int h = Math.Min(bounds.Height, (int)Math.Round(StripHeight * scale));
        int left = Math.Clamp(x - w / 2, bounds.Left, bounds.Right - w);
        int top = Math.Clamp(y - h / 2, bounds.Top, bounds.Bottom - h);

        var pixels = Copy(left, top, w, h);
        if (pixels is null) return null;
        // Small screen text reads far better at twice the size; a high-DPI screen's text is big enough already.
        int factor = scale < 1.4 ? 2 : 1;
        if (factor == 2) pixels = Double(pixels, w, h);
        int sw = w * factor, sh = h * factor;
        var image = BitmapSource.Create(sw, sh, 96, 96, PixelFormats.Bgra32, null, pixels, sw * 4);
        image.Freeze();
        var shot = new ScreenShot { Image = image, Pixels = pixels, Width = sw, Height = sh, Bounds = new Native.RECT { Left = left, Top = top, Right = left + w, Bottom = top + h } };
        var words = await OcrService.RecognizeAsync(shot);
        if (factor == 1) return new HoverRead(words, left, top);
        var back = words.Select(o => o with { Bounds = new Box(o.Bounds.X / factor, o.Bounds.Y / factor, o.Bounds.W / factor, o.Bounds.H / factor) }).ToList();
        return new HoverRead(back, left, top);
    }

    /// <summary>The screen's pixels in a rectangle (BGRA, top-down), or null.</summary>
    static byte[]? Copy(int left, int top, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;
        var screenDc = Native.GetDC(IntPtr.Zero);
        var memDc = Native.CreateCompatibleDC(screenDc);
        var bmp = Native.CreateCompatibleBitmap(screenDc, w, h);
        var old = bmp == IntPtr.Zero ? IntPtr.Zero : Native.SelectObject(memDc, bmp);
        try
        {
            if (bmp == IntPtr.Zero) return null;
            // CAPTUREBLT includes layered windows (text in them is hoverable too); the bubble is excluded from captures.
            if (!Native.BitBlt(memDc, 0, 0, w, h, screenDc, left, top, Native.SRCCOPY | Native.CAPTUREBLT)) return null;
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
            if (Native.GetDIBits(memDc, bmp, 0, (uint)h, pixels, ref bmi, 0) == 0) return null;
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255; // GDI leaves alpha at 0
            return pixels;
        }
        finally
        {
            if (old != IntPtr.Zero) Native.SelectObject(memDc, old);
            if (bmp != IntPtr.Zero) Native.DeleteObject(bmp);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>The picture at twice the size, smoothed (bilinear), so OCR sees small text as it would larger text.</summary>
    static byte[] Double(byte[] src, int w, int h)
    {
        int dw = w * 2, dh = h * 2;
        var dst = new byte[dw * dh * 4];
        for (int y = 0; y < dh; y++)
        {
            double sy = Math.Clamp((y + 0.5) / 2 - 0.5, 0, h - 1);
            int y0 = (int)sy, y1 = Math.Min(y0 + 1, h - 1);
            double fy = sy - y0;
            for (int x = 0; x < dw; x++)
            {
                double sx = Math.Clamp((x + 0.5) / 2 - 0.5, 0, w - 1);
                int x0 = (int)sx, x1 = Math.Min(x0 + 1, w - 1);
                double fx = sx - x0;
                int a = (y0 * w + x0) * 4, b = (y0 * w + x1) * 4, c = (y1 * w + x0) * 4, d = (y1 * w + x1) * 4;
                int o = (y * dw + x) * 4;
                for (int k = 0; k < 3; k++)
                {
                    double top = src[a + k] + (src[b + k] - src[a + k]) * fx;
                    double bottom = src[c + k] + (src[d + k] - src[c + k]) * fx;
                    dst[o + k] = (byte)(top + (bottom - top) * fy + 0.5);
                }
                dst[o + 3] = 255;
            }
        }
        return dst;
    }
}
