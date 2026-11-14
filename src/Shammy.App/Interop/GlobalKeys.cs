using System.Windows.Threading;

namespace Shammy.Interop;

/// <summary>Which hotkey: talk to Shammy, or look at the screen (freeze and circle).</summary>
internal enum HotKey
{
    /// <summary>Right Alt: hold to speak a command or question, tap to type. The screen stays live.</summary>
    Talk,

    /// <summary>The Copilot key, or Right Ctrl held on its own: freeze the screen to circle (and speak).</summary>
    Look,
}

/// <summary>
/// Routes the global keys Shammy cares about: Right Alt (talk), the Copilot key or a lone Right Ctrl (look),
/// a lone Left Alt held (hover to understand), the volume keys (so Windows' own volume flyout never appears)
/// and Escape while a search is open. Everything else passes through.
/// </summary>
internal sealed class GlobalKeys : IDisposable
{
    const int VK_RMENU = 0xA5, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_ESCAPE = 0x1B;
    const int VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_F23 = 0x86;
    const int VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;
    const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_MBUTTON = 0x04;

    // A lone Right Ctrl must be held this long before it counts, so Ctrl shortcuts never trigger it.
    static readonly TimeSpan RightCtrlHold = TimeSpan.FromMilliseconds(400);

    readonly Dispatcher _ui;
    readonly KeyboardHook _hook;
    readonly DispatcherTimer _rightCtrlTimer;
    volatile bool _talkHeld, _lookHeld;
    // AltGr (German, French, Spanish, Polish, Nordic... layouts) arrives as a fake LCtrl followed by RAlt
    // with the same timestamp. It types @ € { } [ ] and must reach the app untouched.
    uint _leftCtrlDownAt = uint.MaxValue;
    bool _altGr, _altGrTalk;
    // The Copilot key is sent as Win + Shift + F23 (repeating while held).
    bool _winDown, _shiftDown, _copilot;
    // Right Ctrl held with no other key: a candidate until the hold time passes.
    volatile bool _rightCtrlCandidate, _rightCtrlLook;

    public GlobalKeys(Dispatcher ui)
    {
        _ui = ui;
        _rightCtrlTimer = new DispatcherTimer(RightCtrlHold, DispatcherPriority.Input, (_, _) => OnRightCtrlHeld(), ui) { IsEnabled = false };
        _leftAltTimer = new DispatcherTimer(LeftAltHold, DispatcherPriority.Input, (_, _) => OnLeftAltHeld(), ui) { IsEnabled = false };
        _hook = new KeyboardHook(Filter);
        Current = this;
    }

    /// <summary>The running instance, for modules that listen to a key of their own (hover to understand).</summary>
    public static GlobalKeys? Current { get; private set; }

    public bool IsInstalled => _hook.IsInstalled;

    public volatile bool InterceptVolume;
    public volatile bool InterceptEscape;

    /// <summary>While "Did you mean" choices show: the number keys 1–4 (top row and keypad) and Enter pick one.</summary>
    public volatile bool InterceptChoices;

    /// <summary>A choice key: 1–4, or 0 for Enter.</summary>
    public event Action<int>? ChoiceKey;

    /// <summary>While the wheel is open: 1–8 (top row and keypad) run a bead, Esc closes it.</summary>
    public volatile bool InterceptWheel;

    /// <summary>A wheel key: 1–8, or 0 for Esc.</summary>
    public event Action<int>? WheelKey;

    public event Action<HotKey>? Pressed;
    public event Action<HotKey>? Released;

    /// <summary>The key turned out to be part of something else (AltGr+4 typing ₹): drop what it started.</summary>
    public event Action<HotKey>? Aborted;
    public event Action? EscapePressed;

    /// <summary>+1 volume up, -1 volume down, 0 mute toggle.</summary>
    public event Action<int>? VolumeKey;

    /// <summary>Caps Lock went down (once per press, not per auto-repeat); its new state isn't known yet.</summary>
    public event Action? CapsLockPressed;

    /// <summary>A language-switch shortcut started (Left Alt+Shift, Ctrl+Shift, Win+Space): note the layout now.</summary>
    public event Action? LanguageComboStarted;

    /// <summary>That shortcut was released, which is when Windows switches; compare the layout shortly after.</summary>
    public event Action? LanguageComboEnded;

    const int VK_CAPITAL = 0x14, VK_SPACE = 0x20, VK_LMENU = 0xA4;
    bool _capsDown, _altKey, _ctrlKey, _shiftKey, _langCombo, _langComboWin;

    /// <summary>
    /// Watches for the keys behind the Caps Lock and input-language pills. Only posts on those keys, so the
    /// UI does nothing while you type; nothing here is swallowed.
    /// </summary>
    void TrackKeyboardPills(int vk, bool down)
    {
        if (vk == VK_CAPITAL)
        {
            if (down && !_capsDown) Post(() => CapsLockPressed?.Invoke());
            _capsDown = down;
            return;
        }
        bool shift = vk is VK_LSHIFT or VK_RSHIFT, alt = vk == VK_LMENU, ctrl = vk is VK_LCONTROL or VK_RCONTROL;
        bool win = vk is VK_LWIN or VK_RWIN;
        if (shift) _shiftKey = down;
        if (alt) _altKey = down;
        if (ctrl) _ctrlKey = down;

        if (down && !_langCombo)
        {
            bool altShift = (shift && (_altKey || _ctrlKey)) || ((alt || ctrl) && _shiftKey);
            bool winSpace = vk == VK_SPACE && _winDown;
            if (altShift || winSpace)
            {
                _langCombo = true;
                _langComboWin = winSpace;
                Post(() => LanguageComboStarted?.Invoke());
            }
        }
        // Win+Space cycles while Win is held and switches when it's released; Alt+Shift switches on release.
        else if (!down && _langCombo && (_langComboWin ? win : shift || alt || ctrl))
        {
            _langCombo = false;
            Post(() => LanguageComboEnded?.Invoke());
        }
    }

    bool Filter(int vk, bool down, uint time)
    {
        // Our own menu mask key (see MaskAltMenu) is not "another key": it must never cancel the hover it serves.
        if (vk == VK_MENU_MASK) return false;
        TrackKeyboardPills(vk, down);
        // Any other key while Right Ctrl is down means a shortcut (Ctrl+C), not a hold.
        if (down && vk != VK_RCONTROL && _rightCtrlCandidate) _rightCtrlCandidate = false;
        // Any other key while Left Alt is down means a shortcut (Alt+Tab, Alt+F4, Alt+F): drop the hover, pass it all.
        if (down && vk != VK_LMENU && (_leftAltCandidate || _hoverHeld)) CancelHover();
        // Any other key while AltGr is down means a character (AltGr+4 = ₹), not talking. AltGr's own
        // auto-repeat resends the fake LCtrl, which doesn't count.
        if (down && _altGr && _altGrTalk && vk is not (VK_RMENU or VK_LCONTROL))
        {
            _altGrTalk = false;
            _talkHeld = false;
            Post(() => Aborted?.Invoke(HotKey.Talk));
        }

        switch (vk)
        {
            case VK_LCONTROL:
                if (down) _leftCtrlDownAt = time;
                return false;

            // On layouts with AltGr (English India for ₹, German, French, Spanish, Polish...) Right Alt
            // arrives as a fake LCtrl plus RAlt. It is passed through untouched (swallowing it would leave
            // the app a bare Ctrl, so AltGr+Q would arrive as Ctrl+Q) and still works as the talk key when
            // held on its own; pressing another key with it types the character and cancels talking.
            case VK_RMENU when _altGr || (down && !_talkHeld && time == _leftCtrlDownAt):
                if (down && !_altGr)
                {
                    _altGr = true;
                    _altGrTalk = true;
                    _talkHeld = true;
                    Post(() => Pressed?.Invoke(HotKey.Talk));
                }
                else if (!down)
                {
                    _altGr = false;
                    if (_altGrTalk)
                    {
                        _altGrTalk = false;
                        _talkHeld = false;
                        Post(() => Released?.Invoke(HotKey.Talk));
                    }
                }
                return false;

            case VK_RMENU:
                if (down && !_talkHeld)
                {
                    _talkHeld = true;
                    Post(() => Pressed?.Invoke(HotKey.Talk));
                }
                else if (!down && _talkHeld)
                {
                    _talkHeld = false;
                    Post(() => Released?.Invoke(HotKey.Talk));
                }
                return true;

            // Never swallowed: a tap still opens the app's menu, and every Alt shortcut works as before.
            case VK_LMENU:
                TrackLeftAlt(down);
                return false;

            case VK_LWIN or VK_RWIN:
                _winDown = down;
                return false;

            case VK_LSHIFT or VK_RSHIFT:
                _shiftDown = down;
                return false;

            // Swallowed, so Windows doesn't also open Copilot or Search. Win and Shift pass through; with
            // Shift in between, releasing Win doesn't open Start.
            case VK_F23 when _copilot || (down && _winDown && _shiftDown):
                if (down && !_copilot)
                {
                    _copilot = true;
                    StartLook();
                }
                else if (!down)
                {
                    _copilot = false;
                    EndLook();
                }
                return true;

            // Never swallowed: Right Ctrl is still Ctrl for every shortcut.
            case VK_RCONTROL:
                if (down && !_rightCtrlCandidate && !_rightCtrlLook && !_lookHeld)
                {
                    _rightCtrlCandidate = true;
                    Post(() =>
                    {
                        _rightCtrlTimer.Stop();
                        _rightCtrlTimer.Start();
                    });
                }
                else if (!down)
                {
                    _rightCtrlCandidate = false;
                    if (_rightCtrlLook)
                    {
                        _rightCtrlLook = false;
                        EndLook();
                    }
                }
                return false;

            case VK_VOLUME_UP or VK_VOLUME_DOWN or VK_VOLUME_MUTE when InterceptVolume:
                if (down)
                {
                    int dir = vk == VK_VOLUME_UP ? 1 : vk == VK_VOLUME_DOWN ? -1 : 0;
                    Post(() => VolumeKey?.Invoke(dir));
                }
                return true;

            case (>= 0x31 and <= 0x38) or (>= 0x61 and <= 0x68) or VK_ESCAPE when InterceptWheel:
            {
                int n = vk == VK_ESCAPE ? 0 : vk >= 0x61 ? vk - 0x60 : vk - 0x30;
                if (down) Post(() => WheelKey?.Invoke(n));
                return true;
            }

            case VK_ESCAPE when InterceptEscape:
                if (down) Post(() => EscapePressed?.Invoke());
                return true;

            case (>= 0x31 and <= 0x34) or (>= 0x61 and <= 0x64) or 0x0D when InterceptChoices:
            {
                int n = vk == 0x0D ? 0 : vk >= 0x61 ? vk - 0x60 : vk - 0x30;
                if (down) Post(() => ChoiceKey?.Invoke(n));
                return true;
            }
        }
        return false;
    }

    void OnRightCtrlHeld()
    {
        _rightCtrlTimer.Stop();
        // Ctrl+click and Ctrl+drag are shortcuts too, and the keyboard hook can't see the mouse.
        bool mouse = Native.GetAsyncKeyState(VK_LBUTTON) < 0 || Native.GetAsyncKeyState(VK_RBUTTON) < 0 || Native.GetAsyncKeyState(VK_MBUTTON) < 0;
        if (!_rightCtrlCandidate || mouse || _lookHeld) return;
        _rightCtrlCandidate = false;
        _rightCtrlLook = true;
        StartLook();
    }

    // ---- Left Alt held: hover to understand ------------------------------------------------------------------------
    //
    // A lone Left Alt held for half a second starts a hover; letting go ends it. Left Alt is never swallowed, so a
    // quick tap still reaches the app (its menu bar) and Alt+Tab, Alt+F4 and Alt+letter work exactly as before: any
    // other key while Alt is down cancels the hover. Once a hover starts, one unassigned key (vkE8) is sent so that
    // releasing Alt doesn't open the app's menu bar afterwards (the same trick AutoHotkey's menu mask key uses).

    const int VK_MENU_MASK = 0xE8;
    static readonly TimeSpan LeftAltHold = TimeSpan.FromMilliseconds(500);

    readonly DispatcherTimer _leftAltTimer;
    volatile bool _leftAltDown, _leftAltCandidate, _hoverHeld;

    /// <summary>Hover to understand is on (Settings). Off: Left Alt holds are ignored entirely.</summary>
    public volatile bool HoverEnabled;

    /// <summary>Asked on the UI thread when a hold is about to start a hover (e.g. not over a fullscreen game); null = always.</summary>
    public Func<bool>? HoverAllowed { get; set; }

    /// <summary>Left Alt has been held on its own for half a second: point at something.</summary>
    public event Action? HoverPressed;

    /// <summary>Left Alt was let go, or another key turned the hold into a shortcut: the bubble goes.</summary>
    public event Action? HoverReleased;

    /// <summary>Whether a hover is running (Left Alt held past the wait, nothing else pressed).</summary>
    public bool HoverHeld => _hoverHeld;

    void TrackLeftAlt(bool down)
    {
        if (down)
        {
            if (_leftAltDown) return; // auto-repeat
            _leftAltDown = true;
            // Alt with Ctrl, Shift or Win already down is a shortcut (Ctrl+Alt+Del, Alt+Shift), never a hover.
            if (!HoverEnabled || _ctrlKey || _shiftKey || _winDown) return;
            _leftAltCandidate = true;
            Post(() =>
            {
                _leftAltTimer.Stop();
                _leftAltTimer.Start();
            });
        }
        else
        {
            _leftAltDown = false;
            CancelHover();
        }
    }

    void CancelHover()
    {
        _leftAltCandidate = false;
        if (!_hoverHeld) return;
        _hoverHeld = false;
        Post(() => HoverReleased?.Invoke());
    }

    void OnLeftAltHeld()
    {
        _leftAltTimer.Stop();
        // Alt+click and Alt+drag are shortcuts too, and the keyboard hook can't see the mouse.
        bool mouse = Native.GetAsyncKeyState(VK_LBUTTON) < 0 || Native.GetAsyncKeyState(VK_RBUTTON) < 0 || Native.GetAsyncKeyState(VK_MBUTTON) < 0;
        if (!_leftAltCandidate || mouse || !HoverEnabled || _hoverHeld) return;
        if (HoverAllowed is { } allowed && !allowed()) return;
        _leftAltCandidate = false;
        _hoverHeld = true;
        // Let go in the moment between the check and now: no hover after all.
        if (!_leftAltDown)
        {
            _hoverHeld = false;
            return;
        }
        MaskAltMenu();
        HoverPressed?.Invoke();
    }

    /// <summary>One press of an unassigned key while Alt is down, so the Alt release isn't read as "open the menu".</summary>
    static void MaskAltMenu()
    {
        const uint KEYEVENTF_KEYUP = 0x2;
        keybd_event(VK_MENU_MASK, 0, 0, UIntPtr.Zero);
        keybd_event(VK_MENU_MASK, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    // ---- Look --------------------------------------------------------------------------------------------------------

    void StartLook()
    {
        if (_lookHeld) return;
        _lookHeld = true;
        Post(() => Pressed?.Invoke(HotKey.Look));
    }

    void EndLook()
    {
        if (!_lookHeld) return;
        _lookHeld = false;
        Post(() => Released?.Invoke(HotKey.Look));
    }

    void Post(Action action) => _ui.BeginInvoke(action);

    public void Dispose()
    {
        _rightCtrlTimer.Stop();
        _leftAltTimer.Stop();
        if (Current == this) Current = null;
        _hook.Dispose();
    }
}
