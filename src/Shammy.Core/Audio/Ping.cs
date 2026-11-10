namespace Shammy.Core.Audio;

public enum PingKind
{
    /// <summary>A notification: one soft, bright tap.</summary>
    Notification,

    /// <summary>A timer or reminder: two rising notes, a little more noticeable.</summary>
    Alert,

    /// <summary>The cat's purr: a low, soft rumble for a moment.</summary>
    Purr,
}

/// <summary>
/// Shammy's own sounds, synthesised rather than shipped as files: a short bell-like tone (a sine with a
/// quieter octave-and-a-fifth partial) that starts softly and dies away, so it reads as a "ping" and never as
/// Windows' default chime.
/// </summary>
public static class Ping
{
    public const int SampleRate = 48000;

    // Peak level well under full scale: it should be heard, not startle.
    const double Level = 0.32;

    /// <summary>16-bit mono PCM at <see cref="SampleRate"/>.</summary>
    public static short[] Render(PingKind kind) => kind switch
    {
        PingKind.Purr => Purr(),
        // E6
        PingKind.Notification => Mix(Note(1318.5, 0.0, 0.42)),
        // E6 then A6
        _ => Mix(Note(1318.5, 0.0, 0.5), Note(1760.0, 0.14, 0.6)),
    };

    /// <summary>
    /// A purr is a train of soft thumps about 25 times a second, a little faster breathing out than in. Each
    /// thump is a short low burst; the fundamental is below what small speakers play, so the burst carries
    /// partials they can. Quieter than the pings: it is a comfort sound, not a signal.
    /// </summary>
    static short[] Purr()
    {
        const double length = 1.7, level = 0.2;
        var samples = new double[(int)(length * SampleRate)];
        var noise = new Random(7);
        double phase = 0, smooth = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double t = (double)i / SampleRate;
            // Breathing in for the first 0.8 s, then out: the rate rises a little and so does the loudness.
            bool breathingOut = t > 0.8;
            double rate = breathingOut ? 27 : 23.5;
            phase += rate / SampleRate;
            double seconds = (phase - Math.Floor(phase)) / rate;
            double thump = Math.Exp(-seconds * 85);
            smooth += ((noise.NextDouble() * 2 - 1) - smooth) * 0.06;
            double body = Math.Sin(2 * Math.PI * 118 * seconds) + 0.55 * Math.Sin(2 * Math.PI * 236 * seconds)
                        + 0.25 * Math.Sin(2 * Math.PI * 354 * seconds) + 2.2 * smooth;
            double breath = breathingOut ? 1 : 0.72;
            double dip = 1 - 0.45 * Math.Exp(-Math.Pow((t - 0.8) / 0.07, 2));
            double fade = Math.Min(1, t / 0.25) * Math.Min(1, (length - t) / 0.4);
            samples[i] = body * thump * breath * dip * fade;
        }
        double peak = samples.Max(Math.Abs);
        double scale = peak > 0 ? level / peak : 0;
        return samples.Select(s => (short)Math.Round(s * scale * short.MaxValue)).ToArray();
    }

    static (double Freq, double Start, double Length) Note(double freq, double start, double length) => (freq, start, length);

    static short[] Mix(params (double Freq, double Start, double Length)[] notes)
    {
        double end = notes.Max(n => n.Start + n.Length);
        var samples = new double[(int)(end * SampleRate)];
        foreach (var (freq, start, length) in notes)
        {
            int from = (int)(start * SampleRate);
            int count = Math.Min((int)(length * SampleRate), samples.Length - from);
            for (int i = 0; i < count; i++)
            {
                double t = (double)i / SampleRate;
                // 4 ms fade-in (no click), then an exponential ring-down; the overtone fades faster, like a bell.
                double attack = Math.Min(1, t / 0.004);
                double tone = Math.Sin(2 * Math.PI * freq * t) * Math.Exp(-t / 0.11)
                            + 0.25 * Math.Sin(2 * Math.PI * freq * 3 * t) * Math.Exp(-t / 0.035);
                // A short tail fade so the note ends at exactly zero.
                double tail = Math.Min(1, (count - i) / (0.01 * SampleRate));
                samples[from + i] += tone * attack * tail;
            }
        }
        double peak = samples.Max(Math.Abs);
        double scale = peak > 0 ? Level / peak : 0;
        return samples.Select(s => (short)Math.Round(s * scale * short.MaxValue)).ToArray();
    }
}
