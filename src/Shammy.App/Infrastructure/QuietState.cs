using Shammy.Core.Connect;

namespace Shammy.Infrastructure;

/// <summary>
/// Quiet mode (see <see cref="QuietRules"/>): on while any reason holds — a call app has the mic, a calendar
/// meeting is on, a focus session runs. Other apps' notifications are then held without a ping or pop-up.
/// Set and read on the UI thread.
/// </summary>
internal static class QuietState
{
    static readonly HashSet<QuietReason> Reasons = new();

    public static bool IsQuiet => Reasons.Count > 0;

    public static string Describe() => QuietRules.Describe(Reasons);

    /// <summary>Whether this reason holds now (a call app has the mic: <see cref="QuietReason.Call"/>).</summary>
    public static bool Has(QuietReason reason) => Reasons.Contains(reason);

    /// <summary>Raised when <see cref="IsQuiet"/> flips (not when only the reasons change).</summary>
    public static event Action<bool>? Changed;

    public static void Set(QuietReason reason, bool on)
    {
        bool was = IsQuiet;
        if (on) Reasons.Add(reason);
        else Reasons.Remove(reason);
        if (was != IsQuiet)
        {
            Log.Info($"Quiet mode {(IsQuiet ? "on" : "off")} ({Describe()})");
            Changed?.Invoke(IsQuiet);
        }
    }
}
