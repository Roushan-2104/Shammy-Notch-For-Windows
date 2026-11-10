namespace Shammy.Core.Notch;

/// <summary>What the pet wears while a live activity owns the compact notch (the Activities board).</summary>
/// <summary><see cref="Custom"/> is for modules: the activity picks its own face, circle and whether it stays.</summary>
public enum PetCostume { Call, Music, Timer, Recording, Download, Charging, Earbuds, Notify, Update, Custom }

/// <summary>The face (and animation) the pet makes in its costume.</summary>
public enum PetFace
{
    /// <summary>Headset on; the mouth follows the microphone level, a smile while others talk.</summary>
    Call,
    /// <summary>Headset on, lips zipped.</summary>
    CallMuted,
    /// <summary>Happy ^ ^ eyes swaying, notes floating off.</summary>
    MusicVibe,
    /// <summary>Waiting, peeking at the play button.</summary>
    MusicPaused,
    /// <summary>Eyes circling a tiny ticking clock.</summary>
    TimerWatch,
    /// <summary>Jumping for joy with confetti.</summary>
    TimerDone,
    /// <summary>Ninja mask and shifty eyes: someone may be watching the screen.</summary>
    Ninja,
    /// <summary>Holding a tiny progress bar.</summary>
    DownloadHold,
    /// <summary>Noms the finished file, chews and smiles.</summary>
    DownloadNom,
    /// <summary>Sips a lightning bolt, then sighs happily.</summary>
    ChargeSip,
    /// <summary>Headphones drop on; nods along.</summary>
    Earbuds,
    /// <summary>Wide eyes and a tiny "o".</summary>
    Notify,
    /// <summary>No eyes: the song's cover as a spinning record.</summary>
    Disc,
    /// <summary>The record, stopped and dimmed.</summary>
    DiscPaused,
    /// <summary>Narrowed eyes scanning side to side: something is being worked on.</summary>
    Focused,
    /// <summary>Happy ^ ^ eyes and a smile.</summary>
    Happy,
    /// <summary>Eyes closed, breathing slowly.</summary>
    Resting,
    /// <summary>Eyes and a mouth that moves by itself: the pet is saying something.</summary>
    Talking,
}

/// <summary>The one direct action the action circle holds.</summary>
/// <summary><see cref="Dismiss"/> is a check (done); <see cref="Skip"/> is a cross (not now).</summary>
public enum CircleAction { Mute, Unmute, Pause, Play, Resume, Dismiss, Stop, Open, OpenWhenDone, Restart, Skip }

/// <summary>
/// How the circle is coloured. Accent only when its action is the primary "Open/Dismiss" (the one thing to do);
/// toggles like mute and pause stay neutral; a muted microphone is red so it reads at a glance.
/// </summary>
public enum CircleTone { Neutral, Accent, Red }

/// <param name="Enabled">False while it only shows progress (a download's Open before it finishes).</param>
/// <param name="Description">What clicking it does, for the tooltip and the screen reader.</param>
public sealed record CircleSpec(CircleAction Action, CircleTone Tone, bool Enabled, string Description);

/// <summary>A live activity as the pet sees it: its costume, the short status beside it and its state.</summary>
public sealed record PetActivityState
{
    /// <summary>The activity's id (filled in by whoever maps the activity).</summary>
    public string Id { get; init; } = "";

    public PetCostume Costume { get; init; }

    /// <summary>The short label on the left ("STANDUP", "TEA", "Airdopes 311 Pro").</summary>
    public string Label { get; init; } = "";

    /// <summary>The value on the right ("12:41", "64%").</summary>
    public string Value { get; init; } = "";

    public bool Muted { get; init; }

    /// <summary>Music playing (vs paused).</summary>
    public bool Playing { get; init; }

    /// <summary>A timer paused.</summary>
    public bool Paused { get; init; }

    /// <summary>A timer at zero, a download finished.</summary>
    public bool Done { get; init; }

    /// <summary>0..1 when known (a download's percent, a battery level).</summary>
    public double? Progress { get; init; }

    /// <summary>Something can stop it from here (a recording Shammy controls).</summary>
    public bool CanStop { get; init; }

    /// <summary>Clicking opens something (the message's app, the finished file, the recording app).</summary>
    public bool CanOpen { get; init; }

    /// <summary>The label is a name (an earbuds model), shown as typed rather than tracked uppercase.</summary>
    public bool PlainLabel { get; init; }

    /// <summary>A face other than the costume's own (required for <see cref="PetCostume.Custom"/>, which has none).</summary>
    public PetFace? Face { get; init; }

    /// <summary><see cref="PetCostume.Custom"/> only: the action circle, or null for none.</summary>
    public CircleSpec? Circle { get; init; }

    /// <summary><see cref="PetCostume.Custom"/> only: it keeps going (so it can wait as the dot) rather than being a moment.</summary>
    public bool Ongoing { get; init; }

    /// <summary>One line under the row (the lyric being sung); the notch grows to hold it. Empty for none.</summary>
    public string Line { get; init; } = "";
}

/// <summary>Who owns the pet (and the circle), and which second live activity waits as the small dot.</summary>
public sealed record PetStage(string? Owner, string? Dot);

/// <summary>
/// Pure rules for the pet-centred compact notch: the face for a costume, the action circle's one action, and
/// which of several live activities gets the pet when more than one is going on.
/// </summary>
public static class PetActivities
{
    public static PetFace Face(PetActivityState s) => s.Face ?? s.Costume switch
    {
        PetCostume.Call => s.Muted ? PetFace.CallMuted : PetFace.Call,
        PetCostume.Music => s.Playing ? PetFace.Disc : PetFace.DiscPaused,
        PetCostume.Timer => s.Done ? PetFace.TimerDone : PetFace.TimerWatch,
        PetCostume.Recording => PetFace.Ninja,
        PetCostume.Download => s.Done ? PetFace.DownloadNom : PetFace.DownloadHold,
        PetCostume.Charging => PetFace.ChargeSip,
        PetCostume.Earbuds => PetFace.Earbuds,
        _ => PetFace.Notify,
    };

    /// <summary>The circle for an activity; null when there's nothing to do (charging, earbuds).</summary>
    public static CircleSpec? Circle(PetActivityState s) => s.Costume switch
    {
        PetCostume.Call => s.Muted
            ? new CircleSpec(CircleAction.Unmute, CircleTone.Red, true, "Unmute microphone")
            : new CircleSpec(CircleAction.Mute, CircleTone.Neutral, true, "Mute microphone"),
        PetCostume.Music => s.Playing
            ? new CircleSpec(CircleAction.Pause, CircleTone.Neutral, true, "Pause")
            : new CircleSpec(CircleAction.Play, CircleTone.Neutral, true, "Play"),
        PetCostume.Timer => s.Done
            ? new CircleSpec(CircleAction.Dismiss, CircleTone.Accent, true, "Dismiss")
            : s.Paused
                ? new CircleSpec(CircleAction.Resume, CircleTone.Neutral, true, "Resume timer")
                : new CircleSpec(CircleAction.Pause, CircleTone.Neutral, true, "Pause timer"),
        PetCostume.Recording => s.CanStop
            ? new CircleSpec(CircleAction.Stop, CircleTone.Neutral, true, "Stop")
            : s.CanOpen ? new CircleSpec(CircleAction.Open, CircleTone.Accent, true, "Open the app") : null,
        PetCostume.Download => s.Done
            ? s.CanOpen ? new CircleSpec(CircleAction.Open, CircleTone.Accent, true, "Open file") : null
            : new CircleSpec(CircleAction.OpenWhenDone, CircleTone.Neutral, false, "Open file, when the download finishes"),
        PetCostume.Notify => s.CanOpen ? new CircleSpec(CircleAction.Open, CircleTone.Accent, true, "Open") : null,
        PetCostume.Update => new CircleSpec(CircleAction.Restart, CircleTone.Accent, true, "Restart to update"),
        PetCostume.Custom => s.Circle,
        _ => null,
    };

    /// <summary>
    /// Ongoing activities that can wait as the dot and swap back: a call, music, a running timer, a recording, a
    /// download in progress, an update waiting for its restart. One-off moments (a notification, plugging in, earbuds, a timer at zero, a finished
    /// download) take the pet while they last and never wait in the dot.
    /// </summary>
    public static bool IsOngoing(PetActivityState s) => s.Costume switch
    {
        PetCostume.Call or PetCostume.Music or PetCostume.Recording or PetCostume.Update => true,
        PetCostume.Timer or PetCostume.Download => !s.Done,
        PetCostume.Custom => s.Ongoing,
        _ => false,
    };

    /// <summary>The record is the music display, not the pet: it shows even with the pet turned off.</summary>
    public static bool ShowsWithoutPet(PetFace face) => face is PetFace.Disc or PetFace.DiscPaused;

    /// <summary>
    /// <paramref name="ranked"/> is the pet-able live activities, most important first. A one-off moment on top
    /// always takes the pet; otherwise the one the user swapped to (<paramref name="preferred"/>, by clicking the
    /// dot) wins while it's still live. The dot is the next ongoing activity, so at most one circle and one dot.
    /// </summary>
    public static PetStage Stage(IReadOnlyList<PetActivityState> ranked, string? preferred)
    {
        if (ranked.Count == 0) return new PetStage(null, null);
        var top = ranked[0];
        var owner = top;
        if (IsOngoing(top) && preferred is not null && ranked.FirstOrDefault(r => r.Id == preferred) is { } pick && IsOngoing(pick))
            owner = pick;
        var dot = ranked.FirstOrDefault(r => r.Id != owner.Id && IsOngoing(r));
        return new PetStage(owner.Id, dot?.Id);
    }

    /// <summary>A click on the pet itself (not the circle) runs the activity's own click only for one-off
    /// moments you'd act on (open the message, the file, dismiss the timer); otherwise it opens the notch.</summary>
    public static bool BodyClickRunsActivity(PetActivityState s) =>
        s.Costume == PetCostume.Notify || (s.Costume is PetCostume.Timer or PetCostume.Download && s.Done)
        || (s.Costume == PetCostume.Custom && !s.Ongoing && s.CanOpen);

    /// <summary>
    /// A notification's value is who it's from; nothing while the screen may be seen (the host already swapped the
    /// title for the app name then) or when the title just repeats the app.
    /// </summary>
    public static string NotifyValue(string title, string appName, bool presenting) =>
        presenting || title.Trim().Length == 0 || string.Equals(title.Trim(), appName.Trim(), StringComparison.OrdinalIgnoreCase)
            ? ""
            : title.Trim();

    /// <summary>"12:41" or "1:02:03": a call or recording's running time.</summary>
    public static string Clock(TimeSpan elapsed) => Productivity.TimeFormat.Elapsed(elapsed);
}

/// <summary>
/// Turns the microphone's peak level into how open the pet's mouth is during a call. A noise gate keeps it shut
/// on room hum; it opens fast and closes a little slower so speech reads as talking rather than flicker, and
/// it counts as speaking for a short hold after the last loud sample.
/// </summary>
public sealed class TalkMeter
{
    /// <summary>Peaks below this (room noise, a fan) don't move the mouth.</summary>
    public const double Gate = 0.04;

    /// <summary>Peak at which the mouth is fully open.</summary>
    public const double Full = 0.35;

    public static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(300);

    double _open;
    TimeSpan? _lastLoud;

    public double Open => _open;

    public bool Speaking { get; private set; }

    public void Reset()
    {
        _open = 0;
        _lastLoud = null;
        Speaking = false;
    }

    /// <returns>Mouth openness 0..1.</returns>
    public double Sample(double peak, TimeSpan now)
    {
        double target = Target(peak);
        if (target > 0) _lastLoud = now;
        // Fast attack, slower release.
        _open += (target - _open) * (target > _open ? 0.6 : 0.3);
        if (_open < 0.02) _open = 0;
        Speaking = _lastLoud is { } loud && now - loud <= Hold;
        return _open;
    }

    public static double Target(double peak)
    {
        if (double.IsNaN(peak) || peak <= Gate) return 0;
        return Math.Clamp(Math.Sqrt((peak - Gate) / (Full - Gate)), 0, 1);
    }
}
