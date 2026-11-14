using System.Windows.Threading;

namespace Shammy.Infrastructure;

/// <summary>Runs something once, a moment from now, on the UI thread.</summary>
internal static class UiDelay
{
    /// <summary>Starts the wait; stop the returned timer to call it off.</summary>
    public static DispatcherTimer After(TimeSpan delay, Action action, Dispatcher? ui = null,
        DispatcherPriority priority = DispatcherPriority.Background)
    {
        var timer = new DispatcherTimer(priority, ui ?? Dispatcher.CurrentDispatcher) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
        return timer;
    }
}
