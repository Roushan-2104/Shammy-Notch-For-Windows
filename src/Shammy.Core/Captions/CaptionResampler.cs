namespace Shammy.Core.Captions;

/// <summary>
/// Turns sound at the PC's own rate (44.1, 48, 96 kHz) into the 16 kHz the voice model listens at, a piece at a
/// time as it arrives. Everything above what 16 kHz can carry is filtered out first, so music and hiss up there
/// are not folded down into the speech. Pieces of any size give the same result as one long piece.
/// </summary>
public sealed class CaptionResampler
{
    public const int TargetRate = CaptionAudio.SampleRate;

    /// <summary>How finely the position between two input samples is resolved.</summary>
    const int Phases = 64;

    /// <summary>Filter reach on each side, in periods of its cut-off: more is sharper and costs more.</summary>
    const int Reach = 8;

    readonly double _step;
    readonly int _half;
    readonly float[][] _kernel;
    float[] _in;
    int _count;
    double _pos;

    public CaptionResampler(int sourceRate)
    {
        if (sourceRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRate));
        SourceRate = sourceRate;
        _step = sourceRate / (double)TargetRate;
        // Cut off just under the lower of the two rates' limits (in cycles per input sample).
        double cutoff = 0.46 / Math.Max(1, _step);
        _half = (int)Math.Ceiling(Reach / (2 * cutoff));
        _kernel = new float[Phases][];
        for (int phase = 0; phase < Phases; phase++)
        {
            double frac = phase / (double)Phases;
            var taps = new float[2 * _half];
            double sum = 0;
            for (int k = 0; k < taps.Length; k++)
            {
                // Tap k weighs the input sample (k - half + 1) places from the one just before the output position.
                double d = k - _half + 1 - frac;
                double x = 2 * cutoff * d;
                double sinc = Math.Abs(x) < 1e-9 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
                double window = 0.5 + 0.5 * Math.Cos(Math.PI * Math.Clamp(d / _half, -1, 1));
                taps[k] = (float)(sinc * window);
                sum += taps[k];
            }
            for (int k = 0; k < taps.Length; k++) taps[k] = (float)(taps[k] / sum);
            _kernel[phase] = taps;
        }
        // Quiet before the first sample, so the first outputs have a full filter behind them.
        _in = new float[Math.Max(4096, 4 * _half)];
        _count = _half - 1;
        _pos = _half - 1;
    }

    public int SourceRate { get; }

    /// <summary>Adds a piece of mono sound and writes what can be made from it so far; returns how many samples
    /// were written to <paramref name="output"/> (which is grown when it is too small).</summary>
    public int Process(ReadOnlySpan<float> input, ref float[] output)
    {
        if (SourceRate == TargetRate)
        {
            if (output.Length < input.Length) output = new float[input.Length];
            input.CopyTo(output);
            return input.Length;
        }
        if (_count + input.Length > _in.Length) Array.Resize(ref _in, Math.Max(_in.Length * 2, _count + input.Length));
        input.CopyTo(_in.AsSpan(_count));
        _count += input.Length;

        int room = (int)(input.Length / _step) + 2;
        if (output.Length < room) output = new float[room];
        int made = 0;
        while (true)
        {
            int at = (int)Math.Floor(_pos);
            int phase = (int)((_pos - at) * Phases + 0.5);
            if (phase == Phases)
            {
                at++;
                phase = 0;
            }
            // The filter needs the samples from at - half + 1 to at + half.
            if (at + _half >= _count || made >= output.Length) break;
            var taps = _kernel[phase];
            int from = at - _half + 1;
            double sum = 0;
            for (int k = 0; k < taps.Length; k++) sum += _in[from + k] * taps[k];
            output[made++] = (float)sum;
            _pos += _step;
        }

        // Forget the input no later output can reach.
        int keepFrom = Math.Max(0, (int)Math.Floor(_pos) - _half + 1);
        if (keepFrom > 0)
        {
            _count -= keepFrom;
            Array.Copy(_in, keepFrom, _in, 0, _count);
            _pos -= keepFrom;
        }
        return made;
    }
}
