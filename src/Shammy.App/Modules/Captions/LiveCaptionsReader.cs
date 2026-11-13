using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using Shammy.Core.Captions;
using Shammy.Infrastructure;

namespace Shammy.Modules.Captions;

/// <summary>
/// Reads Windows Live Captions (LiveCaptions.exe, Windows 11 22H2+) so its words can show in Shammy's strip.
/// <list type="bullet">
/// <item>Starts Live Captions if it isn't running (and closes it again afterwards only if Shammy started it).</item>
/// <item>Keeps its own window out of the way the way open-source readers do: minimised, and out of the taskbar
/// and Alt+Tab (WS_EX_TOOLWINDOW). Live Captions keeps captioning while minimised. Its placement is saved first,
/// to a small file as well, so it is put back exactly when captions turn off, or on the next start if Shammy
/// stopped without turning them off.</item>
/// <item>Reads the caption text through UI Automation: the window of class "LiveCaptionsDesktopWindow", the
/// element with AutomationId "CaptionsTextBlock", its Name. One cross-process read every ~450 ms, on a
/// background thread; the element is found once and found again only when it goes stale.</item>
/// </list>
/// </summary>
internal sealed class LiveCaptionsReader : IDisposable
{
    static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(450);

    /// <summary>How long Live Captions gets to show its caption text before it is shown to the user (first run setup).</summary>
    static readonly TimeSpan SetupAfter = TimeSpan.FromSeconds(8);

    static string SavedPlacementFile => Path.Combine(Paths.Root, "livecaptions-window.json");

    readonly Action<string> _text;
    readonly Action<string, Exception?> _warn;
    readonly CancellationTokenSource _stop = new();
    Task _loop = Task.CompletedTask;
    IntPtr _hwnd;
    WINDOWPLACEMENT? _saved;
    bool _addedToolStyle, _startedIt, _shownForSetup;
    AutomationElement? _textBlock;
    string _last = "";

    /// <param name="text">The whole caption text, each time it changes (called on a background thread).</param>
    public LiveCaptionsReader(Action<string> text, Action<string, Exception?> warn)
    {
        _text = text;
        _warn = warn;
    }

    /// <summary>Live Captions' window was found and is being read.</summary>
    public bool Attached => _hwnd != IntPtr.Zero;

    /// <summary>Its caption text was found (false while its first-run setup is showing).</summary>
    public bool Reading => _textBlock is not null;

    /// <summary>"minimised", "shown for setup", or "-".</summary>
    public string WindowState => _hwnd == IntPtr.Zero ? "-" : _shownForSetup ? "shown for setup" : _saved is not null ? "minimised" : "as it was";

    public int Reads { get; private set; }
    public int Changes { get; private set; }
    public bool StartedIt => _startedIt;

    /// <summary>Whether this PC has Live Captions (Windows 11 22H2 or later, and the app present).</summary>
    public static bool IsSupported() =>
        LiveCaptionsText.Supported(Environment.OSVersion.Version.Build) &&
        File.Exists(Path.Combine(Environment.SystemDirectory, "LiveCaptions.exe"));

    /// <summary>Windows' own Captions settings page, where Live Captions is set up.</summary>
    public static void OpenSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:easeofaccess-closedcaptioning") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Opening the captions settings failed", ex); }
    }

    public void Start() => _loop = Task.Run(RunAsync);

    async Task RunAsync()
    {
        var token = _stop.Token;
        var since = Stopwatch.StartNew();
        try
        {
            if (FindWindow(LiveCaptionsText.WindowClass, null) == IntPtr.Zero)
            {
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "LiveCaptions.exe")) { UseShellExecute = true })?.Dispose();
                _startedIt = true;
            }
            while (!token.IsCancellationRequested)
            {
                Tick(since.Elapsed);
                await Task.Delay(Poll, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Problem = _hwnd == IntPtr.Zero ? "Couldn’t start Windows Live Captions" : "Reading Windows Live Captions stopped";
            _warn("Reading Live Captions stopped", ex);
        }
    }

    /// <summary>Why reading stopped, in plain words; null while all is well.</summary>
    public string? Problem { get; private set; }

    void Tick(TimeSpan elapsed)
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd))
        {
            _textBlock = null;
            _hwnd = FindWindow(LiveCaptionsText.WindowClass, null);
            if (_hwnd == IntPtr.Zero) return;
            _saved = null;
            _shownForSetup = false;
        }
        if (_textBlock is null)
        {
            try
            {
                _textBlock = AutomationElement.FromHandle(_hwnd)?.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, LiveCaptionsText.TextId));
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
            {
                _textBlock = null;
            }
            if (_textBlock is null)
            {
                // Its first run asks to download a language: the user has to see the window to say yes.
                if (elapsed > SetupAfter && !_shownForSetup)
                {
                    RestoreWindow();
                    _shownForSetup = true;
                }
                return;
            }
            _shownForSetup = false;
            HideWindow();
        }
        string text;
        try
        {
            text = _textBlock.Current.Name ?? "";
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            _textBlock = null;
            return;
        }
        Reads++;
        if (string.Equals(text, _last, StringComparison.Ordinal)) return;
        _last = text;
        Changes++;
        _text(text);
    }

    // ---- Its window ---------------------------------------------------------------------------------

    void HideWindow()
    {
        if (_hwnd == IntPtr.Zero || _saved is not null) return;
        var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(_hwnd, ref placement)) return;
        _saved = placement;
        long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        _addedToolStyle = (ex & WS_EX_TOOLWINDOW) == 0;
        Remember(placement, _addedToolStyle);
        if (_addedToolStyle) SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));
        ShowWindow(_hwnd, SW_SHOWMINNOACTIVE);
    }

    /// <summary>Puts the window back where it was, as it was.</summary>
    void RestoreWindow()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;
        if (_addedToolStyle)
        {
            long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex & ~WS_EX_TOOLWINDOW));
            _addedToolStyle = false;
        }
        if (_saved is { } placement)
        {
            // Never back to minimised: that is how Shammy left it, not how the user had it.
            if (placement.showCmd is SW_SHOWMINIMIZED or SW_MINIMIZE or SW_SHOWMINNOACTIVE) placement.showCmd = SW_SHOWNOACTIVATE;
            placement.flags = 0;
            SetWindowPlacement(_hwnd, ref placement);
        }
        else
        {
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        }
        _saved = null;
        Forget();
    }

    static void Remember(WINDOWPLACEMENT p, bool addedToolStyle)
    {
        try
        {
            var saved = new SavedPlacement(p.showCmd, p.rcNormalPosition.Left, p.rcNormalPosition.Top, p.rcNormalPosition.Right,
                p.rcNormalPosition.Bottom, addedToolStyle);
            File.WriteAllText(SavedPlacementFile, JsonSerializer.Serialize(saved));
        }
        catch (Exception ex) { Log.Warn("Saving Live Captions' window placement failed", ex); }
    }

    static void Forget()
    {
        try { File.Delete(SavedPlacementFile); }
        catch (Exception ex) { Log.Warn("Clearing Live Captions' saved placement failed", ex); }
    }

    sealed record SavedPlacement(int ShowCmd, int Left, int Top, int Right, int Bottom, bool AddedToolStyle);

    /// <summary>
    /// Shammy stopped while it had Live Captions minimised (a crash, a power cut): put its window back now. Runs
    /// once at start-up and does nothing unless that happened.
    /// </summary>
    public static void RecoverStranded()
    {
        try
        {
            if (!File.Exists(SavedPlacementFile)) return;
            var saved = JsonSerializer.Deserialize<SavedPlacement>(File.ReadAllText(SavedPlacementFile));
            var hwnd = FindWindow(LiveCaptionsText.WindowClass, null);
            if (saved is not null && hwnd != IntPtr.Zero)
            {
                if (saved.AddedToolStyle)
                {
                    long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                    SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex & ~WS_EX_TOOLWINDOW));
                }
                var p = new WINDOWPLACEMENT
                {
                    length = Marshal.SizeOf<WINDOWPLACEMENT>(),
                    showCmd = saved.ShowCmd is SW_SHOWMINIMIZED or SW_MINIMIZE or SW_SHOWMINNOACTIVE ? SW_SHOWNOACTIVATE : saved.ShowCmd,
                    rcNormalPosition = new RECT { Left = saved.Left, Top = saved.Top, Right = saved.Right, Bottom = saved.Bottom },
                };
                SetWindowPlacement(hwnd, ref p);
                Log.Info("Live Captions' window was put back after Shammy stopped unexpectedly");
            }
            File.Delete(SavedPlacementFile);
        }
        catch (Exception ex)
        {
            Log.Warn("Putting Live Captions' window back failed", ex);
        }
    }

    /// <summary>Stops reading, puts Live Captions' window back, and closes Live Captions if Shammy started it.</summary>
    public void Dispose()
    {
        _stop.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); }
        catch (Exception) { }
        _textBlock = null;
        try
        {
            if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
            {
                RestoreWindow();
                // Started for captions, so it goes with them (its own placement was restored first, so it remembers that).
                if (_startedIt) PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            else
            {
                Forget();
            }
        }
        catch (Exception ex)
        {
            _warn("Putting Live Captions back failed", ex);
        }
        _hwnd = IntPtr.Zero;
        _stop.Dispose();
    }

    // ---- Win32 --------------------------------------------------------------------------------------

    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x80;
    const int SW_SHOWMINIMIZED = 2, SW_MINIMIZE = 6, SW_SHOWMINNOACTIVE = 7, SW_SHOWNOACTIVATE = 4;
    const uint WM_CLOSE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? className, string? windowName);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
}
