using Shammy.Core.Agents;

namespace Shammy.Modules.Agents;

/// <summary>
/// What the Settings screen needs from this feature: where the connection to Claude Code stands, and the one
/// button that changes it. Null while the feature is turned off, so Settings copes without it.
/// </summary>
internal static class AgentsLink
{
    /// <summary>The status line, the button's word and whether it can be pressed, read fresh.</summary>
    public static Func<AgentConnectView>? View { get; set; }

    /// <summary>
    /// Presses the button (connect, disconnect or reconnect, whichever it says). This is the only path that
    /// changes Claude Code's settings. Returns whether it worked and one sentence to show.
    /// </summary>
    public static Func<(bool Ok, string Said)>? Press { get; set; }

    /// <summary>The connection or the day's session count changed.</summary>
    public static event Action? Changed;

    public static void RaiseChanged() => Changed?.Invoke();
}
