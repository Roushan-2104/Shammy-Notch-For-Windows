namespace Shammy.Core.Record;

/// <summary>
/// One sound source (the PC's sound, or the microphone) laid on the recording's own clock, so sound and picture
/// stay together. Windows only hands over sound while something is playing, and in bursts: each burst is placed
/// where the clock says it belongs (it ends "now"), the quiet in between stays silent, and a source that slowly
/// drifts from the clock is pulled back instead of sliding further and further from the picture.
/// Positions are frames since the recording started (one frame = one sample for every channel). Safe to write
/// from the capture thread while the encoder reads.
/// </summary>
public sealed class AudioTimeline
{
    readonly object _gate = new();
    readonly float[] _ring;
    readonly int _channels, _capacity, _gap;
    long _lo, _end;
    bool _any;

    /// <param name="gapSeconds">How far a burst may sit from where the clock expects it before it is moved there.</param>
    /// <param name="bufferSeconds">How much sound is held for a reader that has fallen behind.</param>
    public AudioTimeline(int sampleRate, int channels, double gapSeconds = 0.05, double bufferSeconds = 4)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        _channels = channels;
        _gap = Math.Max(1, (int)(sampleRate * gapSeconds));
        _capacity = Math.Max(_gap * 4, (int)(sampleRate * bufferSeconds));
        _ring = new float[_capacity * channels];
    }

    /// <summary>Frames waiting to be read.</summary>
    public int Buffered
    {
        get
        {
            lock (_gate) return (int)(_end - _lo);
        }
    }

    /// <summary>The position the next burst would continue from.</summary>
    public long End
    {
        get
        {
            lock (_gate) return _end;
        }
    }

    /// <summary>
    /// Adds a burst of interleaved samples that finished arriving when the clock stood at
    /// <paramref name="nowFrame"/>. Sound from before the recording started is dropped.
    /// </summary>
    public void Write(ReadOnlySpan<float> interleaved, long nowFrame)
    {
        int frames = interleaved.Length / _channels;
        if (frames == 0) return;
        lock (_gate)
        {
            long expected = nowFrame - frames;
            // First burst, a silence since the last one, or drift either way: put it where the clock says.
            long at = !_any || Math.Abs(expected - _end) > _gap ? expected : _end;
            _any = true;
            if (at > _end)
            {
                // The skipped stretch must read as silence, not as whatever the ring held a few seconds ago.
                for (long gap = Math.Max(_end, at - _capacity); gap < at; gap++)
                    Array.Clear(_ring, (int)(gap % _capacity) * _channels, _channels);
            }

            int skip = 0;
            if (at < _lo)
            {
                // Before the start of the recording, or already read past: that part is gone.
                skip = (int)Math.Min(frames, _lo - at);
                at += skip;
            }
            for (int f = skip; f < frames; f++, at++)
            {
                int slot = (int)(at % _capacity) * _channels;
                for (int c = 0; c < _channels; c++) _ring[slot + c] = interleaved[f * _channels + c];
            }
            _end = Math.Max(at, _lo);
            // A reader that stopped reading loses the oldest sound, not the newest.
            if (_end - _lo > _capacity) _lo = _end - _capacity;
        }
    }

    /// <summary>
    /// Mixes (adds) the frames from <paramref name="position"/> into <paramref name="mix"/> (interleaved, one
    /// slot per channel per frame); parts with no sound add nothing. Everything up to the end of this read is
    /// then forgotten. Returns how many frames the source covered (sound, or the quiet between two sounds).
    /// </summary>
    public int Read(long position, Span<float> mix)
    {
        int frames = mix.Length / _channels, had = 0;
        lock (_gate)
        {
            long from = Math.Max(position, _lo), to = Math.Min(position + frames, _end);
            for (long at = from; at < to; at++)
            {
                int slot = (int)(at % _capacity) * _channels, into = (int)(at - position) * _channels;
                for (int c = 0; c < _channels; c++) mix[into + c] += _ring[slot + c];
                had++;
            }
            _lo = Math.Max(_lo, position + frames);
            if (_end < _lo) _end = _lo;
        }
        return had;
    }
}

/// <summary>Turns mixed sound into the 16-bit samples the video file is fed.</summary>
public static class PcmMix
{
    /// <summary>Each sample clipped to -1..1 and written as little-endian 16-bit; <paramref name="pcm"/> needs two bytes a sample.</summary>
    public static void ToInt16(ReadOnlySpan<float> mix, Span<byte> pcm)
    {
        for (int i = 0; i < mix.Length; i++)
        {
            float v = mix[i];
            if (float.IsNaN(v)) v = 0;
            int s = (int)MathF.Round(Math.Clamp(v, -1f, 1f) * short.MaxValue);
            pcm[i * 2] = (byte)s;
            pcm[i * 2 + 1] = (byte)(s >> 8);
        }
    }

    /// <summary>
    /// Any channel layout as stereo: mono on both sides, the first two channels of anything wider (front left and
    /// right). Returns how many stereo frames were written to <paramref name="stereo"/>.
    /// </summary>
    public static int ToStereo(ReadOnlySpan<float> interleaved, int channels, Span<float> stereo)
    {
        if (channels <= 0) return 0;
        int frames = Math.Min(interleaved.Length / channels, stereo.Length / 2);
        for (int f = 0; f < frames; f++)
        {
            float left = interleaved[f * channels];
            stereo[f * 2] = left;
            stereo[f * 2 + 1] = channels == 1 ? left : interleaved[f * channels + 1];
        }
        return frames;
    }
}
