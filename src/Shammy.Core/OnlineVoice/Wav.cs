using Shammy.Core.Search;

namespace Shammy.Core.OnlineVoice;

/// <summary>Where the sound is inside a WAV file, and what it is.</summary>
/// <param name="Format">1 = PCM, 3 = floating point.</param>
public sealed record WavInfo(int Format, int SampleRate, int Channels, int BitsPerSample, int DataOffset, int DataLength)
{
    public int BlockAlign => Channels * BitsPerSample / 8;

    public TimeSpan Duration => TimeSpan.FromSeconds(DataLength / (double)(SampleRate * BlockAlign));
}

/// <summary>
/// WAV files both ways: the recording is written as 16 kHz mono 16-bit PCM for upload, and the speech that comes
/// back is checked (and located) before it is played or cached.
/// </summary>
public static class Wav
{
    public const int HeaderLength = 44;

    /// <summary>A complete 16-bit PCM mono WAV file from samples in −1…1 (louder ones are clipped).</summary>
    public static byte[] Pcm16(ReadOnlySpan<float> samples, int sampleRate)
    {
        int dataLength = samples.Length * 2;
        var file = new byte[HeaderLength + dataLength];
        WriteHeader(file, sampleRate, 1, 16, dataLength);
        for (int i = 0; i < samples.Length; i++)
        {
            short s = (short)Math.Clamp(Math.Round(samples[i] * 32767.0), short.MinValue, short.MaxValue);
            file[HeaderLength + i * 2] = (byte)s;
            file[HeaderLength + i * 2 + 1] = (byte)(s >> 8);
        }
        return file;
    }

    /// <summary>The 44-byte header of a PCM WAV file: RIFF, a 16-byte "fmt " chunk, then "data".</summary>
    public static void WriteHeader(Span<byte> header, int sampleRate, int channels, int bitsPerSample, int dataLength)
    {
        if (header.Length < HeaderLength) throw new ArgumentException("A WAV header needs 44 bytes", nameof(header));
        int blockAlign = channels * bitsPerSample / 8;
        Ascii(header, 0, "RIFF");
        Int32(header, 4, 36 + dataLength);
        Ascii(header, 8, "WAVE");
        Ascii(header, 12, "fmt ");
        Int32(header, 16, 16);
        Int16(header, 20, 1);
        Int16(header, 22, channels);
        Int32(header, 24, sampleRate);
        Int32(header, 28, sampleRate * blockAlign);
        Int16(header, 32, blockAlign);
        Int16(header, 34, bitsPerSample);
        Ascii(header, 36, "data");
        Int32(header, 40, dataLength);
    }

    /// <summary>
    /// Finds the sound in a WAV file. True only for PCM or floating-point audio the app can play; a "data" length
    /// of zero or more than the file holds (a file written as a stream) means "the rest of the file".
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> file, out WavInfo info)
    {
        info = null!;
        if (file.Length < 12 || !Is(file, 0, "RIFF") || !Is(file, 8, "WAVE")) return false;
        int format = 0, rate = 0, channels = 0, bits = 0;
        bool haveFormat = false;
        long pos = 12;
        while (pos + 8 <= file.Length)
        {
            int at = (int)pos;
            long size = BitConverter.ToUInt32(file.Slice(at + 4, 4));
            long start = pos + 8;
            if (Is(file, at, "fmt ") && size >= 16 && start + 16 <= file.Length)
            {
                int s = (int)start;
                format = BitConverter.ToUInt16(file.Slice(s, 2));
                channels = BitConverter.ToUInt16(file.Slice(s + 2, 2));
                rate = BitConverter.ToInt32(file.Slice(s + 4, 4));
                bits = BitConverter.ToUInt16(file.Slice(s + 14, 2));
                // "Extensible": the real format is the first two bytes of the sub-format.
                if (format == 0xFFFE && size >= 40 && start + 26 <= file.Length) format = BitConverter.ToUInt16(file.Slice(s + 24, 2));
                haveFormat = true;
            }
            else if (Is(file, at, "data"))
            {
                if (!haveFormat) return false;
                long remaining = file.Length - start;
                long length = size == 0 || size > remaining ? remaining : size;
                if (format is not (1 or 3) || channels is < 1 or > 8 || rate is < 4000 or > 192000 || bits is not (8 or 16 or 24 or 32)) return false;
                if (format == 3 && bits != 32) return false;
                int block = channels * bits / 8;
                length -= length % block;
                if (length <= 0) return false;
                info = new WavInfo(format, rate, channels, bits, (int)start, (int)length);
                return true;
            }
            // Chunks are padded to an even length.
            pos = start + size + (size & 1);
        }
        return false;
    }

    static bool Is(ReadOnlySpan<byte> file, int at, string tag) =>
        file.Length >= at + 4 && file[at] == tag[0] && file[at + 1] == tag[1] && file[at + 2] == tag[2] && file[at + 3] == tag[3];

    static void Ascii(Span<byte> b, int at, string text)
    {
        for (int i = 0; i < text.Length; i++) b[at + i] = (byte)text[i];
    }

    static void Int32(Span<byte> b, int at, int value) => BitConverter.TryWriteBytes(b.Slice(at, 4), value);

    static void Int16(Span<byte> b, int at, int value) => BitConverter.TryWriteBytes(b.Slice(at, 2), (short)value);
}

/// <summary>The recording as it is uploaded: quiet ends trimmed (less to send), never longer than the service takes.</summary>
public static class SttAudio
{
    public static byte[] ToWav(float[] samples, int sampleRate, double maxSeconds = GnaniApi.SttMaxSeconds)
    {
        var trimmed = AudioTrim.TrimSilence(samples, sampleRate);
        int max = (int)(sampleRate * maxSeconds);
        return Wav.Pcm16(trimmed.Length > max ? trimmed.AsSpan(0, max) : trimmed, sampleRate);
    }
}
