using System.Diagnostics;
using System.Runtime.InteropServices;
using Shammy.Core.Quick;
using Shammy.Infrastructure;
using Windows.Devices.Radios;

namespace Shammy.Modules.Quick;

/// <summary>
/// The laptop's own switches the Laptop card flips: the Wi-Fi radio (as in Quick Settings), Windows 11's power
/// mode, and restart / shut down. Each one fails soft: a missing radio or a refused call returns null or false.
/// </summary>
internal static class LaptopSwitches
{
    // ---- Wi-Fi --------------------------------------------------------------------------------------

    static Radio? _wifi;

    static async Task<Radio?> WifiAsync()
    {
        if (_wifi is not null) return _wifi;
        if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return null;
        var radios = await Radio.GetRadiosAsync();
        return _wifi = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
    }

    /// <summary>True/false for on/off; null without a Wi-Fi radio.</summary>
    public static async Task<bool?> WifiAsync(bool? set)
    {
        try
        {
            if (await WifiAsync() is not { } radio) return null;
            if (set is { } on && (radio.State == RadioState.On) != on)
                if (await radio.SetStateAsync(on ? RadioState.On : RadioState.Off) != RadioAccessStatus.Allowed) return radio.State == RadioState.On;
            return radio.State == RadioState.On;
        }
        catch (Exception ex)
        {
            Log.Warn("Wi-Fi switch failed", ex);
            return null;
        }
    }

    // ---- Power mode ---------------------------------------------------------------------------------

    [DllImport("powrprof.dll")] static extern uint PowerGetEffectiveOverlayScheme(out Guid scheme);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveOverlayScheme(Guid scheme);

    /// <summary>The power mode now; null where Windows has no power-mode slider (a plan other than Balanced).</summary>
    public static PowerModeKind? GetPowerMode()
    {
        try { return PowerGetEffectiveOverlayScheme(out var id) == 0 ? PowerMode.From(id) : null; }
        catch (Exception ex)
        {
            Log.Warn("Reading the power mode failed", ex);
            return null;
        }
    }

    public static bool SetPowerMode(PowerModeKind kind)
    {
        try { return PowerSetActiveOverlayScheme(PowerMode.IdOf(kind)) == 0; }
        catch (Exception ex)
        {
            Log.Warn("Setting the power mode failed", ex);
            return false;
        }
    }

    // ---- Restart and shut down ------------------------------------------------------------------------

    /// <summary>Restarts (or shuts down) Windows now, the way Start does: apps are asked to close.</summary>
    public static bool PowerOff(bool restart)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("shutdown.exe", restart ? "/r /t 0" : "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
            return p is not null;
        }
        catch (Exception ex)
        {
            Log.Warn(restart ? "Restart failed" : "Shut down failed", ex);
            return false;
        }
    }
}
