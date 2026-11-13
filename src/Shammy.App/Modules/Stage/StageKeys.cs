using System.Windows.Threading;
using Shammy.Interop;

namespace Shammy.Modules.Stage;

/// <summary>A key Stage answers to.</summary>
internal enum StageKey { Space, Back, On, Bigger, Smaller, Close, Light, Captions, Mirror, Voice }

/// <summary>
/// Stage's keys without taking the keyboard from the meeting or camera app: while the pointer rests on Stage,
/// Space, the arrows, + and −, Esc, and L (light), C (captions), M (mirror), V (voice) go to Stage and nowhere else;
/// with the pointer anywhere else every key goes where it always does. The hook exists only while Stage is open.
/// </summary>
internal sealed class StageKeys : IDisposable
{
    const int VK_SPACE = 0x20, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_ESCAPE = 0x1B;
    const int VK_OEM_PLUS = 0xBB, VK_OEM_MINUS = 0xBD, VK_ADD = 0x6B, VK_SUBTRACT = 0x6D;
    const int VK_C = 0x43, VK_L = 0x4C, VK_M = 0x4D, VK_V = 0x56;
    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    readonly Dispatcher _ui;
    readonly Action<StageKey> _pressed;
    readonly KeyboardHook _hook;
    // Keys swallowed on the way down are swallowed on the way up too, so no app sees half a key press.
    readonly HashSet<int> _held = new();
    volatile bool _armed;

    public StageKeys(Dispatcher ui, Action<StageKey> pressed)
    {
        _ui = ui;
        _pressed = pressed;
        _hook = new KeyboardHook(Filter);
    }

    public bool IsInstalled => _hook.IsInstalled;

    /// <summary>Whether the pointer is on Stage (set from the UI thread, read on the hook's).</summary>
    public bool Armed
    {
        get => _armed;
        set => _armed = value;
    }

    // On the hook's thread: decide at once, never wait for the UI.
    bool Filter(int vk, bool down, uint time)
    {
        lock (_held)
        {
            if (!down) return _held.Remove(vk);
            if (!_armed || Modified()) return false;
            StageKey? key = vk switch
            {
                VK_SPACE => StageKey.Space,
                VK_LEFT or VK_UP => StageKey.Back,
                VK_RIGHT or VK_DOWN => StageKey.On,
                VK_ADD or VK_OEM_PLUS => StageKey.Bigger,
                VK_SUBTRACT or VK_OEM_MINUS => StageKey.Smaller,
                VK_ESCAPE => StageKey.Close,
                VK_L => StageKey.Light,
                VK_C => StageKey.Captions,
                VK_M => StageKey.Mirror,
                VK_V => StageKey.Voice,
                _ => null,
            };
            if (key is not { } k) return false;
            // Auto-repeat of a held key: only the arrows and the size keys repeat.
            bool repeat = !_held.Add(vk);
            if (!repeat || k is StageKey.Back or StageKey.On or StageKey.Bigger or StageKey.Smaller)
                _ui.BeginInvoke(() => _pressed(k));
            return true;
        }
    }

    /// <summary>Ctrl, Alt or Windows held (a shortcut, not a Stage key). Shift is allowed: + is Shift+= on most layouts.</summary>
    static bool Modified() =>
        Down(VK_CONTROL) || Down(VK_MENU) || Down(VK_LWIN) || Down(VK_RWIN);

    static bool Down(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        _armed = false;
        _hook.Dispose();
    }
}
