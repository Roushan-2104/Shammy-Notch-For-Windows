using NAudio.Wave;
using Shammy.Infrastructure;
using Shammy.Search;

namespace Shammy.Modules.Stage;

/// <summary>
/// Stage's ear: only how loud the microphone is, never what is said. Opens the microphone chosen in Settings (the
/// Windows default otherwise) in shared mode, so a meeting app can use it at the same time, and keeps the loudest
/// 40 ms level since it was last read. Nothing is recorded or kept. Open only while Stage is open.
/// </summary>
internal sealed class StageMic : IDisposable
{
    const int SampleRate = 16000;

    WaveInEvent? _wave;
    double _loudest;
    long _blocks, _silentBlocks;

    public bool IsOpen => _wave is not null;

    /// <summary>Blocks heard so far, and how many were exact digital silence (what a privacy block sounds like).</summary>
    public long Blocks => Interlocked.Read(ref _blocks);

    /// <summary>
    /// Every block so far was exact zeros: Windows' microphone privacy setting is keeping desktop apps out (or the
    /// device is muted in hardware). Only meaningful after a second or so.
    /// </summary>
    public bool Blocked => Blocks >= 25 && Interlocked.Read(ref _silentBlocks) == Blocks;

    public bool Open(out string? problem)
    {
        problem = null;
        Close();
        try
        {
            if (WaveInEvent.DeviceCount == 0)
            {
                problem = "No microphone found";
                return false;
            }
            var wave = new WaveInEvent { WaveFormat = new WaveFormat(SampleRate, 16, 1), BufferMilliseconds = 40, NumberOfBuffers = 3 };
            int device = Shammy.Core.Settings.DeviceMatch.Find(MicRecorder.DeviceNames(), MicRecorder.PreferredDevice);
            if (device >= 0) wave.DeviceNumber = device;
            wave.DataAvailable += OnData;
            wave.StartRecording();
            _wave = wave;
            Interlocked.Exchange(ref _blocks, 0);
            Interlocked.Exchange(ref _silentBlocks, 0);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Stage: the microphone didn’t open", ex);
            problem = "Microphone unavailable";
            return false;
        }
    }

    /// <summary>The loudest RMS level (0–1) since the last call, then starts again.</summary>
    public double TakeLevel()
    {
        double v = Volatile.Read(ref _loudest);
        Volatile.Write(ref _loudest, 0);
        return v;
    }

    // On NAudio's capture thread.
    void OnData(object? sender, WaveInEventArgs e)
    {
        int n = e.BytesRecorded / 2;
        if (n <= 0) return;
        double sum = 0;
        bool silent = true;
        for (int i = 0; i < n; i++)
        {
            short raw = BitConverter.ToInt16(e.Buffer, i * 2);
            if (raw != 0) silent = false;
            double s = raw / 32768.0;
            sum += s * s;
        }
        double rms = Math.Sqrt(sum / n);
        if (rms > Volatile.Read(ref _loudest)) Volatile.Write(ref _loudest, rms);
        Interlocked.Increment(ref _blocks);
        if (silent) Interlocked.Increment(ref _silentBlocks);
    }

    public void Close()
    {
        var wave = _wave;
        _wave = null;
        Volatile.Write(ref _loudest, 0);
        if (wave is null) return;
        try
        {
            wave.DataAvailable -= OnData;
            wave.StopRecording();
            wave.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Stage: closing the microphone failed", ex);
        }
    }

    public void Dispose() => Close();
}
