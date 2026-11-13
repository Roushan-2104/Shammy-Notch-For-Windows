using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Shammy.Core.Notch;
using Shammy.Infrastructure;
using Shammy.Interop;

namespace Shammy.Shell;

/// <summary>
/// The island's fake glass. Real Acrylic or Mica can't take the pill's shape, turns solid on a window that is never
/// active, lags on resize and keeps the GPU busy, so instead: the wallpaper of the island's screen is decoded small
/// (DecodePixelWidth, never at full size), the part under the window is cropped at 1/8 scale, blurred once and
/// pre-tinted (~88 % of the island's near-black). The island then just draws that tiny frozen image scaled up.
/// Rebuilt only when the wallpaper, the display or its scale changes.
/// </summary>
internal sealed class IslandGlass
{
    /// <summary>How much of the island's own tint lies over the wallpaper.</summary>
    const double Tint = 0.88;

    /// <summary>Blur radius at 1/8 scale (about 24 px on screen), three box passes.</summary>
    const int BlurRadius = 3;

    readonly Dispatcher _ui;
    string? _key;
    int _generation;

    public IslandGlass(Dispatcher ui) => _ui = ui;

    /// <summary>The glass (frozen), or null while there is none yet or it couldn't be made.</summary>
    public BitmapSource? Image { get; private set; }

    /// <summary>The glass's colour around the closed island (meaningful while <see cref="Image"/> is set).</summary>
    public Color Average { get; private set; }

    /// <summary>A new <see cref="Image"/> is ready (on the UI thread).</summary>
    public event Action? Changed;

    public string Describe() => Image is { } i ? $"glass={i.PixelWidth}x{i.PixelHeight}" : "glass=none";

    /// <summary>The wallpaper may have changed: the next <see cref="Update"/> rebuilds even if nothing else did.</summary>
    public void Invalidate() => _key = null;

    /// <summary>
    /// Make (or keep) the glass for the window at <paramref name="left"/>,<paramref name="top"/> on
    /// <paramref name="monitor"/>, <paramref name="width"/>×<paramref name="height"/> device pixels. Cheap when nothing
    /// changed; otherwise the work runs off the UI thread and <see cref="Changed"/> fires when it's done.
    /// </summary>
    public void Update(IntPtr monitor, int left, int top, int width, int height)
    {
        if (monitor == IntPtr.Zero || width <= 0 || height <= 0) return;
        var screen = Native.MonitorBounds(monitor);
        string? path;
        WallpaperStyle style;
        try
        {
            path = WallpaperFor(screen);
            using var desktop = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            style = WallpaperFit.StyleFrom(desktop?.GetValue("WallpaperStyle") as string, desktop?.GetValue("TileWallpaper") as string);
        }
        catch (Exception ex)
        {
            Log.Warn("Glass: finding the wallpaper failed", ex);
            path = null;
            style = WallpaperStyle.Fill;
        }
        long stamp = 0;
        try
        {
            if (path is not null && File.Exists(path)) stamp = File.GetLastWriteTimeUtc(path).Ticks;
            else path = null;
        }
        catch (IOException) { path = null; }
        catch (UnauthorizedAccessException) { path = null; }
        uint background = GetSysColor(COLOR_DESKTOP);

        var key = $"{path}|{stamp}|{style}|{screen.Left},{screen.Top},{screen.Width},{screen.Height}|{left},{top},{width},{height}|{background}";
        if (key == _key) return;
        _key = key;
        int generation = ++_generation;
        var crop = new PixelRect(left - screen.Left, top - screen.Top, width, height);
        var bg = ((byte)((background >> 16) & 0xFF), (byte)((background >> 8) & 0xFF), (byte)(background & 0xFF));
        Task.Run(() => Build(path, style, screen.Width, screen.Height, crop, bg)).ContinueWith(t =>
        {
            if (t.Exception is { } ex) Log.Warn("Glass: building it failed", ex.InnerException ?? ex);
            var built = t.IsCompletedSuccessfully ? t.Result : default;
            _ui.BeginInvoke(() =>
            {
                if (generation != _generation) return;
                Image = built.Image;
                Average = built.Average;
                Changed?.Invoke();
            });
        }, TaskScheduler.Default);
    }

    static (BitmapSource Image, Color Average) Build(string? path, WallpaperStyle style, int screenW, int screenH, PixelRect crop, (byte B, byte G, byte R) background)
    {
        int outW = Math.Max(8, (int)Math.Ceiling(crop.W / GlassBackdrop.Downscale));
        int outH = Math.Max(8, (int)Math.Ceiling(crop.H / GlassBackdrop.Downscale));
        byte[] src = Array.Empty<byte>();
        int srcW = 0, srcH = 0;
        var placed = new PixelRect(0, 0, screenW, screenH);
        if (path is not null)
        {
            try
            {
                // The picture's own size (read from its header only) decides where it lands on the screen.
                int fullW, fullH;
                using (var stream = File.OpenRead(path))
                {
                    var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                    fullW = frame.PixelWidth;
                    fullH = frame.PixelHeight;
                }
                placed = WallpaperFit.Place(fullW, fullH, screenW, screenH, style);
                // Decoded at about 1/8 of the size it shows at: never at full size.
                int decodeW = Math.Clamp((int)Math.Ceiling(placed.W / GlassBackdrop.Downscale), 8, Math.Max(8, fullW));
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path);
                image.DecodePixelWidth = decodeW;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.IgnoreImageCache;
                image.EndInit();
                image.Freeze();
                var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
                srcW = bgra.PixelWidth;
                srcH = bgra.PixelHeight;
                src = new byte[srcW * srcH * 4];
                bgra.CopyPixels(src, srcW * 4, 0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException or FileFormatException or COMException)
            {
                Log.Warn("Glass: the wallpaper couldn't be read; using its colour", ex);
                src = Array.Empty<byte>();
                srcW = srcH = 0;
            }
        }

        var px = GlassBackdrop.Crop(src, srcW, srcH, placed, crop, outW, outH, background);
        GlassBackdrop.Blur(px, outW, outH, BlurRadius);
        // Bake the island's tint in, so drawing the glass is one opaque image and nothing more.
        var island = IslandPalettes.Island;
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = (byte)Math.Round(island.B * Tint + px[i] * (1 - Tint));
            px[i + 1] = (byte)Math.Round(island.G * Tint + px[i + 1] * (1 - Tint));
            px[i + 2] = (byte)Math.Round(island.R * Tint + px[i + 2] * (1 - Tint));
            px[i + 3] = 255;
        }
        var result = BitmapSource.Create(outW, outH, 96, 96, PixelFormats.Bgra32, null, px, outW * 4);
        result.Freeze();
        return (result, MiddleColour(px, outW, outH));
    }

    /// <summary>The glass's colour where the closed island sits (the middle of the top rows), for the bits drawn beside it.</summary>
    static Color MiddleColour(byte[] px, int w, int h)
    {
        int rows = Math.Max(1, h / 12), x0 = w / 3, x1 = Math.Max(x0 + 1, 2 * w / 3);
        long b = 0, g = 0, r = 0, n = 0;
        for (int y = 0; y < rows; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * w + x) * 4;
                b += px[i];
                g += px[i + 1];
                r += px[i + 2];
                n++;
            }
        return Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    // ---- Which picture is on which screen ---------------------------------------------------------

    /// <summary>The wallpaper file on the screen with these bounds; the desktop-wide one as a fallback.</summary>
    static string? WallpaperFor(Native.RECT screen)
    {
        try
        {
            var wallpaper = (IDesktopWallpaper)new DesktopWallpaperClass();
            try
            {
                uint count = wallpaper.GetMonitorDevicePathCount();
                for (uint i = 0; i < count; i++)
                {
                    string id = wallpaper.GetMonitorDevicePathAt(i);
                    wallpaper.GetMonitorRECT(id, out var r);
                    if (r.Left != screen.Left || r.Top != screen.Top || r.Right != screen.Right || r.Bottom != screen.Bottom) continue;
                    string file = wallpaper.GetWallpaper(id);
                    if (!string.IsNullOrWhiteSpace(file) && File.Exists(file)) return file;
                    break;
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(wallpaper);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            // Older or trimmed-down Windows: fall back to the desktop-wide picture below.
        }

        var buffer = new char[MaxPath];
        if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)buffer.Length, buffer, 0))
        {
            var file = new string(buffer).TrimEnd('\0');
            if (file.Length > 0 && File.Exists(file)) return file;
        }
        // Slideshows and Spotlight leave the picture in use here.
        var transcoded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
        return File.Exists(transcoded) ? transcoded : null;
    }

    const uint SPI_GETDESKWALLPAPER = 0x0073;
    const int MaxPath = 1024, COLOR_DESKTOP = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SystemParametersInfo(uint action, uint param, char[] value, uint winIni);

    [DllImport("user32.dll")]
    static extern uint GetSysColor(int index);

    [ComImport, Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")]
    class DesktopWallpaperClass
    {
    }

    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId);

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetMonitorDevicePathAt(uint monitorIndex);

        uint GetMonitorDevicePathCount();

        void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out Native.RECT displayRect);
    }
}
