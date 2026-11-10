using System.Globalization;

namespace Shammy.Core.Nudges;

/// <summary>
/// Glasses of water confirmed today. The day is the local calendar day, so the count starts again at local
/// midnight. <see cref="Day"/> and <see cref="Glasses"/> are what gets saved.
/// </summary>
public sealed class WaterTally
{
    /// <summary>The local day the count belongs to, as "yyyy-MM-dd".</summary>
    public string Day { get; set; } = "";

    public int Glasses { get; set; }

    /// <summary>Glasses confirmed on <paramref name="localNow"/>'s day; another day's count reads as none.</summary>
    public int Today(DateTime localNow) => Day == Key(localNow) ? Math.Max(0, Glasses) : 0;

    /// <summary>Counts one glass and returns today's new total.</summary>
    public int Add(DateTime localNow)
    {
        int count = Today(localNow) + 1;
        Day = Key(localNow);
        Glasses = count;
        return count;
    }

    static string Key(DateTime localNow) => localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
