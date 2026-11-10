using System.Windows;

namespace Shammy.Infrastructure;

/// <summary>
/// Whether decorative motion plays: the pet's ambient bursts, the spinning record, pops and fades. It stops when
/// Windows has animations turned off, and when the user picks Calm (see <see cref="Calm"/>). Views ask this
/// instead of reading the Windows setting themselves, so one switch quiets everything.
/// </summary>
internal static class MotionPrefs
{
    /// <summary>The user's Calm choice (Settings); Windows' own setting is respected either way.</summary>
    public static bool Calm { get; set; }

    public static bool Lively => !Calm && SystemParameters.ClientAreaAnimation;
}
