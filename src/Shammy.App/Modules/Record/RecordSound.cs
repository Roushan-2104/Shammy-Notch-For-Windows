using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using Shammy.Core.Record;

namespace Shammy.Modules.Record;

/// <summary>
/// One sound a recording hears: what the PC is playing (loopback of the default speakers) or the default
/// microphone. Whatever format Windows delivers becomes 48 kHz stereo on the recording's clock
/// (<see cref="AudioTimeline"/>). If the device goes away (headphones unplugged) it tries the new default a
/// moment later; until then that source is simply silent.
/// </summary>
internal sealed class RecordSound : IDisposable
{
    public const int Rate = 48000, Channels = 2;
    const int MaxRestarts = 20;
    static readonly Guid IeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");

    readonly bool _loopback;
    readonly Func<long> _nowFrame;
    readonly Action<string, Exception?> _warn;
    readonly object _gate = new();
    WasapiCapture? _capture;
    WdlResampler? _resampler;
    WaveFormat? _format;
    float[] _floats = Array.Empty<float>(), _stereo = Array.Empty<float>(), _resampled = Array.Empty<float>();
    int _restarts;
    bool _disposed;

    /// <param name="nowFrame">The recording clock in 48 kHz frames; negative before the recording has started.</param>
    public RecordSound(bool loopback, Func<long> nowFrame, Action<string, Exception?> warn)
    {
        _loopback = loopback;
        _nowFrame = nowFrame;
        _warn = warn;
    }

    public AudioTimeline Timeline { get; } = new(Rate, Channels);

    public string Name => _loopback ? "PC sound" : "microphone";

    /// <summary>True once Windows has delivered any sound at all.</summary>
    public bool Heard { get; private set; }

    /// <summary>The loudest sample so far, 0..1 (0 for a muted microphone or a silent PC).</summary>
    public float Peak { get; private set; }

    /// <summary>"PC sound 48000 Hz 2 ch peak 0.31", for the log and the test pipe.</summary>
    public string Describe() => _format is { } f
        ? FormattableString.Invariant($"{Name} {f.SampleRate} Hz {f.Channels} ch {f.BitsPerSample} bit heard={Heard} peak={Peak:0.0000}")
        : $"{Name} unavailable";

    /// <summary>Starts listening; false when there is no such device (the recording goes on without it).</summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            try
            {
                var capture = _loopback ? new WasapiLoopbackCapture() : NewMicrophone();
                SetFormat(capture.WaveFormat);
                capture.DataAvailable += OnData;
                capture.RecordingStopped += OnStopped;
                capture.StartRecording();
                _capture = capture;
                return true;
            }
            catch (Exception ex)
            {
                _warn($"Recording the {Name} is unavailable", ex);
                return false;
            }
        }
    }

    static WasapiCapture NewMicrophone()
    {
        using var devices = new MMDeviceEnumerator();
        return new WasapiCapture(devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console));
    }

    /// <summary>The format the device delivers; anything but 48 kHz goes through a resampler.</summary>
    internal void SetFormat(WaveFormat format)
    {
        _format = format;
        _resampler = null;
        if (format.SampleRate == Rate) return;
        _resampler = new WdlResampler();
        _resampler.SetMode(true, 2, false);
        _resampler.SetFilterParms();
        _resampler.SetFeedMode(true);
        _resampler.SetRates(format.SampleRate, Rate);
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        if (ReferenceEquals(sender, _capture)) Feed(e.Buffer, e.BytesRecorded);
    }

    /// <summary>A burst of the device's bytes, in the format last given to <see cref="SetFormat"/>, heard just now.</summary>
    internal void Feed(byte[] buffer, int bytes)
    {
        try
        {
            if (bytes <= 0 || _format is not { } format) return;
            int frames = ToFloat(buffer, bytes, format);
            if (frames == 0) return;

            if (_stereo.Length < frames * 2) _stereo = new float[frames * 2];
            PcmMix.ToStereo(_floats.AsSpan(0, frames * format.Channels), format.Channels, _stereo);
            var sound = _stereo.AsSpan(0, frames * 2);

            if (_resampler is { } resampler)
            {
                int wanted = resampler.ResamplePrepare(frames, Channels, out var input, out int offset);
                sound[..(wanted * Channels)].CopyTo(input.AsSpan(offset));
                int room = (int)((long)frames * Rate / format.SampleRate) + 64;
                if (_resampled.Length < room * Channels) _resampled = new float[room * Channels];
                int made = resampler.ResampleOut(_resampled, 0, wanted, room, Channels);
                sound = _resampled.AsSpan(0, made * Channels);
            }
            if (sound.Length == 0) return;
            float peak = Peak;
            foreach (float v in sound) peak = Math.Max(peak, Math.Abs(v));
            Peak = peak;
            Heard = true;
            Timeline.Write(sound, _nowFrame());
        }
        catch (Exception ex)
        {
            _warn($"Reading the {Name} failed", ex);
        }
    }

    /// <summary>The device's bytes as floats in <see cref="_floats"/>, still in its own channel layout; returns the frames.</summary>
    int ToFloat(byte[] buffer, int bytes, WaveFormat format)
    {
        int bits = format.BitsPerSample, width = bits / 8;
        if (width <= 0 || format.Channels <= 0) return 0;
        int samples = bytes / width, frames = samples / format.Channels;
        samples = frames * format.Channels;
        if (_floats.Length < samples) _floats = new float[samples];
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloat);
        var source = buffer.AsSpan(0, samples * width);
        var floats = _floats.AsSpan(0, samples);
        switch (bits)
        {
            case 32 when isFloat:
                MemoryMarshal.Cast<byte, float>(source).CopyTo(floats);
                break;
            case 32:
            {
                var ints = MemoryMarshal.Cast<byte, int>(source);
                for (int i = 0; i < samples; i++) floats[i] = ints[i] / 2147483648f;
                break;
            }
            case 24:
                for (int i = 0; i < samples; i++)
                {
                    int v = source[i * 3] | (source[i * 3 + 1] << 8) | ((sbyte)source[i * 3 + 2] << 16);
                    floats[i] = v / 8388608f;
                }
                break;
            case 16:
            {
                var shorts = MemoryMarshal.Cast<byte, short>(source);
                for (int i = 0; i < samples; i++) floats[i] = shorts[i] / 32768f;
                break;
            }
            default:
                return 0;
        }
        return frames;
    }

    void OnStopped(object? sender, StoppedEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _capture)) return;
            Drop();
            if (++_restarts > MaxRestarts) return;
        }
        _warn($"The {Name} stopped; trying the default device again", e.Exception);
        // Give Windows a moment to settle on the new default device.
        _ = Task.Delay(1000).ContinueWith(_ => Start(), TaskScheduler.Default);
    }

    /// <summary>Lets go of a stopped device. Its own thread is the one telling us, and closing waits for that thread, so close from another.</summary>
    void Drop()
    {
        var capture = _capture;
        _capture = null;
        if (capture is null) return;
        capture.DataAvailable -= OnData;
        capture.RecordingStopped -= OnStopped;
        _ = Task.Run(() =>
        {
            try { capture.Dispose(); }
            catch (Exception ex) { _warn($"Closing the {Name} failed", ex); }
        });
    }

    public void Dispose()
    {
        WasapiCapture? capture;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            capture = _capture;
            _capture = null;
        }
        if (capture is null) return;
        capture.DataAvailable -= OnData;
        capture.RecordingStopped -= OnStopped;
        try
        {
            capture.StopRecording();
            capture.Dispose();
        }
        catch (Exception ex)
        {
            _warn($"Closing the {Name} failed", ex);
        }
    }
}
