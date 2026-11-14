using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Shammy.Core.Notch;
using Shammy.Infrastructure;
using Rgb = Shammy.Core.Music.Rgb;

namespace Shammy.Shell;

/// <summary>
/// The floating island's look: the gap above it, the fake glass behind it, the soft waves inside it and the rim light
/// along its lower edge. Waves and rim move (~20 fps, one capped timer) only while something is happening — music, a
/// call, focus, a live activity — and hold still otherwise, so a resting island repaints nothing.
/// </summary>
public partial class NotchWindow
{
    /// <summary>The island's plain colour, when there is no glass.</summary>
    static readonly Color PlainIsland = Color.FromRgb(0x08, 0x08, 0x0A);

    /// <summary>
    /// What the bits drawn beside the island (the action circle and its bridge, the side bubble, the tab) are filled
    /// with: the plain colour, or the glass's own colour around the closed island so they match it. Not frozen, so a
    /// new wallpaper repaints them.
    /// </summary>
    readonly SolidColorBrush _islandFill = new(PlainIsland);

    /// <summary>Room for the island's shadow, the tab under it and the cards peeking below a message.</summary>
    const double ShadowPad = 36;

    /// <summary>The glass covers the whole window, which never grows past the canvas.</summary>
    const double GlassHeight = 1100;

    /// <summary>The gap above the island in Root's own units (4 px at 100 %, snapped to whole pixels).</summary>
    double _gap = IslandGeometry.Gap;

    IslandGlass _glass = null!;
    WriteableBitmap _waves = null!;
    byte[] _wavePx = null!;
    DispatcherTimer _ambientTick = null!, _ambientSlow = null!;
    readonly System.Diagnostics.Stopwatch _ambientClock = new();
    double _waveTime, _breathTime;
    IslandLookPlan _plan;
    RimState _rimState;
    bool _transparency = true, _batterySaver, _busy;
    (int X, int Y) _windowPx;
    bool _windowPlaced, _boundsDirty = true;

    void InitIsland()
    {
        Shape.Fill = _islandFill;
        TuckTab.Background = _islandFill;
        Bubble.Background = _islandFill;
        _glass = new IslandGlass(Dispatcher);
        _glass.Changed += ApplyLook;
        _waves = new WriteableBitmap(IslandWaves.Width, IslandWaves.Height, 96, 96, PixelFormats.Pbgra32, null);
        _wavePx = new byte[IslandWaves.Width * IslandWaves.Height * 4];
        Shape.Waves = _waves;
        // Something to look at from the first frame, even before anything plays.
        RenderWaves();

        _ambientTick = new DispatcherTimer(TimeSpan.FromMilliseconds(1000.0 / IslandLook.AmbientFps), DispatcherPriority.Render, (_, _) => OnAmbientTick(), Dispatcher) { IsEnabled = false };
        // A meeting coming up isn't an event the island hears about: look now and then (repaints only on a change).
        _ambientSlow = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => UpdateAmbience(), Dispatcher);

        _transparency = ReadTransparency();
        _batterySaver = ReadBatterySaver();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        try { Windows.System.Power.PowerManager.EnergySaverStatusChanged += OnEnergySaverChanged; }
        catch (Exception ex) { Log.Warn("Island: Battery Saver can't be watched", ex); }
        Theme.PaletteTheme.Changed += OnPaletteChanged;
        Closed += (_, _) =>
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
            try { Windows.System.Power.PowerManager.EnergySaverStatusChanged -= OnEnergySaverChanged; }
            catch (Exception) { }
            Theme.PaletteTheme.Changed -= OnPaletteChanged;
            _ambientTick.Stop();
            _ambientSlow.Stop();
        };
    }

    // ---- What's going on ---------------------------------------------------------------------------

    /// <summary>Something started or stopped (music, a call, focus, a setting): the island's glass, waves and rim follow.</summary>
    void UpdateAmbience()
    {
        // Not built yet (the palette is chosen early in the constructor).
        if (_waves is null || _restView is null) return;
        var media = _services.Media;
        bool music = media.IsPlaying, micLive = false, focus = false;
        var now = DateTime.UtcNow;
        foreach (var a in _ranked)
        {
            if (PetFor(a, now)?.State is not { } s) continue;
            if (s.Costume == Core.Notch.PetCostume.Recording || (s.Costume == Core.Notch.PetCostume.Call && !s.Muted)) micLive = true;
            if (s.Costume == Core.Notch.PetCostume.Timer && !s.Done && !s.Paused) focus = true;
        }
        bool meeting = _restView.Mood.Meeting is not null;
        _rimState = RimLight.Pick(music, focus, micLive, meeting);
        bool shown = _mode != NotchMode.Hidden && !_tucked;
        bool listening = _mode is NotchMode.Listening or NotchMode.Circling or NotchMode.Thinking;
        // A paused song stays among the activities; it isn't something happening.
        bool live = _ranked.Any(a => a.Kind != Core.Activities.ActivityKind.NowPlaying);
        _busy = shown && (music || micLive || focus || meeting || listening || live);
        _plan = IslandLook.Decide(new IslandConditions(_transparency, _batterySaver, SystemParameters.HighContrast, MotionPrefs.Lively, _busy));

        Edge.SetState(shown ? RimColour() : null);
        bool run = shown && (_plan.AnimateWaves || (_plan.Breathe && _rimState != RimState.None && Edge.Enabled));
        if (!_plan.Breathe) Edge.Breath = RimLight.Steady;
        if (run && !_ambientTick.IsEnabled)
        {
            _ambientClock.Restart();
            _ambientTick.Start();
        }
        else if (!run && _ambientTick.IsEnabled)
        {
            _ambientTick.Stop();
        }
        ApplyLook();
    }

    /// <summary>The rim's state colour: music in the album's colour, focus blue, a live microphone red, a meeting soon green.</summary>
    Color? RimColour()
    {
        var album = Theme.PaletteTheme.FromAlbum ? Theme.PaletteTheme.First
            : _services.Media.ArtColor is var art && art != Colors.White ? new Rgb(art.R, art.G, art.B)
            : Theme.PaletteTheme.First;
        return RimLight.Colour(_rimState, album) is { } c ? Theme.PaletteTheme.ToColor(c) : null;
    }

    /// <summary>Push the plan to the island: glass or plain tint, waves shown (softer on an open slab) or not.</summary>
    void ApplyLook()
    {
        if (_waves is null) return;
        var glass = _plan.Glass ? _glass.Image : null;
        bool open = _spec.H > 80;
        double waves = _plan.Waves ? (open ? 0.7 : 1) : 0;
        var fill = glass is null ? PlainIsland : _glass.Average;
        if (_islandFill.Color != fill) _islandFill.Color = fill;
        var rect = new Rect(0, -_gap, WindowWidth, GlassHeight);
        if (ReferenceEquals(Shape.Glass, glass) && Shape.WavesOpacity == waves && Shape.GlassRect == rect) return;
        // The action circle beside the island is the same liquid: same glass, same place.
        _chrome.SetSurface(glass, rect);
        Shape.Glass = glass;
        Shape.GlassRect = rect;
        Shape.WavesOpacity = waves;
        Shape.InvalidateVisual();
    }

    void OnAmbientTick()
    {
        double dt = Math.Min(0.2, _ambientClock.Elapsed.TotalSeconds);
        _ambientClock.Restart();
        if (_plan.AnimateWaves)
        {
            _waveTime += dt;
            RenderWaves();
        }
        if (_plan.Breathe && _rimState != RimState.None)
        {
            _breathTime += dt;
            Edge.Breath = RimLight.Breath(_breathTime);
        }
    }

    /// <summary>One frame of the waves into the tiny bitmap; WPF redraws the island with it (scaled up, soft).</summary>
    void RenderWaves()
    {
        IslandWaves.Render(_wavePx, IslandWaves.Width, IslandWaves.Height, Theme.PaletteTheme.Colours, _waveTime, 1);
        _waves.WritePixels(new Int32Rect(0, 0, IslandWaves.Width, IslandWaves.Height), _wavePx, IslandWaves.Width * 4, 0);
    }

    /// <summary>Test pipe: "waves off", "rim off", "content off" (and "on") hide one part of the island's drawing.</summary>
    internal string SetPartForTest(string arg)
    {
        var p = arg.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 2) return "error: usage island-part waves|rim|content|shape on|off";
        var v = p[1] == "on" ? Visibility.Visible : Visibility.Hidden;
        switch (p[0])
        {
            case "waves": Shape.WavesOpacity = p[1] == "on" ? 0.7 : 0; Shape.InvalidateVisual(); break;
            case "rim": Edge.Visibility = v; break;
            case "content": ContentHost.Visibility = v; break;
            case "shape": Shape.Visibility = v; break;
            case "software":
                // "software on": WPF draws this window on the CPU instead of the graphics card.
                if (System.Windows.Interop.HwndSource.FromHwnd(_hwnd)?.CompositionTarget is { } target)
                    target.RenderMode = p[1] == "on" ? System.Windows.Interop.RenderMode.SoftwareOnly : System.Windows.Interop.RenderMode.Default;
                break;
            default: return "error: waves, rim, content, shape or software";
        }
        return "ok";
    }

    void OnPaletteChanged()
    {
        // New colours: redraw the (possibly still) waves once, and the rim if it shows the album.
        RenderWaves();
        UpdateAmbience();
    }

    // ---- Windows settings that change the look ---------------------------------------------------------

    static bool ReadTransparency()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int value || value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return true;
        }
    }

    static bool ReadBatterySaver()
    {
        try { return Windows.System.Power.PowerManager.EnergySaverStatus == Windows.System.Power.EnergySaverStatus.On; }
        catch (Exception) { return false; }
    }

    // System events arrive on their own thread.
    void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        _transparency = ReadTransparency();
        // A new wallpaper (or slideshow picture) comes as a Desktop change.
        if (e.Category is UserPreferenceCategory.Desktop or UserPreferenceCategory.General) _glass.Invalidate();
        UpdateGlass();
        UpdateAmbience();
    });

    void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        _glass.Invalidate();
        _boundsDirty = true;
        UpdateGlass();
    });

    void OnEnergySaverChanged(object? sender, object e) => Dispatcher.BeginInvoke(() =>
    {
        _batterySaver = ReadBatterySaver();
        UpdateAmbience();
    });

    void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast) or nameof(SystemParameters.ClientAreaAnimation))
            Dispatcher.BeginInvoke(UpdateAmbience);
    }

    // ---- Placement -----------------------------------------------------------------------------------

    /// <summary>The window's top-left in device pixels, from <see cref="PlaceInPixels"/>; the gap and the glass follow.</summary>
    void Placed(int x, int y, uint dpi)
    {
        _windowPx = (x, y);
        _windowPlaced = true;
        _boundsDirty = true;
        double dpiScale = dpi / 96.0;
        _gap = IslandGeometry.SnappedGap(dpiScale) / _scale;
        UpdateGlass();
        ApplyVisuals(force: true);
        ApplyLook();
    }

    /// <summary>Make (or keep) the glass for where the window is now; skipped while the plan has no glass.</summary>
    /// <summary>Test only: the glass uses this picture instead of the wallpaper ("off" goes back).</summary>
    internal string SetTestWallpaper(string arg)
    {
        var path = arg.Trim().Trim('"');
        IslandGlass.TestWallpaper = path.Length == 0 || path == "off" ? null : path;
        _glass.Invalidate();
        UpdateGlass();
        return IslandGlass.TestWallpaper is null ? "ok: the real wallpaper" : "ok: " + IslandGlass.TestWallpaper;
    }

    void UpdateGlass()
    {
        if (_glass is null || !_windowPlaced || _hwnd == IntPtr.Zero) return;
        if (!_transparency || _batterySaver || SystemParameters.HighContrast) return;
        if (Interop.Native.GetDpiForMonitor(Monitor, 0, out uint dpi, out _) != 0 || dpi == 0) return;
        double px = dpi / 96.0 * _scale;
        _glass.Update(Monitor, _windowPx.X, _windowPx.Y, (int)Math.Round(WindowWidth * px), (int)Math.Round(GlassHeight * px));
    }

    /// <summary>Once the island has settled (or moved), tell other windows where it is (see <see cref="IslandBounds"/>).</summary>
    void PublishBounds(double w, double h, bool settled)
    {
        if (!settled || (!_boundsDirty && w == _publishedSize.W && h == _publishedSize.H)) return;
        if (PresentationSource.FromVisual(Root) is null) return;
        _boundsDirty = false;
        _publishedSize = (w, h);
        try
        {
            var a = Root.PointToScreen(new Point(Cx - w / 2, 0));
            var b = Root.PointToScreen(new Point(Cx + w / 2, h));
            IslandBounds.Set(h > 0.5 && !_tucked ? new Rect(a, b) : Rect.Empty, Monitor);
        }
        catch (InvalidOperationException)
        {
            _boundsDirty = true;
        }
    }

    (double W, double H) _publishedSize = (-1, -1);

    internal string DescribeIsland() =>
        $"gap={_gap * _scale:0.##} glass={(Shape.Glass is not null ? "on" : "off")} {_glass.Describe()} transparency={_transparency} batterySaver={_batterySaver} " +
        $"highContrast={SystemParameters.HighContrast} busy={_busy} waves={(_plan.Waves ? (_plan.AnimateWaves ? "moving" : "still") : "off")} " +
        $"ticking={_ambientTick.IsEnabled} {Edge.Describe()} state={_rimState} {Theme.PaletteTheme.Describe()} bounds={IslandBounds.Screen}";
}
