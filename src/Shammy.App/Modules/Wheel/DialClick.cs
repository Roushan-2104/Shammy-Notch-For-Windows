using NAudio.Wave;
using Shammy.Core.Audio;
using Shammy.Core.Wheel;
using Shammy.Infrastructure;

namespace Shammy.Modules.Wheel;

/// <summary>
/// The dial's soft click (Settings › Wheel, off by default): a tick of a few milliseconds rendered once in memory
/// (<see cref="DialTick"/>), at the notch sounds' volume and silent when those are off. A fast spin clicks at most
/// every 35 ms.
/// </summary>
internal static class DialClick
{
    static byte[]? _pcm;
    static long _lastTick;

    public static void Play()
    {
        if (!NotchSound.Enabled || NotchSound.Volume <= 0) return;
        long now = Environment.TickCount64;
        if (now - _lastTick < 35) return;
        _lastTick = now;
        try
        {
            if (_pcm is null)
            {
                var samples = DialTick.Render();
                var bytes = new byte[samples.Length * 2];
                Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                _pcm = bytes;
            }
            var pcm = _pcm;
            double gain = SoundLevel.Gain(NotchSound.Volume);
            if (gain < 0.999)
            {
                pcm = new byte[_pcm.Length];
                for (int i = 0; i + 1 < pcm.Length; i += 2)
                {
                    short s = SoundLevel.Scale(BitConverter.ToInt16(_pcm, i), gain);
                    pcm[i] = (byte)s;
                    pcm[i + 1] = (byte)(s >> 8);
                }
            }
            var output = new WaveOutEvent { DesiredLatency = 60 };
            output.PlaybackStopped += (_, _) => output.Dispose();
            output.Init(new RawSourceWaveStream(new MemoryStream(pcm), new WaveFormat(DialTick.SampleRate, 16, 1)));
            output.Play();
        }
        catch (Exception ex)
        {
            Log.Warn("Playing the wheel's click failed", ex);
        }
    }
}
