namespace Shammy.Infrastructure;

/// <summary>
/// Whether someone else may be watching the screen: an app is capturing it (screen sharing, recording).
/// A plain call with the mic or camera doesn't count. Notifications (pop-ups and the Notifications tab)
/// show only the app name while this is on.
/// </summary>
internal static class PrivacyState
{
    static volatile bool _presenting, _recording;

    public static bool Presenting => _presenting || _recording;

    /// <summary>Raised on the setter's thread when <see cref="Presenting"/> flips, so open views can re-hide or reveal.</summary>
    public static event Action<bool>? Changed;

    public static void Set(bool presenting)
    {
        bool was = Presenting;
        _presenting = presenting;
        if (Presenting != was) Changed?.Invoke(Presenting);
    }

    /// <summary>
    /// Shammy's own screen recording is running. Kept apart from <see cref="Set"/> (which follows other apps'
    /// captures and leaves Shammy itself out), so neither switches the other off.
    /// </summary>
    public static void SetRecording(bool recording)
    {
        bool was = Presenting;
        _recording = recording;
        if (Presenting != was) Changed?.Invoke(Presenting);
    }
}
