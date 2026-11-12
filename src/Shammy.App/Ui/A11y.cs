using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace Shammy.Ui;

/// <summary>
/// Screen-reader support for the code-built controls: <see cref="Announce"/> speaks a status that appears without
/// focus moving (an activity, a notification, "Saved"), and the peers below make clickable borders real buttons
/// and switches to UI Automation.
/// </summary>
internal static class A11y
{
    /// <summary>
    /// Asks the screen reader (if one is listening) to read <paramref name="text"/> out, politely, without moving
    /// focus. <paramref name="source"/> is the element showing it; with none, any visible Shammy window is used.
    /// Newer text with the same <paramref name="activityId"/> replaces older text still waiting to be read.
    /// </summary>
    public static void Announce(string? text, UIElement? source = null, string activityId = "Shammy.Status")
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var element = source is { IsVisible: true } ? source : AnyVisibleWindow();
            if (element is null) return;
            var peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
            peer?.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.MostRecent, text, activityId);
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Warn("Screen reader announcement failed", ex);
        }
    }

    static UIElement? AnyVisibleWindow()
    {
        if (Application.Current is not { } app) return null;
        Window? any = null;
        foreach (Window w in app.Windows)
        {
            if (!w.IsVisible) continue;
            if (w.IsActive) return w;
            any ??= w;
        }
        return any;
    }
}

/// <summary>A clickable element that screen readers see as a button (Invoke runs its click).</summary>
internal sealed class InvokePeer : FrameworkElementAutomationPeer, IInvokeProvider
{
    readonly Action _invoke;

    public InvokePeer(FrameworkElement owner, Action invoke) : base(owner) => _invoke = invoke;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

    protected override string GetClassNameCore() => Owner.GetType().Name;

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;

    protected override bool IsKeyboardFocusableCore() => Owner.Focusable;

    protected override string GetNameCore()
    {
        var name = base.GetNameCore();
        if (!string.IsNullOrEmpty(name)) return name;
        return Owner is FrameworkElement { ToolTip: string tip } ? tip : "";
    }

    public override object? GetPattern(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

    void IInvokeProvider.Invoke()
    {
        if (!IsEnabled()) throw new ElementNotEnabledException();
        Owner.Dispatcher.BeginInvoke(_invoke);
    }
}

/// <summary>An on/off element that screen readers see as a toggle with its state (a switch, a day toggle).</summary>
internal sealed class TogglePeer : FrameworkElementAutomationPeer, IToggleProvider
{
    readonly Func<bool> _isOn;
    readonly Action _toggle;

    public TogglePeer(FrameworkElement owner, Func<bool> isOn, Action toggle) : base(owner)
    {
        _isOn = isOn;
        _toggle = toggle;
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

    protected override string GetClassNameCore() => Owner.GetType().Name;

    protected override bool IsKeyboardFocusableCore() => Owner.Focusable;

    protected override string GetNameCore()
    {
        var name = base.GetNameCore();
        if (!string.IsNullOrEmpty(name)) return name;
        return Owner is FrameworkElement { ToolTip: string tip } ? tip : "";
    }

    public override object? GetPattern(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Toggle ? this : base.GetPattern(patternInterface);

    public ToggleState ToggleState => _isOn() ? ToggleState.On : ToggleState.Off;

    void IToggleProvider.Toggle()
    {
        if (!IsEnabled()) throw new ElementNotEnabledException();
        Owner.Dispatcher.BeginInvoke(_toggle);
    }

    /// <summary>Tells listeners the state changed (call after the switch flips).</summary>
    public void Changed(bool was, bool now) =>
        RaisePropertyChangedEvent(TogglePatternIdentifiers.ToggleStateProperty, was ? ToggleState.On : ToggleState.Off, now ? ToggleState.On : ToggleState.Off);
}

/// <summary>A border that clicks: screen readers see a button, and Enter or Space runs it when it has the keyboard.</summary>
internal class InvokableBorder : Border
{
    /// <summary>What Invoke (a screen reader's "press", Enter or Space) runs.</summary>
    public Action? Invoked { get; set; }

    protected override AutomationPeer OnCreateAutomationPeer() => new InvokePeer(this, () => Invoked?.Invoke());
}
