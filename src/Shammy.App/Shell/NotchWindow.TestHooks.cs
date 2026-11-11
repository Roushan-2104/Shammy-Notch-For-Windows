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
    // ---- v2 test hooks (pipe commands) -----------------------------------------------------------

    internal string DescribeIdle()
    {
        var pet = _restView.Pet;
        var mood = _restView.Mood;
        return $"mode={_mode} peeking={_peeking} pet={(_restView.PetEnabled ? "on" : "off")} mood={mood.Mood}{(mood.Forced is null ? "" : "(forced)")} " +
               $"eyes={pet.Mood} sleepyHours={EyeLogic.IsSleepy(DateTime.Now)} following={pet.IsFollowing} " +
               $"look={pet.FollowOffset:0.0} glance='{_restView.CurrentText}' items=[{_glances.Describe()}] " +
               $"intent={_intent.Stage} speed={_intent.Speed:0.00} purrs={pet.Purrs}";
    }

    internal string DescribePeek() => $"mode={_mode} {_peekView.Describe()}";

    internal void BounceEyes() => _restView.Pet.Bounce();

    /// <summary>Shammy the pet on the resting notch on or off ("pet on/off"); saved in the settings.</summary>
    internal void SetPetEnabled(bool on)
    {
        if (_services.Settings.PetEnabled != on)
        {
            _services.Settings.PetEnabled = on;
            _services.Settings.Save();
        }
        _restView.SetPetEnabled(on);
        RefreshPet();
    }

    /// <summary>Force the pet's mood for testing ("auto" follows the real inputs again).</summary>
    internal string SetPetMood(string name)
    {
        var controller = _restView.Mood;
        string key = name.Trim().Replace("-", "").ToLowerInvariant();
        key = key switch { "timer" => "timerdone", "meeting" => "meetingsoon", "listen" => "listening", "think" => "thinking", "sleep" => "sleepy", _ => key };
        if (key == "auto") controller.Forced = null;
        else if (Enum.TryParse<PetMood>(key, ignoreCase: true, out var mood) && Enum.IsDefined(mood)) controller.Forced = mood;
        else return "error: usage pet-mood idle|music|notify|timer|listen|think|meeting|tired|sleepy|happy|auto";
        controller.Update();
        return DescribeIdle();
    }

    void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason is not (Microsoft.Win32.SessionSwitchReason.SessionUnlock or Microsoft.Win32.SessionSwitchReason.ConsoleConnect)) return;
        // System events arrive on their own thread.
        Dispatcher.BeginInvoke(() => GreetBack());
    }

    /// <summary>You are back at the PC: the pet is pleased to see you and gives a hop. Not with Calm motion.</summary>
    internal string GreetBack()
    {
        bool greeted = MotionPrefs.Lively && PetMoments.Greets(DateTime.UtcNow, _greetedAt);
        if (greeted)
        {
            _greetedAt = DateTime.UtcNow;
            _restView.Mood.Petted();
            _restView.Pet.Bounce();
        }
        return $"greeted={greeted} " + DescribeIdle();
    }
}
