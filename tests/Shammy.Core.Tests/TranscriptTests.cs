using Shammy.Core.Search;

namespace Shammy.Core.Tests;

public class TranscriptCleanerTests
{
    [Theory]
    [InlineData("Play tombhigong. Play tombhigong.", "Play tombhigong.")]
    [InlineData("Play Sleepwalk. Play Sleepwalk.", "Play Sleepwalk.")]
    [InlineData("Take a screenshot. Take a screenshot.", "Take a screenshot.")]
    [InlineData("screen shot. Take a screenshot.", "Take a screenshot.")]
    [InlineData("Take a screenshot. screenshot.", "Take a screenshot.")]
    [InlineData("play kaahe mose play kaahe mose", "play kaahe mose")]
    [InlineData("next next next", "next")]
    [InlineData("What is the weather? What is the weather?", "What is the weather?")]
    public void Collapses_repeats(string input, string expected) =>
        Assert.Equal(expected, TranscriptCleaner.RemoveRepeats(input));

    [Theory]
    [InlineData("Remind me in 10 minutes to call mom.")]
    [InlineData("Translate good morning to Hindi.")]
    [InlineData("Is New York in New York state?")]
    [InlineData("Set a timer. Then play music.")]
    [InlineData("bye bye")]
    public void Leaves_normal_speech_alone(string input) =>
        Assert.Equal(input, TranscriptCleaner.RemoveRepeats(input));

    [Fact]
    public void Empty_input_is_empty() => Assert.Equal("", TranscriptCleaner.RemoveRepeats("   "));
}

public class UploadNameTests
{
    [Theory]
    [InlineData("invoice.pdf.exe", "invoice.pdf.exe.blocked")]
    [InlineData("setup.MSI", "setup.MSI.blocked")]
    [InlineData("shortcut.lnk", "shortcut.lnk.blocked")]
    [InlineData("x.searchConnector-ms", "x.searchConnector-ms.blocked")]
    [InlineData("app.appref-ms", "app.appref-ms.blocked")]
    [InlineData("site.website", "site.website.blocked")]
    [InlineData("addin.XLL", "addin.XLL.blocked")]
    [InlineData("x.wsc", "x.wsc.blocked")]
    [InlineData("dark.theme", "dark.theme.blocked")]
    [InlineData("pack.themepack", "pack.themepack.blocked")]
    [InlineData("photo.jpg", "photo.jpg")]
    [InlineData("notes.pdf", "notes.pdf")]
    [InlineData("README", "README")]
    public void Executables_are_neutralized(string input, string expected) =>
        Assert.Equal(expected, Shammy.Core.PhoneSend.SafeFileName.NeutralizeExecutable(input));
}

public class AudioTrimTests
{
    const int Rate = 16000;

    static float[] Build(double silenceBefore, double speech, double silenceAfter, float noise = 0.003f)
    {
        var rng = new Random(1);
        int a = (int)(silenceBefore * Rate), b = (int)(speech * Rate), c = (int)(silenceAfter * Rate);
        var s = new float[a + b + c];
        for (int i = 0; i < s.Length; i++) s[i] = (float)((rng.NextDouble() * 2 - 1) * noise);
        for (int i = a; i < a + b; i++) s[i] += (float)(0.3 * Math.Sin(2 * Math.PI * 220 * i / Rate));
        return s;
    }

    [Fact]
    public void Cuts_long_silence_but_keeps_padding_around_speech()
    {
        var trimmed = AudioTrim.TrimSilence(Build(1.0, 1.0, 3.0), Rate);
        double seconds = (double)trimmed.Length / Rate;
        Assert.InRange(seconds, 1.4, 1.7); // 1 s speech + 0.2 s before + 0.35 s after (frame rounding)
    }

    [Fact]
    public void Silence_only_is_returned_unchanged()
    {
        var s = Build(2.0, 0, 0);
        Assert.Same(s, AudioTrim.TrimSilence(s, Rate));
    }

    [Fact]
    public void Tiny_clips_are_returned_unchanged()
    {
        var s = new float[100];
        Assert.Same(s, AudioTrim.TrimSilence(s, Rate));
    }
}
