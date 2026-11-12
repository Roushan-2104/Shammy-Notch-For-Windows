using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Views.Cards;
using Shammy.Views.Screens;

namespace Shammy.Modules.Quick;

/// <summary>
/// The "asks first" step the Laptop and Health cards share: the first click arms (the control turns red and says
/// what will happen), a second click within six seconds does it, and after six seconds it quietly disarms. The
/// armed state lives here, not in a button, so a card that redraws keeps it.
/// </summary>
internal sealed class ConfirmStep
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(6);

    readonly DispatcherTimer _timer;
    readonly Action _changed;

    /// <param name="changed">Called when it arms or disarms, so the card redraws.</param>
    public ConfirmStep(Dispatcher ui, Action changed)
    {
        _changed = changed;
        _timer = new DispatcherTimer(Window, DispatcherPriority.Background, (_, _) => Disarm(), ui);
        _timer.Stop();
    }

    /// <summary>What is armed (a cleanup kind, "restart"); null when nothing is.</summary>
    public object? Armed { get; private set; }

    public bool Is(object what) => Equals(Armed, what);

    /// <summary>First click: arms <paramref name="what"/> (re-arming restarts the six seconds).</summary>
    public void Arm(object what)
    {
        Armed = what;
        _timer.Stop();
        _timer.Start();
        _changed();
    }

    public void Disarm()
    {
        _timer.Stop();
        if (Armed is null) return;
        Armed = null;
        _changed();
    }

    /// <summary>The armed look: red words (on the control's own surface); plain again when disarmed.</summary>
    public static void Look(Pressable button, bool armed)
    {
        Brush brush = armed ? CardKit.Alarm : ScreenKit.Fg;
        foreach (var t in Words(button)) t.Foreground = brush;
        if (!Watched.TryGetValue(button, out var state))
        {
            state = new Flag();
            Watched.Add(button, state);
            // The control repaints its ring on hover; the red ring goes back on after it.
            button.MouseEnter += (_, _) => Ring(button, state.Value);
            button.MouseLeave += (_, _) => Ring(button, state.Value);
        }
        state.Value = armed;
        if (armed) Ring(button, true);
        else button.Paint();
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Pressable, Flag> Watched = new();

    sealed class Flag
    {
        public bool Value;
    }

    static void Ring(Pressable button, bool armed)
    {
        if (armed) button.BorderBrush = CardKit.Alarm;
    }

    static IEnumerable<TextBlock> Words(Pressable button) => button.Child switch
    {
        TextBlock t => new[] { t },
        Panel p => p.Children.OfType<TextBlock>(),
        _ => Array.Empty<TextBlock>(),
    };
}
