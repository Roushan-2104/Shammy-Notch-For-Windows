using System.Text;

namespace Shammy.Core.PhoneLink;

/// <summary>
/// Shammy's own two-note chimes (your call, 16 Nov), so you can tell by ear where a notification came from: falling
/// G5 → C5 for "from your phone" on the laptop; the phone plays the rising mirror for "from your laptop". Soft sine
/// notes with a quick attack and a gentle decay, about 0.35 s, written as a tiny in-memory WAV (no sound files ship).
/// </summary>
public static class Chime
{
    public const int Rate = 44100;
    public static readonly double[] FromPhone = [783.99, 523.25];
    public static readonly double[] FromLaptop = [523.25, 783.99];

    /// <summary>16-bit mono PCM samples: each note 170 ms, starting 120 ms after the last, at most <paramref name="volume"/>.</summary>
    public static short[] Samples(double[] notes, double volume = 0.22)
    {
        const double noteSec = 0.17, stepSec = 0.12;
        int total = (int)(Rate * (stepSec * (notes.Length - 1) + noteSec));
        var mix = new double[total];
        for (int n = 0; n < notes.Length; n++)
        {
            int start = (int)(Rate * stepSec * n), len = (int)(Rate * noteSec);
            for (int i = 0; i < len && start + i < total; i++)
            {
                double t = (double)i / Rate;
                // 6 ms attack (no click), then an exponential fade, plus a quiet octave for a softer, bell-like tone
                double env = Math.Min(1, t / 0.006) * Math.Exp(-t * 18);
                mix[start + i] += env * (Math.Sin(2 * Math.PI * notes[n] * t) + 0.25 * Math.Sin(4 * Math.PI * notes[n] * t));
            }
        }
        double peak = mix.Max(Math.Abs);
        double scale = peak > 0 ? volume * short.MaxValue / peak : 0;
        return mix.Select(v => (short)Math.Round(v * scale)).ToArray();
    }

    /// <summary>The samples as a WAV file (RIFF, PCM 16-bit mono, 44.1 kHz).</summary>
    public static byte[] Wav(short[] samples)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII);
        int data = samples.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(data);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
