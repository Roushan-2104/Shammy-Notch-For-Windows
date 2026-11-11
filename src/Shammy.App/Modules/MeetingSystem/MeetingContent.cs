using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Core.Notch;

namespace Shammy.Modules.MeetingSystem;

/// <summary>
/// Compact ears while an app has the microphone: mic (green live / red slashed muted) and a camera
/// when one is on, with the app's name on the right. Mute and camera update in place. The pet wears it as
/// the call costume (headset, mouth following the mic, zipped lips when muted) with the call's running time,
/// and the action circle mutes and unmutes.
/// </summary>
internal sealed class MeetingContent : ICompactContent, IPetActivity
{
    const double MaxNameWidth = 120;

    bool _muted, _camera;
    TextBlock? _mic, _cam, _bubbleMic;
    FrameworkElement? _gap;

    readonly DateTime _since;
    readonly Action? _toggleMute;
    readonly Func<double>? _level;

    /// <param name="since">When the call started (the app first took the mic), for the pet's call timer.</param>
    public MeetingContent(string appName, bool muted, bool camera, DateTime? since = null, Action? toggleMute = null, Func<double>? level = null)
    {
        AppName = appName;
        _muted = muted;
        _camera = camera;
        _since = since ?? DateTime.UtcNow;
        _toggleMute = toggleMute;
        _level = level;
    }

    public string AppName { get; }

    public PetActivityState Describe(DateTime utcNow) => new()
    {
        Costume = PetCostume.Call,
        Label = AppName,
        Value = PetActivities.Clock(utcNow - _since),
        Muted = _muted,
    };

    public void RunCircle(CircleAction action)
    {
        if (action is CircleAction.Mute or CircleAction.Unmute) _toggleMute?.Invoke();
    }

    /// <summary>No mouth movement while muted: nobody hears it anyway.</summary>
    public double Level() => _muted ? 0 : _level?.Invoke() ?? 0;

    public double Build(ContentControl left, ContentControl right)
    {
        _mic = Glyph(15);
        _gap = new FrameworkElement { Width = 9 };
        _cam = Glyph(15);
        _cam.Text = "";
        _cam.Foreground = Ui.Tokens.Res("Fg");
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_mic);
        row.Children.Add(_gap);
        row.Children.Add(_cam);
        left.Content = row;

        var name = new TextBlock
        {
            Text = AppName,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Tokens.Res("Fg"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = MaxNameWidth,
        };
        right.Content = name;
        Apply();

        name.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // Wide enough for mic + camera on the left even when the name is short.
        return Math.Max(44, Math.Min(MaxNameWidth, Math.Ceiling(name.DesiredSize.Width) + 2));
    }

    public UIElement? BuildBubble()
    {
        _bubbleMic = Glyph(13);
        Apply();
        return _bubbleMic;
    }

    public void Set(bool muted, bool camera)
    {
        if (muted == _muted && camera == _camera) return;
        _muted = muted;
        _camera = camera;
        Apply();
    }

    void Apply()
    {
        foreach (var mic in new[] { _mic, _bubbleMic })
        {
            if (mic is null) continue;
            mic.Text = _muted ? "" : "";
            mic.Foreground = Ui.Tokens.Res(_muted ? "Red" : "Green");
        }
        var camVisibility = _camera ? Visibility.Visible : Visibility.Collapsed;
        if (_cam is not null) _cam.Visibility = camVisibility;
        if (_gap is not null) _gap.Visibility = camVisibility;
    }

    static TextBlock Glyph(double size) => new()
    {
        FontFamily = Ui.Tokens.FontRes("IconFont"),
        FontSize = size,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

}
