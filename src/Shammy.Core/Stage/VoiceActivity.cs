namespace Shammy.Core.Stage;

/// <summary>
/// Is someone speaking into the microphone? No speech recognition: just how loud the input is against a noise floor
/// that follows the room. The floor drops quickly to quiet moments and rises slowly (slower still while speaking),
/// so a fan or the hum of a laptop becomes the floor while a voice stands clear of it. Speaking starts once the
/// level has been <see cref="Margin"/> above the floor for <see cref="Attack"/>, and lasts <see cref="Hangover"/>
/// past the last loud moment, so the short gaps between words never stop it.
/// Feed it levels as RMS amplitude (0–1) with the time each one covers.
/// </summary>
public sealed class VoiceActivity
{
    /// <summary>How far above the floor counts as a voice (dB).</summary>
    public double Margin { get; init; } = 10;

    /// <summary>Quieter than this is never a voice, however quiet the room (dBFS).</summary>
    public double Quietest { get; init; } = -58;

    /// <summary>Loud for this long before it counts (seconds): a cough or a knock is shorter.</summary>
    public double Attack { get; init; } = 0.1;

    /// <summary>Still speaking for this long after the last loud moment (seconds).</summary>
    public double Hangover { get; init; } = 0.7;

    /// <summary>How fast the floor follows a quieter room (time constant, seconds).</summary>
    public double FallTime { get; init; } = 0.3;

    /// <summary>How fast the floor follows a louder room while nobody speaks (time constant, seconds).</summary>
    public double RiseTime { get; init; } = 4;

    /// <summary>How fast the floor may creep up while speaking (dB a second), so a steady new noise is learnt in time.</summary>
    public double SpeakingRise { get; init; } = 0.4;

    const double Lowest = -90;

    double _loudFor, _quietFor;
    bool _started;

    /// <summary>The room's noise floor (dBFS).</summary>
    public double Floor { get; private set; } = -70;

    /// <summary>The last level fed (dBFS).</summary>
    public double Level { get; private set; } = Lowest;

    public bool Speaking { get; private set; }

    public static double Decibels(double rms) => rms <= 0 ? Lowest : Math.Max(Lowest, 20 * Math.Log10(rms));

    /// <summary>Takes one block's level (RMS 0–1) covering <paramref name="seconds"/>; returns whether someone is speaking.</summary>
    public bool Feed(double rms, double seconds)
    {
        if (seconds <= 0) return Speaking;
        seconds = Math.Min(seconds, 0.5);
        double db = Decibels(rms);
        Level = db;
        if (!_started)
        {
            // The first block is the best guess at the room. If it was already a voice, the floor falls to the room
            // in the first pause (a fraction of a second), so at worst the first words are missed.
            _started = true;
            Floor = db;
        }

        bool loud = db >= Math.Max(Floor + Margin, Quietest);
        if (loud)
        {
            _loudFor += seconds;
            _quietFor = 0;
            if (_loudFor >= Attack) Speaking = true;
        }
        else
        {
            _loudFor = 0;
            _quietFor += seconds;
            if (_quietFor >= Hangover) Speaking = false;
        }

        if (db < Floor) Floor += (db - Floor) * (1 - Math.Exp(-seconds / FallTime));
        else if (!Speaking && !loud) Floor += (db - Floor) * (1 - Math.Exp(-seconds / RiseTime));
        else Floor = Math.Min(db, Floor + SpeakingRise * seconds);
        Floor = Math.Max(Floor, Lowest);
        return Speaking;
    }

    /// <summary>Starts over (a new microphone, or Stage opened again).</summary>
    public void Reset()
    {
        _loudFor = _quietFor = 0;
        _started = false;
        Speaking = false;
        Floor = -70;
        Level = Lowest;
    }
}
