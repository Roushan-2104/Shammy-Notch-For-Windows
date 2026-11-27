using System.Windows;
using System.Windows.Media;
using Shammy.Motion;

namespace Shammy.Shell.Glass;

/// <summary>
/// One element of the island family (a wing's slab, its dot) that frosted glass draws behind. <see cref="Track"/> follows
/// it on the shared frame clock for a moment (a pour, a slide, a fade), then stops: nothing ticks while it's still.
/// </summary>
internal sealed class GlassPiece
{
    readonly string _key;
    readonly FrameworkElement _element;
    readonly Func<double> _radius;
    readonly double _depth;
    IDisposable? _frames;
    DateTime _until;

    /// <param name="radius">Its corner radius in its own units.</param>
    /// <param name="depth">Its tint: 0 light (a dot), 1 dark (a slab that carries text).</param>
    public GlassPiece(string key, FrameworkElement element, Func<double> radius, double depth = 0)
    {
        _depth = depth;
        _key = key;
        _element = element;
        _radius = radius;
        element.IsVisibleChanged += (_, _) => Track();
        element.SizeChanged += (_, _) => Track();
        GlassSurfaces.RunningChanged += OnRunning;
    }

    void OnRunning() => Track();

    /// <summary>Follow it for <paramref name="seconds"/> (long enough for its own animation).</summary>
    public void Track(double seconds = 0.5)
    {
        if (!GlassSurfaces.Running)
        {
            Stop();
            return;
        }
        var until = DateTime.UtcNow.AddSeconds(seconds);
        if (until > _until) _until = until;
        _frames ??= Animator.Subscribe(_ => OnFrame());
        Publish();
    }

    void OnFrame()
    {
        Publish();
        if (DateTime.UtcNow > _until) Stop();
    }

    void Stop()
    {
        _frames?.Dispose();
        _frames = null;
        if (!GlassSurfaces.Running) GlassSurfaces.Remove(_key);
    }

    public void Publish()
    {
        if (!GlassSurfaces.Running) return;
        double opacity = Opacity(_element);
        if (!_element.IsVisible || opacity < 0.01 || _element.ActualWidth < 1 || PresentationSource.FromVisual(_element) is null)
        {
            GlassSurfaces.Remove(_key);
            return;
        }
        try
        {
            var a = _element.PointToScreen(new Point(0, 0));
            var b = _element.PointToScreen(new Point(_element.ActualWidth, _element.ActualHeight));
            var rect = new Rect(a, b);
            double px = rect.Width / _element.ActualWidth;
            double r = _radius() * px;
            GlassSurfaces.Set(_key, new GlassShape(rect, r, r, opacity, _depth));
        }
        catch (InvalidOperationException)
        {
            GlassSurfaces.Remove(_key);
        }
    }

    /// <summary>The element's opacity times its parents' (what you actually see).</summary>
    internal static double Opacity(DependencyObject element)
    {
        double o = 1;
        for (var d = element; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is UIElement u) o *= u.Opacity;
        return o;
    }

    /// <summary>Its window closed: gone for good (a new window brings its own piece under the same key).</summary>
    public void Remove()
    {
        GlassSurfaces.RunningChanged -= OnRunning;
        _frames?.Dispose();
        _frames = null;
        GlassSurfaces.Remove(_key);
    }
}
