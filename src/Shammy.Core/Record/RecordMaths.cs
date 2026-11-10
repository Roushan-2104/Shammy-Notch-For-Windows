namespace Shammy.Core.Record;

/// <summary>The numbers behind a recording: the picture's size, its frame rate and how many bits a second it gets.</summary>
public static class RecordMaths
{
    public const int FrameRate = 30;

    /// <summary>The largest picture every H.264 encoder takes (level 5.1): bigger screens are scaled to fit.</summary>
    public const int MaxWidth = 4096, MaxHeight = 2304;

    /// <summary>Bits per pixel per frame. Screens are mostly still and sharp-edged; this keeps text crisp at 30 fps.</summary>
    public const double BitsPerPixel = 0.13;

    public const int MinBitrate = 2_000_000, MaxBitrate = 40_000_000, AudioBitrate = 192_000;

    /// <summary>
    /// The video's size for a captured size: the same shape, never above <see cref="MaxWidth"/>×<see cref="MaxHeight"/>
    /// (or the given cap), and both sides even, because H.264 cannot hold an odd one. Never smaller than 2×2.
    /// </summary>
    public static (int Width, int Height) OutputSize(int width, int height, int maxWidth = MaxWidth, int maxHeight = MaxHeight)
    {
        width = Math.Max(2, width);
        height = Math.Max(2, height);
        double scale = Math.Min(1, Math.Min(maxWidth / (double)width, maxHeight / (double)height));
        return (Even((int)Math.Round(width * scale)), Even((int)Math.Round(height * scale)));
    }

    /// <summary>Rounds down to an even number, at least 2.</summary>
    public static int Even(int n) => Math.Max(2, n & ~1);

    /// <summary>
    /// Video bits a second for a picture this size: pixels × frames × <see cref="BitsPerPixel"/>, rounded to the
    /// nearest half megabit and kept between 2 and 40 Mbps. 1920×1080 gets 8 Mbps, 2560×1440 14.5, 3840×2160 32.5.
    /// </summary>
    public static int Bitrate(int width, int height, int frameRate = FrameRate)
    {
        double raw = (double)Math.Max(1, width) * Math.Max(1, height) * Math.Max(1, frameRate) * BitsPerPixel;
        long rounded = (long)Math.Round(raw / 500_000) * 500_000;
        return (int)Math.Clamp(rounded, MinBitrate, MaxBitrate);
    }

    /// <summary>Roughly how many bytes a minute of recording takes at this size (video plus sound).</summary>
    public static long BytesPerMinute(int width, int height, bool sound) =>
        ((long)Bitrate(width, height) + (sound ? AudioBitrate : 0)) * 60 / 8;

    /// <summary>When frame <paramref name="index"/> is due, counted from the start (frame 0 at zero).</summary>
    public static TimeSpan FrameTime(long index, int frameRate = FrameRate) =>
        TimeSpan.FromTicks(index * TimeSpan.TicksPerSecond / Math.Max(1, frameRate));

    /// <summary>
    /// The part of a screen a dragged rectangle records: kept inside the screen, both sides even, at least
    /// <paramref name="minSide"/> wide and tall. Null when the drag was too small to mean anything.
    /// </summary>
    public static (int X, int Y, int Width, int Height)? Area(int x, int y, int width, int height, int screenWidth, int screenHeight, int minSide = 64)
    {
        int left = Math.Clamp(Math.Min(x, x + width), 0, screenWidth), right = Math.Clamp(Math.Max(x, x + width), 0, screenWidth);
        int top = Math.Clamp(Math.Min(y, y + height), 0, screenHeight), bottom = Math.Clamp(Math.Max(y, y + height), 0, screenHeight);
        int w = (right - left) & ~1, h = (bottom - top) & ~1;
        if (w < minSide || h < minSide) return null;
        return (left, top, w, h);
    }
}
