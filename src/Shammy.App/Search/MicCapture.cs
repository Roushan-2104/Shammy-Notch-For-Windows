using NAudio.Wave;
using Shammy.Infrastructure;

namespace Shammy.Search;

/// <summary>
/// One 16 kHz mono microphone capture that stops and frees itself safely. NAudio's WaveInEvent has two traps that
/// crashed Shammy 1.5.0 about once a day (an AccessViolation in waveInAddBuffer / waveInPrepareHeader) and could
/// leave the microphone open:
/// - Disposing straight after StopRecording frees the buffers while its thread may still be handing one back.
/// - A stop that lands before its thread has begun (a very quick tap) is overwritten when the thread starts, so the
///   thread waits forever with the device open.
/// So it is freed only when its thread reports it has stopped, and the stop is repeated until it does.
/// Start and Stop on the same thread (the UI thread); RecordingStopped is posted back to it.
/// </summary>
internal sealed class MicCapture
{
    public const int SampleRate = 16000;

    readonly WaveInEvent _wave;
    readonly EventHandler<WaveInEventArgs> _onData;
    volatile bool _stopped;

    MicCapture(WaveInEvent wave, EventHandler<WaveInEventArgs> onData)
    {
        _wave = wave;
        _onData = onData;
    }

    /// <summary>The capture, recording; null when the microphone didn't start (logged).</summary>
    public static MicCapture? Start(int device, int buffers, EventHandler<WaveInEventArgs> onData)
    {
        var wave = new WaveInEvent { WaveFormat = new WaveFormat(SampleRate, 16, 1), BufferMilliseconds = 40, NumberOfBuffers = buffers };
        if (device >= 0) wave.DeviceNumber = device;
        var capture = new MicCapture(wave, onData);
        wave.DataAvailable += onData;
        wave.RecordingStopped += (_, e) =>
        {
            capture._stopped = true;
            if (e.Exception is not null) Log.Warn("The microphone stopped by itself", e.Exception);
            try { wave.Dispose(); }
            catch (Exception ex) { Log.Warn("Freeing the microphone failed", ex); }
        };
        try
        {
            wave.StartRecording();
            return capture;
        }
        catch (Exception ex)
        {
            Log.Warn("Microphone failed to start", ex);
            try { wave.Dispose(); } catch (Exception) { }
            return null;
        }
    }

    /// <summary>Stops delivering audio now; the device closes and is freed once its thread has finished.</summary>
    public void Stop()
    {
        _wave.DataAvailable -= _onData;
        _ = StopUntilStoppedAsync();
    }

    async Task StopUntilStoppedAsync()
    {
        for (int attempt = 1; attempt <= 6 && !_stopped; attempt++)
        {
            try { _wave.StopRecording(); }
            catch (Exception ex) { Log.Warn("Stopping the microphone failed", ex); }
            await Task.Delay(150 * attempt);
        }
        if (!_stopped) Log.Warn("The microphone didn’t report stopping");
    }
}
