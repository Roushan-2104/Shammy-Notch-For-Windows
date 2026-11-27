using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using Shammy.Core.Notch;
using Shammy.Infrastructure;
using Shammy.Interop;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;
using WinColor = Windows.UI.Color;

namespace Shammy.Shell.Glass;

/// <summary>
/// Frosted glass (on by default): Windows itself blurs whatever is behind the island family. One click-through window sits
/// just under the family's windows; on it, each piece (see <see cref="GlassSurfaces"/>) is a shape filled with
/// Windows' own backdrop blur and a dark tint so white text stays readable (the island's colour waves draw on top, in
/// its own window). Nothing is captured or drawn by Shammy: Windows does the blur as it does for its own menus, and
/// nothing runs while it's off.
/// </summary>
internal sealed class FrostedGlass : IDisposable
{

    readonly Func<IReadOnlyList<IntPtr>> _family;
    Compositor? _compositor;
    DesktopWindowTarget? _target;
    ContainerVisual? _root;
    GlassWindow? _window;
    CompositionBrush? _blur;
    Int32Rect _windowRect;
    int _shapes;
    bool _queued;

    /// <summary>Everything the last reshape made (visuals, clips, outlines), let go when the next one replaces it.</summary>
    readonly List<IDisposable> _made = new();

    /// <summary>A moment after the last change the window shrinks back to fit what's left (a wing folded away).</summary>
    readonly System.Windows.Threading.DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(700) };

    /// <param name="family">The island family's windows, which the frosted window must stay under.</param>
    public FrostedGlass(Func<IReadOnlyList<IntPtr>> family)
    {
        _family = family;
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            _windowRect = default;
            Reshape();
        };
    }

    public bool Running => _window is not null;

    /// <summary>Starts it (UI thread). False when this Windows can't (it's then off until Shammy restarts).</summary>
    /// <param name="hidden">Kept out of screen capture, as the island is now.</param>
    public bool Start(bool hidden)
    {
        Stop();
        try
        {
            EnsureDispatcherQueue();
            _window = new GlassWindow();
            HideFromCapture(hidden);
            Accent.HostBackdrop(_window.Handle);
            _compositor = new Compositor();
            _compositor.As<ICompositorDesktopInterop>().CreateDesktopWindowTarget(_window.Handle, true, out var target);
            _target = MarshalInterface<DesktopWindowTarget>.FromAbi(target);
            Marshal.Release(target);
            _root = _compositor.CreateContainerVisual();
            _target.Root = _root;
            // One blur, shared by every piece (each has its own tint: see FrostedGlassRules.TintAlpha).
            _blur = _compositor.CreateHostBackdropBrush();
            Reshape();
            Log.Info("Frosted glass: on");
            return true;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or InvalidCastException or ArgumentException or DllNotFoundException or EntryPointNotFoundException or TypeLoadException)
        {
            Log.Warn("Frosted glass couldn't start", ex);
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        _settle.Stop();
        _root?.Children.RemoveAll();
        Release();
        _target?.Dispose();
        _target = null;
        _root = null;
        _blur?.Dispose();
        _blur = null;
        _compositor?.Dispose();
        _compositor = null;
        _window?.DestroyHandle();
        _window = null;
        _windowRect = default;
        _shapes = 0;
    }

    /// <summary>
    /// The island family moved, resized or faded (UI thread). Several pieces report in one frame, so the reshape runs
    /// once, after them.
    /// </summary>
    public void Update()
    {
        if (_queued || _window is null) return;
        _queued = true;
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () =>
        {
            _queued = false;
            Reshape();
        });
    }

    /// <summary>
    /// Fits the window and redraws the pieces (see <see cref="FrostWindowFit"/>: it only grows while they move, so
    /// only Windows' compositor moves their shapes, in step with the island itself).
    /// </summary>
    void Reshape()
    {
        if (_window is null || _compositor is null || _root is null) return;
        try
        {
            var shapes = GlassSurfaces.Current();
            if (shapes.Count == 0)
            {
                _windowRect = default;
                _shapes = 0;
                _window.Hide();
                return;
            }
            var union = shapes[0].Screen;
            foreach (var s in shapes) union.Union(s.Screen);
            var pieces = new ScreenBox((int)Math.Floor(union.Left), (int)Math.Floor(union.Top), (int)Math.Ceiling(union.Right), (int)Math.Ceiling(union.Bottom));
            var current = new ScreenBox(_windowRect.X, _windowRect.Y, _windowRect.X + _windowRect.Width, _windowRect.Y + _windowRect.Height);
            var (window, shrinkSoon) = FrostWindowFit.Next(current, pieces);
            var rect = new Int32Rect(window.Left, window.Top, window.Width, window.Height);

            // Few pieces (the island, a dot or two, a wing): rebuilding them is simpler than keeping score.
            _root.Children.RemoveAll();
            Release();
            foreach (var s in shapes) _root.Children.InsertAtTop(Piece(s, rect.X, rect.Y));
            _shapes = shapes.Count;
            if (shrinkSoon)
            {
                _settle.Stop(); // restarts the wait
                _settle.Start();
            }
            _windowRect = rect;
            // Same place when it fits: this only keeps it just under the family in the z-order. With none of the
            // family showing there's nothing to sit under, so it hides rather than go on top.
            var under = Lowest(_family());
            if (under == IntPtr.Zero) _window.Hide();
            else _window.Place(rect, under);
        }
        catch (ExternalException ex)
        {
            // Windows' compositor went away (a graphics reset, a display change): off until the rules run again.
            Log.Warn("Frosted glass: reshape failed", ex);
            Stop();
        }
    }

    void Release()
    {
        foreach (var d in _made) d.Dispose();
        _made.Clear();
    }

    T Keep<T>(T made) where T : IDisposable
    {
        _made.Add(made);
        return made;
    }

    /// <summary>
    /// One piece: blur plus tint inside its outline. Windows' rounded shapes have one radius for all four corners, so
    /// a piece whose top corners differ from its bottom ones is drawn as two halves, each cut from its own shape.
    /// </summary>
    Visual Piece(GlassShape s, int originX, int originY)
    {
        var c = _compositor!;
        float w = (float)s.Screen.Width, h = (float)s.Screen.Height, room = Math.Min(w, h) / 2;
        float top = Math.Min((float)s.TopRadius, room), bottom = Math.Min((float)s.BottomRadius, room);
        var piece = Keep(c.CreateContainerVisual());
        piece.Offset = new Vector3((float)s.Screen.X - originX, (float)s.Screen.Y - originY, 0);
        piece.Size = new Vector2(w, h);
        piece.Opacity = (float)Math.Clamp(s.Opacity, 0, 1);
        if (Math.Abs(top - bottom) < 0.5f)
        {
            piece.Children.InsertAtTop(Frost(w, h, bottom, s.Depth));
            return piece;
        }
        float split = MathF.Round(h / 2);
        foreach (var (radius, from, to) in new[] { (top, 0f, split), (bottom, split, h) })
        {
            var half = Keep(c.CreateContainerVisual());
            half.Size = new Vector2(w, h);
            var cut = Keep(c.CreateInsetClip());
            cut.TopInset = from;
            cut.BottomInset = h - to;
            half.Clip = cut;
            half.Children.InsertAtTop(Frost(w, h, radius, s.Depth));
            piece.Children.InsertAtTop(half);
        }
        return piece;
    }

    /// <summary>A rounded rectangle of Windows' backdrop blur under its tint (light resting, dark where there is text).</summary>
    Visual Frost(float w, float h, float radius, double depth)
    {
        var c = _compositor!;
        var outline = Keep(c.CreateRoundedRectangleGeometry());
        outline.Size = new Vector2(w, h);
        outline.CornerRadius = new Vector2(radius, radius);
        var shape = Keep(c.CreateContainerVisual());
        shape.Size = new Vector2(w, h);
        shape.Clip = Keep(c.CreateGeometricClip(outline));
        var blur = Keep(c.CreateSpriteVisual());
        blur.Size = shape.Size;
        blur.Brush = _blur;
        var tint = Keep(c.CreateSpriteVisual());
        tint.Size = shape.Size;
        tint.Brush = Keep(c.CreateColorBrush(WinColor.FromArgb(FrostedGlassRules.TintAlpha(depth), 20, 20, 24)));
        shape.Children.InsertAtTop(blur);
        shape.Children.InsertAtTop(tint);
        return shape;
    }

    /// <summary>Of the island family's windows, the one lowest in the z-order (the frosted window goes right under it).</summary>
    static IntPtr Lowest(IReadOnlyList<IntPtr> windows)
    {
        IntPtr lowest = IntPtr.Zero;
        foreach (var w in windows)
        {
            if (w == IntPtr.Zero || !Native.IsVisible(w)) continue;
            if (lowest == IntPtr.Zero) { lowest = w; continue; }
            // Walk down from the current lowest: finding w means w is lower still.
            for (var h = Native.NextBelow(lowest); h != IntPtr.Zero; h = Native.NextBelow(h))
            {
                if (h != w) continue;
                lowest = w;
                break;
            }
        }
        return lowest;
    }

    /// <summary>Out of screen capture exactly when the island is (Stage, a recording, Shammy's own screenshot).</summary>
    public void HideFromCapture(bool hidden)
    {
        if (_window is not null) Native.SetWindowDisplayAffinity(_window.Handle, hidden ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE);
    }

    /// <summary>The frosted window's place on screen (device pixels; empty while hidden).</summary>
    public Int32Rect WindowRect => _windowRect;

    public string Describe() =>
        $"frosted={(Running ? "on" : "off")} window={_windowRect.X},{_windowRect.Y} {_windowRect.Width}x{_windowRect.Height} shapes={_shapes}";

    public void Dispose() => Stop();

    static object? _queue;

    /// <summary>Windows' compositor needs a dispatcher queue on this (the UI) thread; WPF doesn't make one.</summary>
    static void EnsureDispatcherQueue()
    {
        if (_queue is not null || global::Windows.System.DispatcherQueue.GetForCurrentThread() is not null) return;
        var options = new DispatcherQueueOptions { Size = Marshal.SizeOf<DispatcherQueueOptions>(), ThreadType = 2, ApartmentType = 2 };
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out var controller));
        _queue = controller; // kept for the life of the app
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DispatcherQueueOptions { public int Size, ThreadType, ApartmentType; }

    [DllImport("CoreMessaging.dll")]
    static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool isTopmost, out IntPtr target);
    }

    /// <summary>
    /// Windows' "blur what's behind this window" switch (accent 5, host backdrop): without it the backdrop brush draws
    /// black. Undocumented but long-standing; Windows' own shell uses it.
    /// </summary>
    static class Accent
    {
        [StructLayout(LayoutKind.Sequential)] struct Policy { public int State, Flags, Gradient, Animation; }
        [StructLayout(LayoutKind.Sequential)] struct Data { public int Attribute; public IntPtr Value; public int Size; }

        [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref Data data);

        public static void HostBackdrop(IntPtr hwnd)
        {
            const int accentPolicy = 19, hostBackdrop = 5;
            var policy = new Policy { State = hostBackdrop };
            var p = Marshal.AllocHGlobal(Marshal.SizeOf<Policy>());
            try
            {
                Marshal.StructureToPtr(policy, p, false);
                var data = new Data { Attribute = accentPolicy, Value = p, Size = Marshal.SizeOf<Policy>() };
                if (SetWindowCompositionAttribute(hwnd, ref data) == 0) throw new InvalidOperationException("Windows refused the backdrop blur");
            }
            finally { Marshal.FreeHGlobal(p); }
        }
    }
}
