using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Core.Activities;
using Shammy.Core.Music;
using Shammy.Core.Notch;
using Shammy.Infrastructure;
using Shammy.Interop;
using Shammy.Modules;
using Shammy.Motion;
using Shammy.Notifications;
using Shammy.Views;

namespace Shammy.Shell;

public partial class NotchWindow
{
    // ---- The pet wears the activity ---------------------------------------------------------------

    PetLive? PetFor(Activity? a, DateTime? now = null) => a is null ? null : PetActivitySource.From(a, _services, now ?? DateTime.UtcNow);

    /// <summary>
    /// The glow pooled under the pill while music played is gone: the island's rim light shows the album's colour
    /// instead (quieter, and it costs nothing extra). Kept as a switch so the chrome's own glow can come back.
    /// </summary>
    static bool PetGlow => false;

    /// <summary>
    /// Puts the pet in <paramref name="current"/>'s costume (or the one the user swapped to) and picks the dot; false
    /// when it isn't something the pet wears (a volume HUD, a keyboard pill, a message), which keeps its own pill.
    /// Over a fullscreen app only what broke through shows, with no dot.
    /// </summary>
    bool PreparePet(Activity current)
    {
        var now = DateTime.UtcNow;
        if (PetFor(current, now) is not { } first) return false;
        var lives = new List<PetLive> { first };
        if (!_fullscreen)
            foreach (var a in _ranked)
                if (a.Id != current.Id && PetFor(a, now) is { } live) lives.Add(live);
        var stage = PetActivities.Stage(lives.Select(l => l.State).ToList(), _fullscreen ? null : _petPreferred);
        _petOwner = lives.FirstOrDefault(l => l.State.Id == stage.Owner) ?? first;
        _petDot = stage.Dot is null ? null : lives.FirstOrDefault(l => l.State.Id == stage.Dot);
        bool changed = _activityView.Show(_petOwner, _services.Settings.PetEnabled);
        // A different activity took the pill while it was up: its content blurs through the change.
        if (changed && _petShowing && _mode == NotchMode.Compact && _petCompact.Opacity.Value > 0.9 && MotionPrefs.Lively)
        {
            _petCompact.Swap.Snap(0);
            _petCompact.Swap.AnimateTo(1, 0.34, 1);
        }
        _petShowing = true;
        return true;
    }

    /// <summary>Once a second while the pet wears something, and right after its circle is used: live values.</summary>
    void RefreshPet()
    {
        if (_mode != NotchMode.Compact || !_petShowing || _activity is null)
        {
            _petTick.IsEnabled = false;
            return;
        }
        if (!PreparePet(_activity))
        {
            Recompute();
            return;
        }
        _chrome.Set(_petOwner, _fullscreen ? null : _petDot, PetGlow);
        if (_petOwner is not null) _chrome.Refresh(_petOwner);
        // A line under the row came or went (a lyric started): the notch grows or shrinks around it.
        if (SpecFor(_mode) != _spec) Transition(_mode);
    }

    void RunCircle(CircleAction action)
    {
        if (_petOwner is not { } owner) return;
        try
        {
            owner.Run?.Invoke(action);
        }
        catch (Exception ex)
        {
            Log.Warn($"Action circle {action} on {owner.Activity.Id} failed", ex);
        }
        // Opening or dismissing a one-off moment is the end of it.
        if (action is CircleAction.Open or CircleAction.Dismiss or CircleAction.Skip && !PetActivities.IsOngoing(owner.State)) ActionClicked?.Invoke(owner.Activity.Id);
        RefreshPet();
    }

    /// <summary>
    /// The pet itself was clicked: a finished timer or download runs its own click
    /// (dismiss, the download card), and anything ongoing opens the notch (its one action is on the circle).
    /// </summary>
    void PetBodyClicked()
    {
        var owner = _petOwner;
        if (owner is null || !PetActivities.BodyClickRunsActivity(owner.State) || owner.Activity.Click is not { } click)
        {
            OpenFromHover();
            return;
        }
        click();
        if (IsAction(owner.Activity) || !PetActivities.IsOngoing(owner.State)) ActionClicked?.Invoke(owner.Activity.Id);
    }

    internal string DescribeCircle()
    {
        var o = _petOwner;
        var pet = _activityView.Pet;
        return $"mode={_mode} petCompact={(_mode == NotchMode.Compact && _petShowing ? "yes" : "no")} petOn={_services.Settings.PetEnabled} " +
               $"owner={(o is null ? "-" : $"{o.Activity.Id}:{o.State.Costume}")} face={pet.Face?.ToString() ?? "-"} " +
               $"label='{_activityView.LabelText}' value='{_activityView.ValueText}' line='{_activityView.LineText}' {_chrome.Describe()} " +
               $"preferred={_petPreferred ?? "-"} mouth={pet.MouthOpen:0.00} fullscreen={_fullscreen}";
    }

    /// <summary>Test hook: click the action circle, or with "dot" the dot (as a user would).</summary>
    internal string ClickCircle(string what)
    {
        if (_mode != NotchMode.Compact || !_petShowing) return "error: no pet activity on the notch";
        if (what.Trim() == "dot")
        {
            if (_chrome.DotId is not { } id) return "error: no dot";
            _petPreferred = id;
            Recompute();
            return DescribeCircle();
        }
        if (_chrome.Circle is not { } spec) return "error: no circle";
        if (!spec.Enabled) return $"disabled: {spec.Description}";
        RunCircle(spec.Action);
        return $"ran {spec.Action}; {DescribeCircle()}";
    }
}
