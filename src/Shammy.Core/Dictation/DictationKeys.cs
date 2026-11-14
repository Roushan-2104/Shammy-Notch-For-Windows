namespace Shammy.Core.Dictation;

/// <summary>The key that dictates (Settings › Voice › Dictation key).</summary>
public enum DictationKey
{
    /// <summary>Hold Ctrl + Win (either of each) and talk; let go of either to type it.</summary>
    CtrlWin,

    /// <summary>Hold Right Alt in a text box to dictate; anywhere else it stays the voice command key.</summary>
    RightAlt,

    /// <summary>Tap Right Alt twice to start, once to stop.</summary>
    DoubleTap,

    Off,
}

public static class DictationKeys
{
    public static readonly IReadOnlyList<(DictationKey Key, string Name, string Label)> All = new[]
    {
        (DictationKey.CtrlWin, "ctrlwin", "Hold Ctrl + Win"),
        (DictationKey.RightAlt, "rightalt", "Right Alt (smart)"),
        (DictationKey.DoubleTap, "double", "Double-tap Right Alt"),
        (DictationKey.Off, "off", "Off"),
    };

    /// <summary>"ctrlwin", "rightalt", "double" or "off" (any case); null for anything else.</summary>
    public static DictationKey? Parse(string? name)
    {
        var n = (name ?? "").Trim();
        foreach (var k in All)
            if (string.Equals(k.Name, n, StringComparison.OrdinalIgnoreCase)) return k.Key;
        return null;
    }

    public static string Name(DictationKey key) => All.FirstOrDefault(k => k.Key == key).Name ?? "off";

    public static string Label(DictationKey key) => All.FirstOrDefault(k => k.Key == key).Label ?? "Off";
}

/// <summary>A key as the Ctrl + Win gesture sees it.</summary>
public enum GestureKey { LeftCtrl, RightCtrl, LeftWin, RightWin, Other }

/// <summary>What a key event did to the Ctrl + Win gesture.</summary>
public enum GestureStep { None, Start, Stop, Cancel }

/// <summary>
/// Hold Ctrl + Win to dictate, decided key by key so the keyboard hook never waits: it starts once a Ctrl and a Win
/// are both down with no other key pressed since the first of them, stops when either is let go, and any other key
/// while it runs cancels it (Ctrl+Win+D, Ctrl+Win+arrows keep working; nothing here is swallowed). Auto-repeat of the
/// held keys changes nothing. Not thread-safe: only the hook thread calls it.
/// </summary>
public sealed class CtrlWinGesture
{
    bool _leftCtrl, _rightCtrl, _leftWin, _rightWin, _spoiled, _active;

    public bool Active => _active;

    bool Ctrl => _leftCtrl || _rightCtrl;
    bool Win => _leftWin || _rightWin;

    public GestureStep Key(GestureKey key, bool down)
    {
        bool heldBefore = Ctrl || Win;
        switch (key)
        {
            case GestureKey.LeftCtrl: _leftCtrl = down; break;
            case GestureKey.RightCtrl: _rightCtrl = down; break;
            case GestureKey.LeftWin: _leftWin = down; break;
            case GestureKey.RightWin: _rightWin = down; break;
            default:
                if (!down) return GestureStep.None;
                // A shortcut: drop the dictation (if any) and don't start one until Ctrl and Win are both let go.
                if (heldBefore) _spoiled = true;
                if (!_active) return GestureStep.None;
                _active = false;
                return GestureStep.Cancel;
        }

        if (!Ctrl && !Win) _spoiled = false;
        if (down)
        {
            if (_active || _spoiled || !Ctrl || !Win) return GestureStep.None;
            _active = true;
            return GestureStep.Start;
        }
        if (!_active || (Ctrl && Win)) return GestureStep.None;
        _active = false;
        // Pressing the let-go key again while the other is still held is not a new dictation.
        _spoiled = Ctrl || Win;
        return GestureStep.Stop;
    }

    /// <summary>Forgets every key (key-ups on the lock screen never arrive).</summary>
    public void Reset() => _leftCtrl = _rightCtrl = _leftWin = _rightWin = _spoiled = _active = false;
}
