using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Shammy.Core.Captions;

namespace Shammy.Modules.Captions;

/// <summary>
/// Listens to what the PC is playing (WASAPI loopback on the default output, the way the equalizer bars do) and
/// feeds it to the caption window as 16 kHz mono. It exists only while captions are on. Nothing is kept beyond
/// the window's few seconds, and nothing is written anywhere.
/// A sound file can stand in for the speakers (the "captions-file" pipe), played at its real speed.
/// </summary>
internal sealed class CaptionsCapture : IDisposable
{
    readonly CaptionAudio _audio;
    readonly Action<string, Exception?> _warn;
    readonly object _life = new(), _feed = new();
    WasapiLoopbackCapture? _capture;
    string? _deviceId;
    CaptionResampler? _resampler;
    float[] _mono = new float[4800], _out = new float[2048];
    long _lastSound = Stopwatch.GetTimestamp(), _lastDeviceCheck = Stopwatch.GetTimestamp();
    CancellationTokenSource? _file;
    volatile bool _broken, _fromFile, _disposed;
    bool _startFailed;

    public CaptionsCapture(CaptionAudio audio, Action<string, Exception?> warn)
    {
        _audio = audio;
        _warn = warn;
    }

    /// <summary>A file is standing in for the speakers right now.</summary>
    public bool FromFile => _fromFile;

    /// <summary>The file has played to its end (raised off the UI thread): time to listen to the speakers again.</summary>
    public event Action? FileEnded;

    public bool IsListening
    {
        get { lock (_life) return _capture is not null; }
    }

    /// <summary>Starts listening to the default output device; false when Windows would not let it.</summary>
    public bool Start()
    {
        lock (_life)
        {
            if (_disposed) return false;
            if (_capture is not null) return true;
            try
            {
                using var devices = new MMDeviceEnumerator();
                var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var capture = new WasapiLoopbackCapture(device);
                capture.DataAvailable += OnData;
                capture.RecordingStopped += OnStopped;
                capture.StartRecording();
                _capture = capture;
                _deviceId = device.ID;
                _broken = false;
                _startFailed = false;
                Volatile.Write(ref _lastSound, Stopwatch.GetTimestamp());
                return true;
            }
            catch (Exception ex)
            {
                // Tried again every few seconds while there is no output device: say so once.
                if (!_startFailed) _warn("Listening to what the PC plays failed", ex);
                _startFailed = true;
                return false;
            }
        }
    }

    public void Stop()
    {
        lock (_life)
        {
            var c = _capture;
            _capture = null;
            if (c is null) return;
            try
            {
                c.DataAvailable -= OnData;
                c.RecordingStopped -= OnStopped;
                c.StopRecording();
                c.Dispose();
            }
            catch (Exception ex)
            {
                _warn("Stopping the caption capture failed", ex);
            }
        }
    }

    void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return;
        // The stream died (the output device went away, or its format changed): the next look-in starts a new one.
        _warn("The caption capture stopped", e.Exception);
        _broken = true;
    }

    /// <summary>
    /// Called a few times a second while captions are on. Windows sends no sound at all while nothing is playing,
    /// so the time that passed is fed as quiet (that is how the end of a sentence is noticed). Every few seconds it
    /// also checks that it is still listening to the default output device, which loopback does not follow by
    /// itself: true means <see cref="Restart"/> is due (on the UI thread, where the capture was made).
    /// </summary>
    public bool LookIn()
    {
        if (_disposed) return false;
        long now = Stopwatch.GetTimestamp();
        lock (_feed)
        {
            double quiet = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastSound), now).TotalSeconds;
            if (quiet >= 0.25)
            {
                _audio.AddSilence((int)(Math.Min(quiet, 10) * CaptionAudio.SampleRate));
                Volatile.Write(ref _lastSound, now);
            }
        }
        if (_fromFile || Stopwatch.GetElapsedTime(_lastDeviceCheck, now).TotalSeconds < 3) return false;
        _lastDeviceCheck = now;
        return _broken || OutputChanged();
    }

    /// <summary>Listen to whatever the default output device is now.</summary>
    public void Restart()
    {
        if (_fromFile) return;
        Stop();
        Start();
    }

    bool OutputChanged()
    {
        string? listeningTo;
        lock (_life)
        {
            if (_capture is null) return true;
            listeningTo = _deviceId;
        }
        try
        {
            using var devices = new MMDeviceEnumerator();
            using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID != listeningTo;
        }
        catch (Exception)
        {
            // No output device at all right now: keep what there is and look again later.
            return false;
        }
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        if (sender is not WasapiLoopbackCapture cap || _fromFile) return;
        var fmt = cap.WaveFormat;
        int channels = fmt.Channels, bytes = fmt.BitsPerSample / 8, frameBytes = bytes * channels;
        if (frameBytes == 0 || bytes is not (2 or 3 or 4)) return;
        int frames = e.BytesRecorded / frameBytes;
        if (frames == 0) return;
        // The shared-mode mix is 32-bit float in practice; 16- and 24-bit PCM are handled just in case.
        bool isFloat = bytes == 4 && fmt.Encoding != WaveFormatEncoding.Pcm;
        lock (_feed)
        {
            if (_mono.Length < frames) _mono = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++) sum += Sample(e.Buffer, f * frameBytes + c * bytes, bytes, isFloat);
                _mono[f] = sum / channels;
            }
            Feed(_mono.AsSpan(0, frames), fmt.SampleRate);
        }
    }

    static float Sample(byte[] buffer, int at, int bytes, bool isFloat)
    {
        if (isFloat) return BitConverter.ToSingle(buffer, at);
        if (bytes == 2) return BitConverter.ToInt16(buffer, at) / 32768f;
        if (bytes == 4) return BitConverter.ToInt32(buffer, at) / 2147483648f;
        // 24-bit: three bytes, low first, the top one carrying the sign.
        int value = buffer[at] | buffer[at + 1] * 256 | (sbyte)buffer[at + 2] * 65536;
        return value / 8388608f;
    }

    /// <summary>Mono sound at any rate, into the caption window at 16 kHz. Call with the feed lock held.</summary>
    void Feed(ReadOnlySpan<float> mono, int rate)
    {
        if (_resampler is null || _resampler.SourceRate != rate) _resampler = new CaptionResampler(rate);
        int n = _resampler.Process(mono, ref _out);
        _audio.Add(_out.AsSpan(0, n));
        Volatile.Write(ref _lastSound, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Plays a sound file into the captions instead of the speakers, at its real speed, then raises
    /// <see cref="FileEnded"/>. Returns how long the file is; throws when it cannot be read.
    /// </summary>
    public TimeSpan PlayFile(string path)
    {
        var reader = new AudioFileReader(path);
        var length = reader.TotalTime;
        StopFile();
        var cts = new CancellationTokenSource();
        _file = cts;
        _fromFile = true;
        Stop();
        _ = Task.Run(() => PlayAsync(reader, cts));
        return length;
    }

    async Task PlayAsync(AudioFileReader reader, CancellationTokenSource cts)
    {
        try
        {
            var fmt = reader.WaveFormat;
            int channels = fmt.Channels, rate = fmt.SampleRate;
            var block = new float[rate / 10 * channels];
            var mono = new float[rate / 10];
            var clock = Stopwatch.StartNew();
            long played = 0;
            int read;
            while (!cts.IsCancellationRequested && (read = reader.Read(block, 0, block.Length)) > 0)
            {
                int frames = read / channels;
                for (int f = 0; f < frames; f++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++) sum += block[f * channels + c];
                    mono[f] = sum / channels;
                }
                lock (_feed) Feed(mono.AsSpan(0, frames), rate);
                played += frames;
                // Keep to the file's own clock, as the speakers would.
                var ahead = TimeSpan.FromSeconds(played / (double)rate) - clock.Elapsed;
                if (ahead > TimeSpan.Zero) await Task.Delay(ahead, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _warn("Playing the test file into captions failed", ex);
        }
        finally
        {
            reader.Dispose();
            if (ReferenceEquals(_file, cts))
            {
                _file = null;
                _fromFile = false;
                if (!_disposed) FileEnded?.Invoke();
            }
            cts.Dispose();
        }
    }

    void StopFile()
    {
        var cts = _file;
        _file = null;
        _fromFile = false;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        _disposed = true;
        StopFile();
        Stop();
    }
}
