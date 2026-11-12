using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;
using Shammy.Infrastructure;

namespace Shammy.Modules.Quick;

/// <summary>
/// Shammy's own night light: a warm amber tint at low strength over every screen. One plain Win32 window per
/// monitor, topmost, click-through, never activated, never in Alt+Tab or the taskbar, and left out of screenshots
/// and screen sharing (as Windows' own night light is). It follows monitors being plugged in, removed or
/// rearranged, and steps back on top now and then in case another topmost window covered it.
/// Made, moved and destroyed on the UI thread only.
/// </summary>
internal sealed class NightLightOverlay : IDisposable
{
    /// <summary>About 12 % strength.</summary>
    const byte Alpha = 31;

    // Warm amber, as COLORREF (0x00BBGGRR): R 255, G 147, B 41.
    const uint Amber = 0x002993FF;

    const string ClassName = "ShammyNightLight";
    static readonly TimeSpan KeepOnTopEvery = TimeSpan.FromSeconds(20);

    readonly Dispatcher _ui;
    readonly List<IntPtr> _windows = new();
    readonly DispatcherTimer _onTop;
    static ushort _class;
    bool _on, _listening;

    public NightLightOverlay(Dispatcher ui)
    {
        _ui = ui;
        _onTop = new DispatcherTimer(KeepOnTopEvery, DispatcherPriority.Background, (_, _) => KeepOnTop(), ui);
        _onTop.Stop();
    }

    public bool IsOn => _on;

    /// <summary>How many screens it covers now (for the state pipe).</summary>
    public int Screens => _windows.Count;

    /// <summary>Tints every screen; false if not even one window could be made.</summary>
    public bool Show()
    {
        if (_on) return true;
        Build();
        if (_windows.Count == 0) return false;
        _on = true;
        if (!_listening)
        {
            SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
            _listening = true;
        }
        _onTop.Start();
        return true;
    }

    public void Hide()
    {
        _on = false;
        _onTop.Stop();
        if (_listening)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
            _listening = false;
        }
        DestroyAll();
    }

    public void Dispose() => Hide();

    // SystemEvents raises this on its own thread.
    void OnDisplaysChanged(object? sender, EventArgs e) => _ui.BeginInvoke(() =>
    {
        if (!_on) return;
        Build();
    });

    void Build()
    {
        DestroyAll();
        if (!Register()) return;
        var hinst = GetModuleHandle(null);
        foreach (var r in Monitors())
        {
            var hwnd = CreateWindowEx(ExTransparent | ExLayered | ExToolWindow | ExNoActivate | ExTopmost, ClassName, "Shammy night light",
                WsPopup, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, IntPtr.Zero, IntPtr.Zero, hinst, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                Log.Warn($"Night light: a screen's tint couldn't be made (Windows error {Marshal.GetLastWin32Error()})");
                continue;
            }
            SetLayeredWindowAttributes(hwnd, 0, Alpha, LwaAlpha);
            // Screenshots and shared screens show the true colours (Windows 10 2004 and later; ignored before).
            SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
            ShowWindow(hwnd, SwShowNoActivate);
            _windows.Add(hwnd);
        }
    }

    void KeepOnTop()
    {
        foreach (var hwnd in _windows)
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    }

    void DestroyAll()
    {
        foreach (var hwnd in _windows) DestroyWindow(hwnd);
        _windows.Clear();
    }

    /// <summary>A window class whose own background brush is the tint, painted by Windows' default handler.</summary>
    static bool Register()
    {
        if (_class != 0) return true;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
            hInstance = GetModuleHandle(null),
            hbrBackground = CreateSolidBrush(Amber),
            lpszClassName = ClassName,
        };
        _class = RegisterClassEx(ref wc);
        if (_class == 0) Log.Warn($"Night light: the window class couldn't be registered (Windows error {Marshal.GetLastWin32Error()})");
        return _class != 0;
    }

    static List<RECT> Monitors()
    {
        var list = new List<RECT>();
        MonitorEnumProc each = (IntPtr _, IntPtr _, ref RECT r, IntPtr _) =>
        {
            list.Add(r);
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, each, IntPtr.Zero);
        GC.KeepAlive(each);
        return list;
    }

    // ---- Win32 ------------------------------------------------------------------------------------

    const uint ExTransparent = 0x20, ExLayered = 0x80000, ExToolWindow = 0x80, ExNoActivate = 0x08000000, ExTopmost = 0x8;
    const uint WsPopup = 0x80000000;
    const uint LwaAlpha = 0x2, WdaExcludeFromCapture = 0x11;
    const int SwShowNoActivate = 4;
    const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoActivate = 0x10, SwpNoOwnerZOrder = 0x200;
    static readonly IntPtr HwndTopmost = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Windows' default handling is all these windows need; held here for as long as the class lives.</summary>
    static readonly WndProc Proc = DefWindowProc;

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
