namespace Shammy.Core.Captions;

/// <param name="Samples">16 kHz mono, at most one window long, starting where the speech in it starts.</param>
/// <param name="Overlaps">True when part of it was in an earlier chunk, so its transcript repeats shown words.</param>
public sealed record CaptionChunk(float[] Samples, bool Overlaps);

/// <summary>
/// The rolling window of sound that captions are made from, and the decision of when it is worth transcribing.
/// It keeps the last few seconds; every couple of seconds of new sound, if any of it was louder than the noise
/// floor, the window is handed out. Silence is never handed out, and nothing is thrown away because of it. The
/// window is cut to start where its speech starts and, after a pause, at the new sentence, so the model is not
/// given quiet to invent words for and finished sentences are not transcribed again.
/// Safe to feed from one thread and take from another.
/// </summary>
public sealed class CaptionAudio
{
    public const int SampleRate = 16000;

    /// <summary>Loudness is judged in 30 ms pieces.</summary>
    const int Frame = SampleRate * 30 / 1000;

    /// <summary>Sound this short in a step is a click, not speech.</summary>
    const int MinVoicedFrames = 5;

    /// <summary>Quiet kept before the first word so its start is not clipped.</summary>
    const int Lead = SampleRate * 3 / 10;

    /// <summary>Quiet this long after speech means the sentence is over: transcribe now rather than wait out the step.</summary>
    const int SentenceEnd = SampleRate * 6 / 10;

    /// <summary>Quiet this long is a pause: the next window starts after it.</summary>
    const int Pause = SampleRate;

    /// <summary>The model is unreliable on less than about a second of speech that is still going on.</summary>
    const int MinSpeech = SampleRate * 12 / 10;

    readonly object _gate = new();
    readonly float[] _ring;
    readonly int _step;
    readonly float _floor;
    long _total, _decided, _taken = -1;
    (long Decided, long Taken) _before;

    public CaptionAudio(double windowSeconds = 8, double stepSeconds = 2, float noiseFloor = 0.005f)
    {
        _ring = new float[(int)(windowSeconds * SampleRate)];
        _step = (int)(stepSeconds * SampleRate);
        _floor = noiseFloor;
    }

    /// <summary>Every sample ever fed (sound and filled-in silence).</summary>
    public long Total
    {
        get { lock (_gate) return _total; }
    }

    public void Add(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            // More than a whole window at once: only its end can matter.
            if (samples.Length > _ring.Length)
            {
                _total += samples.Length - _ring.Length;
                samples = samples[^_ring.Length..];
            }
            int at = (int)(_total % _ring.Length);
            int head = Math.Min(samples.Length, _ring.Length - at);
            samples[..head].CopyTo(_ring.AsSpan(at));
            samples[head..].CopyTo(_ring);
            _total += samples.Length;
        }
    }

    /// <summary>Time passed with nothing playing (Windows sends no sound at all then): it counts as quiet.</summary>
    public void AddSilence(int samples)
    {
        if (samples <= 0) return;
        Add(new float[Math.Min(samples, _ring.Length)]);
    }

    /// <summary>The window to transcribe now, or false when it is too soon or there was only silence.</summary>
    public bool TryTake(out CaptionChunk chunk)
    {
        chunk = null!;
        lock (_gate)
        {
            long from = Math.Max(0, _total - _ring.Length);
            long decided = Math.Max(_decided, from);
            if (_total - decided < Frame * MinVoicedFrames) return false;

            // Where the sound is: whole 30 ms pieces, counted back from the newest sample.
            int frames = (int)((_total - from) / Frame);
            long origin = _total - (long)frames * Frame;
            var voiced = new bool[frames];
            for (int f = 0; f < frames; f++) voiced[f] = Loud(origin + (long)f * Frame);
            long StartOf(int f) => origin + (long)f * Frame;

            // What counts as new sound reaches a little before the last chunk's end: a word that chunk cut in half,
            // or one it heard with nothing after it, is worth one more pass once the speaker has gone on or stopped.
            long heardFrom = decided - Lead;
            int firstVoiced = -1, lastVoiced = -1, freshVoiced = 0;
            for (int f = 0; f < frames; f++)
            {
                if (!voiced[f]) continue;
                if (firstVoiced < 0) firstVoiced = f;
                lastVoiced = f;
                if (StartOf(f) >= heardFrom) freshVoiced++;
            }
            bool due = _total - decided >= _step;
            long quietTail = lastVoiced < 0 ? _total - from : _total - (StartOf(lastVoiced) + Frame);
            if (freshVoiced < MinVoicedFrames)
            {
                // A step of quiet: nothing to transcribe, and nothing to wait for. Unless a sound has only just
                // begun: that may be the first word, so look again in a moment instead of a whole step later.
                if (due && quietTail >= Lead) _decided = _total;
                return false;
            }
            bool sentenceOver = quietTail >= SentenceEnd;
            if (!due && !sentenceOver) return false;

            // Start where the speech starts; after a pause the previous chunk already heard out, at the new sentence.
            long start = Math.Max(from, StartOf(firstVoiced) - Lead);
            int quietFrom = -1;
            for (int f = firstVoiced; f <= lastVoiced; f++)
            {
                if (!voiced[f])
                {
                    if (quietFrom < 0) quietFrom = f;
                    continue;
                }
                if (quietFrom >= 0 && StartOf(f) - StartOf(quietFrom) >= Pause && StartOf(quietFrom) + Lead <= _taken)
                    start = Math.Max(start, StartOf(f) - Lead);
                quietFrom = -1;
            }
            // Speech that has only just begun reads better with a little more of it.
            if (!sentenceOver && _total - start < Math.Min(MinSpeech, _ring.Length) && _total - decided < _step + MinSpeech) return false;

            bool overlaps = false;
            for (int f = 0; f < frames && !overlaps; f++)
                overlaps = voiced[f] && StartOf(f) >= start && StartOf(f) + Frame <= _taken;

            var samples = new float[_total - start];
            for (int i = 0; i < samples.Length; i++) samples[i] = _ring[(start + i) % _ring.Length];
            _before = (_decided, _taken);
            _decided = _taken = _total;
            chunk = new CaptionChunk(samples, overlaps);
            return true;
        }
    }

    /// <summary>The chunk just taken was not transcribed after all (the model was busy): offer it again.</summary>
    public void Undo()
    {
        lock (_gate) (_decided, _taken) = _before;
    }

    bool Loud(long at)
    {
        double sum = 0;
        for (int i = 0; i < Frame; i++)
        {
            float v = _ring[(at + i) % _ring.Length];
            sum += v * v;
        }
        return Math.Sqrt(sum / Frame) > _floor;
    }
}
