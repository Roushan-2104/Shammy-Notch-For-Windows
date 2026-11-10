using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Shammy.Core.Audio;
using Shammy.Core.OnlineVoice;

namespace Shammy.Modules.OnlineVoice;

/// <summary>
/// Plays one utterance through the default output device at the app's sound volume. One at a time: starting
/// another, or <see cref="Stop"/>, ends the one playing. Created and used on the UI thread; NAudio reports the
/// end of playback back on it.
/// </summary>
internal sealed class SpeechPlayer : IDisposable
{
    WaveOutEvent? _output;
    TaskCompletionSource? _playing;

    public bool IsPlaying => _output is not null;

    /// <summary>
    /// Plays the sound <paramref name="info"/> found in <paramref name="wav"/>; the task ends when it has been
    /// said (or stopped). Throws when there is no output device, which the caller treats like any failure.
    /// </summary>
    /// <param name="volume">0–100, the same scale as the notch's pings.</param>
    public Task PlayAsync(byte[] wav, WavInfo info, int volume)
    {
        Stop();
        var format = info.Format == 3
            ? WaveFormat.CreateIeeeFloatWaveFormat(info.SampleRate, info.Channels)
            : new WaveFormat(info.SampleRate, info.BitsPerSample, info.Channels);
        var source = new RawSourceWaveStream(new MemoryStream(wav, info.DataOffset, info.DataLength, writable: false), format);
        var levelled = new VolumeSampleProvider(source.ToSampleProvider()) { Volume = (float)SoundLevel.Gain(volume) };
        var output = new WaveOutEvent { DesiredLatency = 100 };
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, _) =>
        {
            done.TrySetResult();
            if (_output == output) _output = null;
            output.Dispose();
            source.Dispose();
        };
        try
        {
            output.Init(levelled);
            output.Play();
        }
        catch
        {
            output.Dispose();
            source.Dispose();
            throw;
        }
        _output = output;
        _playing = done;
        return done.Task;
    }

    public void Stop()
    {
        var output = _output;
        _output = null;
        _playing?.TrySetResult();
        _playing = null;
        if (output is null) return;
        try { output.Stop(); }
        catch (Exception ex) { Shammy.Infrastructure.Log.Warn("Stopping speech failed", ex); }
    }

    public void Dispose() => Stop();
}
