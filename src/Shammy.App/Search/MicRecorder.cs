using Shammy.Infrastructure;
using NAudio.Wave;

namespace Shammy.Search;

/// <summary>Records 16 kHz mono (what Whisper wants) and exposes a live level for the UI.</summary>
internal sealed class MicRecorder
{
    public const int SampleRate = MicCapture.SampleRate;
    const int MaxSamples = SampleRate * 30;

    readonly object _gate = new();
    List<float> _samples = new();
    float[] _chunk = Array.Empty<float>();
    MicCapture? _capture;
    float _level;

    /// <summary>The microphone's name from settings (MicrophoneDeviceId); null records from the Windows default.</summary>
    public static string? PreferredDevice { get; set; }

    /// <summary>Input devices as Windows lists them (names truncated to 31 characters by WinMM).</summary>
    public static IReadOnlyList<string> DeviceNames()
    {
        var names = new List<string>();
        try
        {
            for (int i = 0; i < WaveInEvent.DeviceCount; i++) names.Add(WaveInEvent.GetCapabilities(i).ProductName);
        }
        catch (Exception ex)
        {
            Log.Warn("Listing microphones failed", ex);
        }
        return names;
    }

    public float Level => Volatile.Read(ref _level);
    public bool IsRecording => _capture is not null;

    public bool Start(out string? error)
    {
        error = null;
        Stop();
        lock (_gate) _samples = new List<float>(SampleRate * 10);
        try
        {
            if (WaveInEvent.DeviceCount == 0)
            {
                error = "No microphone found";
                return false;
            }
            // The chosen microphone (settings), or the Windows default when it's unset or unplugged.
            var capture = MicCapture.Start(Shammy.Core.Settings.DeviceMatch.Find(DeviceNames(), PreferredDevice), 4, OnData);
            if (capture is null)
            {
                error = "Microphone unavailable";
                return false;
            }
            _capture = capture;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Microphone failed to start", ex);
            error = "Microphone unavailable";
            return false;
        }
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        int n = e.BytesRecorded / 2;
        double sum = 0;
        // One reused buffer for the 40 ms blocks (the recording thread is the only one that touches it).
        if (_chunk.Length < n) _chunk = new float[n];
        var chunk = _chunk;
        for (int i = 0; i < n; i++)
        {
            float s = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
            chunk[i] = s;
            sum += s * s;
        }
        // Whisper only listens to 30 s; a key held down by accident mustn't buffer (and later process) minutes.
        lock (_gate)
        {
            int room = MaxSamples - _samples.Count;
            if (room > 0) _samples.AddRange(new ArraySegment<float>(chunk, 0, Math.Min(room, n)));
        }
        float rms = n > 0 ? (float)Math.Sqrt(sum / n) : 0;
        Volatile.Write(ref _level, Math.Min(1, rms * 6));
    }

    /// <summary>What has been recorded so far, while recording goes on (dictation's live words).</summary>
    public float[] Snapshot()
    {
        lock (_gate) return _samples.ToArray();
    }

    public float[] Stop()
    {
        var capture = _capture;
        _capture = null;
        capture?.Stop();
        Volatile.Write(ref _level, 0);
        lock (_gate)
        {
            // The recording leaves with the caller; the buffer (up to 30 s, ~2 MB) isn't kept between holds.
            var recorded = _samples.ToArray();
            _samples = new List<float>();
            return recorded;
        }
    }

    /// <summary>
    /// True when at least ~200 ms of the recording is louder than background noise. This guards
    /// Whisper, which invents text ("Thank you.") when fed silence.
    /// </summary>
    public static bool HasSpeech(float[] samples)
    {
        const int frame = SampleRate * 30 / 1000;
        int voiced = 0;
        for (int start = 0; start + frame <= samples.Length; start += frame)
        {
            double sum = 0;
            for (int i = start; i < start + frame; i++) sum += samples[i] * samples[i];
            if (Math.Sqrt(sum / frame) > 0.018) voiced++;
        }
        return voiced >= 7;
    }

    /// <summary>All-zero audio means Windows' microphone privacy setting is blocking desktop apps.</summary>
    public static bool IsSilentBlock(float[] samples) => samples.Length > SampleRate / 4 && samples.All(s => s == 0);
}
