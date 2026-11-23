namespace Shammy.Motion;

/// <summary>
/// A start/stop ticker that runs on the app's one frame clock (<see cref="Animator"/>), so whatever it drives moves in
/// step with the screen and the island's springs, never between frames as a DispatcherTimer does. Stopped, it costs
/// nothing.
/// </summary>
internal sealed class FrameTicker(Action<double> onFrame)
{
    IDisposable? _frames;

    public bool IsEnabled => _frames is not null;

    public void Start() => _frames ??= Animator.Subscribe(onFrame);

    public void Stop()
    {
        _frames?.Dispose();
        _frames = null;
    }
}
