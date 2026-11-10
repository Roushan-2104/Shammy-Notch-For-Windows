namespace Shammy.Core.OnlineVoice;

/// <summary>
/// Stops asking after repeated failures: three in a row (a dead key, no credit, no internet) and speaking pauses
/// for ten minutes, so a broken setup costs three short waits and then nothing. One success clears the count.
/// </summary>
public sealed class SpeakGate
{
    public const int MaxFailures = 3;
    public static readonly TimeSpan Pause = TimeSpan.FromMinutes(10);

    /// <summary>Failures in a row since the last success (or since the last pause ended).</summary>
    public int Failures { get; private set; }

    public DateTime? PausedUntilUtc { get; private set; }

    /// <summary>Whether a request may be made now. A pause that has run out is over, with a clean count.</summary>
    public bool Allows(DateTime utcNow)
    {
        if (PausedUntilUtc is not { } until) return true;
        if (utcNow < until) return false;
        PausedUntilUtc = null;
        Failures = 0;
        return true;
    }

    public void Failed(DateTime utcNow)
    {
        if (!Allows(utcNow)) return;
        Failures++;
        if (Failures >= MaxFailures) PausedUntilUtc = utcNow + Pause;
    }

    public void Succeeded()
    {
        Failures = 0;
        PausedUntilUtc = null;
    }

    /// <summary>A new key or a switch flipped back on deserves a fresh try.</summary>
    public void Reset() => Succeeded();
}

/// <summary>Why Shammy stays silent, most basic reason first.</summary>
public enum SpeakBlock { None, Empty, NoKey, SwitchOff, SoundsOff, Quiet, Paused }

/// <summary>When Shammy may speak and when listening goes online: every condition in one place.</summary>
public static class VoiceRules
{
    /// <param name="switchOn">"Shammy speaks" (the Test button and a faked voice count as on).</param>
    /// <param name="soundsOn">The app's own sounds are on, at a volume above zero.</param>
    /// <param name="quiet">A call, a meeting or a focus session is on.</param>
    /// <param name="paused">Too many failures in a row (see <see cref="SpeakGate"/>).</param>
    public static SpeakBlock Speak(string text, bool hasKey, bool switchOn, bool soundsOn, bool quiet, bool paused)
    {
        if (string.IsNullOrWhiteSpace(text)) return SpeakBlock.Empty;
        if (!hasKey) return SpeakBlock.NoKey;
        if (!switchOn) return SpeakBlock.SwitchOff;
        if (!soundsOn) return SpeakBlock.SoundsOff;
        if (quiet) return SpeakBlock.Quiet;
        return paused ? SpeakBlock.Paused : SpeakBlock.None;
    }

    /// <summary>The reason as the pipe reports it.</summary>
    public static string Describe(SpeakBlock block) => block switch
    {
        SpeakBlock.Empty => "nothing to say",
        SpeakBlock.NoKey => "no key",
        SpeakBlock.SwitchOff => "off",
        SpeakBlock.SoundsOff => "sounds off",
        SpeakBlock.Quiet => "quiet",
        SpeakBlock.Paused => "paused",
        _ => "ok",
    };

    /// <summary>The same reason for a person (the Test button's status line).</summary>
    public static string Friendly(SpeakBlock block) => block switch
    {
        SpeakBlock.NoKey => "Paste your Gnani key first",
        SpeakBlock.SoundsOff => "Notch sounds are off",
        SpeakBlock.Quiet => "Not during a call or meeting",
        SpeakBlock.Paused => "Paused after a few failures, try again in a bit",
        SpeakBlock.SwitchOff => "Shammy speaks is off",
        _ => "",
    };

    /// <summary>Whether the recording goes to Gnani: the switch is on, a key is saved and the PC has a network.</summary>
    public static bool ListenOnline(bool hasKey, bool switchOn, bool networkUp) => hasKey && switchOn && networkUp;
}
