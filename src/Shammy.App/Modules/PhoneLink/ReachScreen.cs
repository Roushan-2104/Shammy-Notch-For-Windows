using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shammy.Interop;
using Shammy.Search;

namespace Shammy.Modules.PhoneLink;

/// <summary>
/// Reach P7 and P8 on the laptop, on one background thread that sleeps when nothing is asked:
/// <list type="bullet">
/// <item>The screen for the phone (<c>screen</c> {mode}): the monitor under the pointer, as a JPEG small enough for one
/// message (≤ 45 KB), every 3 s for the Laptop view's card ("snap"), about 4 a second while the phone shows it full
/// screen ("live"); an unchanged picture isn't sent again. GDI capture, as Shammy's own screen search uses: no Windows
/// capture border, DRM video comes out black.</item>
/// <item>"Tell me when the screen goes quiet" (<c>watch</c> {quiet}): every 10 s a 32×18 thumbnail; once the screen
/// has been changing and then stays still for 2 minutes, one <c>done</c> {what: "quiet"}.</item>
/// </list>
/// shortcut: each capture copies the full monitor (8 MB at 1080p) before scaling; fine at 4 a second.
/// </summary>
internal sealed class ReachScreen : IDisposable
{
    const int MaxJpeg = 45_000;
    readonly Action<JsonObject> _send;
    readonly Action<string> _warn;
    readonly Func<bool> _allowed;
    readonly Thread _thread;
    volatile bool _stop;
    volatile string _mode = "off";
    volatile bool _quiet;

    /// <summary>The monitor in the last picture sent (the phone's taps on the picture land on it), physical pixels.</summary>
    public volatile Native.RECT[] Shown = [];

    /// <param name="allowed">Whether the screen may be sent now (Reach is on): checked before every picture.</param>
    public ReachScreen(Action<JsonObject> send, Action<string> warn, Func<bool> allowed)
    {
        _send = send; _warn = warn; _allowed = allowed;
        _thread = new Thread(Run) { IsBackground = true, Name = "Reach screen" };
        _thread.SetApartmentState(ApartmentState.STA); // WPF imaging
        _thread.Start();
    }

    /// <summary>"off", "snap" or "live".</summary>
    public string Mode
    {
        get => _mode;
        set
        {
            var mode = value is "snap" or "live" ? value : "off";
            if (mode == _mode) return;
            _mode = mode;
            // a fresh start: the next picture goes even if the screen hasn't changed (the phone dropped its copy)
            _sentHash = 0; Shown = []; _nextShot = 0;
        }
    }

    public bool Quiet { set { _quiet = value; if (!value) { _changing = false; _last = null; } } }

    long _nextShot, _nextQuiet, _changedAt;
    bool _changing;
    byte[]? _last;
    volatile int _sentHash;
    int _fits; // the step that last fitted, so most pictures are encoded once
    string _lastError = "";

    void Run()
    {
        while (!_stop)
        {
            long now = Environment.TickCount64;
            try
            {
                if (_mode != "off" && now >= _nextShot)
                {
                    _nextShot = now + (_mode == "live" ? 250 : 3000);
                    if (_allowed()) Shot(); else _mode = "off"; // never past the end of Reach
                }
                if (_quiet && now >= _nextQuiet) { _nextQuiet = now + 10_000; Watch(now); }
            }
            catch (Exception ex)
            {
                // anything at all (a locked screen, an encoder hiccup): this thread must never take Shammy down; said once per kind
                if (ex.GetType().Name != _lastError) { _lastError = ex.GetType().Name; _warn($"Reach screen: {_lastError}"); }
                _nextShot = now + 3000;
            }
            Thread.Sleep(_mode == "live" ? 30 : 200);
        }
    }

    void Shot()
    {
        var shot = ScreenCapture.CaptureMonitorAtCursor();
        var steps = new[] { (960, 55), (720, 50), (560, 40), (480, 35) };
        for (int i = Math.Min(_fits, steps.Length - 1); i < steps.Length; i++)
        {
            var (width, quality) = steps[i];
            var jpeg = Jpeg(shot.Image, width, quality);
            if (jpeg.Length > MaxJpeg) continue;
            _fits = i;
            int hash = Hash(jpeg);
            if (hash == _sentHash) return; // the same picture as last time
            _sentHash = hash;
            Shown = [shot.Bounds];
            _send(new JsonObject { ["t"] = "shot", ["jpg"] = Convert.ToBase64String(jpeg), ["w"] = shot.Width, ["h"] = shot.Height });
            return;
        }
        _fits = 0; // nothing fitted this time (a very busy picture): start from the top again next time
        if (_lastError != "too big") { _lastError = "too big"; _warn("Reach screen: a picture didn't fit in one message"); }
    }

    void Watch(long now)
    {
        var thumb = Thumb(ScreenCapture.CaptureMonitorAtCursor().Image);
        if (_last is { } last)
        {
            long diff = 0;
            for (int i = 0; i < thumb.Length; i++) diff += Math.Abs(thumb[i] - last[i]);
            bool moved = diff > thumb.Length * 3; // ~1% of the brightness range on average
            if (moved) { _changing = true; _changedAt = now; }
            else if (_changing && now - _changedAt >= 120_000)
            {
                _changing = false;
                _send(new JsonObject { ["t"] = "done", ["what"] = "quiet" });
            }
        }
        _last = thumb;
    }

    static byte[] Jpeg(BitmapSource image, int width, int quality)
    {
        double s = Math.Min(1, (double)width / image.PixelWidth);
        var scaled = s < 1 ? new TransformedBitmap(image, new ScaleTransform(s, s)) : image;
        var enc = new JpegBitmapEncoder { QualityLevel = quality };
        enc.Frames.Add(BitmapFrame.Create(scaled));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static byte[] Thumb(BitmapSource image)
    {
        var small = new FormatConvertedBitmap(new TransformedBitmap(image, new ScaleTransform(32.0 / image.PixelWidth, 18.0 / image.PixelHeight)), PixelFormats.Gray8, null, 0);
        var px = new byte[small.PixelWidth * small.PixelHeight];
        small.CopyPixels(px, small.PixelWidth, 0);
        return px;
    }

    static int Hash(byte[] b)
    {
        var h = new HashCode();
        h.AddBytes(b);
        return h.ToHashCode();
    }

    public void Dispose() => _stop = true;
}
