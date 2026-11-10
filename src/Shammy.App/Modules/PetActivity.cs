using System.Windows.Media;
using Shammy.Core.Activities;
using Shammy.Core.Notch;
using Shammy.Infrastructure;
using Shammy.Notifications;

namespace Shammy.Modules;

/// <summary>
/// A module's compact activity that the pet can wear (a call, a running timer, a screen recording). Put it on the
/// same payload as <see cref="ICompactContent"/>: the notch then shows the pet in costume with this status instead
/// of the module's own ears. Asked again every second while it's on screen, so live values need no extra events.
/// </summary>
internal interface IPetActivity
{
    PetActivityState Describe(DateTime utcNow);

    /// <summary>Run the action circle's action (mute, pause, dismiss...).</summary>
    void RunCircle(CircleAction action);

    /// <summary>The microphone's peak level 0..1 for a call (the pet's mouth follows it); 0 otherwise.</summary>
    double Level() => 0;
}

/// <summary>An activity the pet is wearing right now, with whatever it needs to draw and act on it.</summary>
internal sealed record PetLive(Activity Activity, PetActivityState State)
{
    public Action<CircleAction>? Run { get; init; }
    public Func<double>? Level { get; init; }

    /// <summary>The app's icon beside a notification's label.</summary>
    public ImageSource? Badge { get; init; }

    /// <summary>Album colour: the notch glows with it and the eq bars use it.</summary>
    public Color? Art { get; init; }

    /// <summary>The song's cover, drawn as the record.</summary>
    public ImageSource? Cover { get; init; }

    public PetFace Face => PetActivities.Face(State);
    public CircleSpec? Circle => PetActivities.Circle(State);
}

/// <summary>Maps the notch's activities to pet costumes. Anything it returns null for keeps its own pill.</summary>
internal static class PetActivitySource
{
    public static PetLive? From(Activity a, ShammyServices s, DateTime utcNow)
    {
        try
        {
            return Map(a, s, utcNow);
        }
        catch (Exception ex)
        {
            Log.Warn($"Pet: describing {a.Id} failed", ex);
            return null;
        }
    }

    static PetLive? Map(Activity a, ShammyServices s, DateTime utcNow)
    {
        switch (a.Kind)
        {
            case ActivityKind.NowPlaying:
            {
                var m = s.Media;
                if (!m.HasSession) return null;
                bool playing = m.IsPlaying;
                return new PetLive(a, new PetActivityState
                {
                    Id = a.Id,
                    Costume = PetCostume.Music,
                    Playing = playing,
                    Label = playing ? m.Title : "Paused",
                    Value = playing || m.Duration <= TimeSpan.Zero ? "" : PetActivities.Clock(m.Position),
                    Line = playing && s.Settings.LyricLineEnabled ? HostLinks.CurrentLyric?.Invoke() ?? "" : "",
                })
                {
                    Run = _ => m.TogglePlayPause(),
                    Cover = m.Art,
                    // White is the player's "no artwork" colour: no glow then, plain bars.
                    Art = m.Art is not null && m.ArtColor != Colors.White ? m.ArtColor : null,
                };
            }
            case ActivityKind.Charging when a.Payload is BatteryPayload b:
                return new PetLive(a, new PetActivityState
                {
                    Id = a.Id, Costume = PetCostume.Charging, Label = "Charging", Value = $"{b.Percent}%", Progress = b.Percent / 100.0,
                });
            case ActivityKind.Bluetooth when a.Payload is BluetoothPayload { Connected: true, IsAudio: true } bt:
                return new PetLive(a, new PetActivityState
                {
                    Id = a.Id, Costume = PetCostume.Earbuds, Label = bt.Name, PlainLabel = true,
                    Value = bt.Battery is { } level ? $"{level}%" : "Connected",
                });
            case ActivityKind.Downloading when a.Payload is DownloadPayload d:
                return new PetLive(a, new PetActivityState
                {
                    Id = a.Id, Costume = PetCostume.Download, Label = "Download",
                    Value = Core.Connect.ConnectText.DownloadProgress(d.Bytes, d.Total),
                    Progress = d.Total is > 0 ? Math.Clamp(d.Bytes / (double)d.Total.Value, 0, 1) : null,
                });
            case ActivityKind.DownloadComplete when a.Payload is DownloadPayload d:
                return new PetLive(a, new PetActivityState
                {
                    Id = a.Id, Costume = PetCostume.Download, Label = "Saved", Value = "Done", Done = true, Progress = 1,
                    CanOpen = d.Path is not null || a.Click is not null,
                })
                {
                    // The circle opens the file itself; the pill's own click still shows the download card.
                    Run = _ => OpenFile(d.Path, a.Click),
                };
            case ActivityKind.Notification when a.Payload is NotificationPayload np:
            {
                var n = np.Notification;
                return new PetLive(a, new PetActivityState
                {
                    Id = a.Id, Costume = PetCostume.Notify, Label = n.AppName,
                    Value = PetActivities.NotifyValue(n.Title, n.AppName, PrivacyState.Presenting),
                    CanOpen = a.Click is not null,
                })
                {
                    Run = _ => a.Click?.Invoke(),
                    Badge = n.Icon,
                };
            }
            case ActivityKind.Custom when a.Payload is IPetActivity p:
                return new PetLive(a, p.Describe(utcNow) with { Id = a.Id })
                {
                    Run = p.RunCircle,
                    Level = p.Level,
                };
            default:
                return null;
        }
    }

    static void OpenFile(string? path, Action? fallback)
    {
        if (path is null || !File.Exists(path))
        {
            fallback?.Invoke();
            return;
        }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Warn("Opening the download failed", ex); }
    }
}
