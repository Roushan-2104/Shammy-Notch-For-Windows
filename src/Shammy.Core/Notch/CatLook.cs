using System.Globalization;

namespace Shammy.Core.Notch;

/// <summary>What the pet is: a cat (ears, slit pupils, whiskers and a nose around the eyes) or the plain eyes.</summary>
public enum PetKind { Cat, Eyes }

/// <summary>
/// How the cat holds itself in one mood. <see cref="EarTilt"/> folds both ears outward in degrees,
/// <see cref="EarHeight"/> stretches them (1 is at rest), <see cref="EarDrop"/> lowers them in pixels,
/// <see cref="EarTurn"/> swivels the right ear alone, <see cref="WhiskerLift"/> raises (negative) or droops
/// (positive) the whiskers in degrees and <see cref="Pupil"/> is half the pupil's width (0 for none).
/// </summary>
public readonly record struct CatPose(double EarTilt, double EarHeight, double EarDrop, double EarTurn, double WhiskerLift, double Pupil);

/// <summary>
/// The cat around the pet's eyes: the eyes themselves are unchanged, and everything here is sized against
/// them (an eye is 11 wide at the design's size). Pure numbers, so the drawing code only places shapes.
/// </summary>
public static class CatLooks
{
    /// <summary>Half the width of the pupil: a daylight slit, an alert pupil, and a wide one for a surprise.</summary>
    public const double Slit = 1.25, Alert = 1.7, Wide = 2.5;

    /// <summary>A pupil this wide or wider is drawn round rather than pointed.</summary>
    public const double RoundFrom = 2;

    public static CatPose Pose(PetMood mood) => mood switch
    {
        PetMood.Notify => new(0, 1.18, 0, 0, -7, Wide),
        PetMood.Listening => new(0, 1, 1.5, 14, 0, Alert),
        PetMood.Thinking => new(0, 1, 1, 10, 0, Slit),
        PetMood.Music or PetMood.Happy or PetMood.TimerDone => new(6, 1, 0, 0, -10, 0),
        PetMood.Tired => new(9, 0.93, 1.4, 0, 8, 1.3),
        PetMood.Sleepy => new(24, 0.83, 3.4, 0, 9, 0),
        _ => new(0, 1, 0, 0, 0, Slit),
    };

    /// <summary>
    /// The pupil that fits an eye: half its width (never wider than the eye allows) and half its height.
    /// A very low eye (a lid) has no pupil.
    /// </summary>
    public static (double HalfWidth, double HalfHeight) Pupil(double eyeWidth, double eyeHeight, double open)
    {
        double halfHeight = eyeHeight / 2 - (eyeHeight >= 11 ? 2.6 : 1.6);
        double halfWidth = Math.Min(open, eyeWidth / 2 - 2.4);
        return halfHeight < 1.5 || halfWidth <= 0 ? (0, 0) : (Math.Round(halfWidth, 2), Math.Round(halfHeight, 2));
    }

    /// <summary>
    /// The pupil's outline around (0,0): a pointed slit (two curves meeting at the top and bottom) while it is
    /// narrow, an ellipse once it is wide.
    /// </summary>
    public static string PupilPath(double halfWidth, double halfHeight)
    {
        static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        if (halfWidth >= RoundFrom)
            return $"M0,{N(-halfHeight)} A{N(halfWidth)},{N(halfHeight)} 0 1 1 0,{N(halfHeight)} A{N(halfWidth)},{N(halfHeight)} 0 1 1 0,{N(-halfHeight)} Z";
        // A quadratic curve reaches half its control point's offset, so the control sits at twice the half width.
        return $"M0,{N(-halfHeight)} Q{N(halfWidth * 2)},0 0,{N(halfHeight)} Q{N(-halfWidth * 2)},0 0,{N(-halfHeight)} Z";
    }

    /// <summary>How far the pupils slide (in pixels) for an eye turn of <paramref name="look"/> pixels.</summary>
    public static double PupilShift(double look) => Math.Clamp(look * 0.6, -1.7, 1.7);

    /// <summary>The cat fidgets every 4 to 8 seconds while it rests.</summary>
    public static TimeSpan NextFidget(Random random) => TimeSpan.FromSeconds(4 + random.NextDouble() * 4);

    /// <summary>Moods in which a resting cat twitches an ear or its whiskers now and then.</summary>
    public static bool Fidgets(PetMood mood) => mood is PetMood.Idle or PetMood.MeetingSoon or PetMood.Listening or PetMood.Thinking;
}
