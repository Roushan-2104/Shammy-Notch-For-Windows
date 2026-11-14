using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shammy.Core.CircleMemory;
using Shammy.Infrastructure;

namespace Shammy.Modules.CircleMemory;

/// <summary>
/// Keeps the circle memory in %LOCALAPPDATA%\Shammy\circle-memory: the list in circles.dat and each small picture in
/// &lt;id&gt;.pic, all encrypted for this Windows account (DPAPI), like the song history. It never leaves the PC.
/// Writes happen off the UI thread; the latest list wins.
/// </summary>
internal sealed class CircleMemoryStore
{
    static readonly byte[] Entropy = "Shammy circle memory v1"u8.ToArray();
    static readonly byte[] PictureEntropy = "Shammy circle picture v1"u8.ToArray();

    /// <summary>A kept picture's height: enough for the card's 48 px tile at 2× and a dragged-out copy.</summary>
    public const int PictureHeight = 96;

    static string Dir
    {
        get
        {
            var dir = Path.Combine(Paths.Root, "circle-memory");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    static string FilePath => Path.Combine(Dir, "circles.dat");
    static string PicturePath(string id) => Path.Combine(Dir, id + ".pic");

    readonly object _lock = new();
    int _version;

    public CircleMemoryData Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<CircleMemoryData>(json) ?? new CircleMemoryData();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or CryptographicException or UnauthorizedAccessException)
        {
            // A convenience: starting over is fine (the next save replaces the unreadable file).
            Log.Warn("Circle memory unreadable; starting fresh", ex);
        }
        return new CircleMemoryData();
    }

    /// <summary>Writes a snapshot of the list off the UI thread; the latest snapshot wins.</summary>
    public void SaveInBackground(bool paused, IReadOnlyList<RememberedCircle> circles)
    {
        var snapshot = new CircleMemoryData { Paused = paused, Circles = circles.ToList() };
        int version = Interlocked.Increment(ref _version);
        _ = Task.Run(() =>
        {
            lock (_lock)
            {
                if (version != Volatile.Read(ref _version)) return; // a newer snapshot is queued
                try
                {
                    var tmp = FilePath + ".tmp";
                    var json = JsonSerializer.SerializeToUtf8Bytes(snapshot);
                    File.WriteAllBytes(tmp, ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser));
                    File.Move(tmp, FilePath, true);
                }
                catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
                {
                    Log.Warn("Saving the circle memory failed", ex);
                }
            }
        });
    }

    /// <summary>Encrypts and writes a picture (call off the UI thread). Returns false when it couldn't.</summary>
    public bool SavePicture(string id, byte[] jpeg)
    {
        try
        {
            File.WriteAllBytes(PicturePath(id), ProtectedData.Protect(jpeg, PictureEntropy, DataProtectionScope.CurrentUser));
            return true;
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
        {
            Log.Warn("Saving a circle's picture failed", ex);
            return false;
        }
    }

    /// <summary>The picture's JPEG bytes, or null when there is none (or it can't be read).</summary>
    public byte[]? LoadPicture(string id)
    {
        try
        {
            var path = PicturePath(id);
            return File.Exists(path) ? ProtectedData.Unprotect(File.ReadAllBytes(path), PictureEntropy, DataProtectionScope.CurrentUser) : null;
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
        {
            Log.Warn("Reading a circle's picture failed", ex);
            return null;
        }
    }

    /// <summary>Deletes these circles' pictures off the UI thread.</summary>
    public void DeletePictures(IEnumerable<string> ids)
    {
        var list = ids.ToList();
        if (list.Count == 0) return;
        _ = Task.Run(() =>
        {
            foreach (var id in list)
            {
                try { File.Delete(PicturePath(id)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("Deleting a circle's picture failed", ex); }
            }
        });
    }

    /// <summary>Removes every picture no circle in <paramref name="keep"/> owns (left over from a crash mid-delete).</summary>
    public void SweepPictures(IReadOnlyCollection<string> keep)
    {
        var ids = new HashSet<string>(keep);
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(Dir, "*.pic"))
                    if (!ids.Contains(Path.GetFileNameWithoutExtension(path))) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Tidying the circle pictures failed", ex);
            }
        });
    }

    // ---- Pictures ------------------------------------------------------------------------------------

    /// <summary>A small JPEG of the circled region, <see cref="PictureHeight"/> px tall at most; null if it won't decode.</summary>
    public static byte[]? Shrink(byte[] png)
    {
        try
        {
            using var input = new MemoryStream(png);
            var frame = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            if (frame.PixelWidth == 0 || frame.PixelHeight == 0) return null;
            double scale = Math.Min(1, Math.Min((double)PictureHeight / frame.PixelHeight, PictureHeight * 3.0 / frame.PixelWidth));
            BitmapSource small = scale < 1 ? new TransformedBitmap(frame, new ScaleTransform(scale, scale)) : frame;
            // JPEG has no transparency: flatten onto black (the island's colour) rather than let it turn grey.
            if (small.Format != PixelFormats.Bgr24 && small.Format != PixelFormats.Bgr32)
                small = new FormatConvertedBitmap(small, PixelFormats.Bgr24, null, 0);
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(small));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException or InvalidOperationException)
        {
            Log.Warn("Shrinking a circle's picture failed", ex);
            return null;
        }
    }

    /// <summary>A kept picture as an image for the card (frozen), or null.</summary>
    public static BitmapImage? ToImage(byte[]? jpeg, int decodeHeight = 0)
    {
        if (jpeg is null) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeHeight > 0) image.DecodePixelHeight = decodeHeight;
            image.StreamSource = new MemoryStream(jpeg);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException or InvalidOperationException)
        {
            Log.Warn("Showing a circle's picture failed", ex);
            return null;
        }
    }

    /// <summary>A kept picture as PNG bytes (for a dragged-out file), or null.</summary>
    public static byte[]? ToPng(byte[]? jpeg)
    {
        if (ToImage(jpeg) is not { } image) return null;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
