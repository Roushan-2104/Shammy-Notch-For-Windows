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
        try
        {
            uint result = PowerSetActiveOverlayScheme(PowerMode.IdOf(kind));
            if (result != 0) Log.Warn($"Windows refused power mode {kind} (error {result})");
            return result == 0;
        }
        catch (Exception ex)
        {
            Log.Warn("Setting the power mode failed", ex);
            return false;
        }
    }

    // ---- Sleep on Modern Standby laptops -----------------------------------------------------------------

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool GetPwrCapabilities(IntPtr capabilities);

    static bool? _modernStandby;

    /// <summary>
    /// Whether this PC sleeps the Modern Standby way (S0 low power idle, "AoAc" in SYSTEM_POWER_CAPABILITIES):
    /// there SetSuspendState has nothing to do, and turning the screen off is how it goes to sleep.
    /// </summary>
    public static bool ModernStandby()
    {
        if (_modernStandby is { } known) return known;
        // SYSTEM_POWER_CAPABILITIES is 76 bytes; AoAc is the BOOLEAN at offset 20 (after 15 BOOLEANs, two
        // throttle BYTEs, FastSystemS4, Hiberboot and WakeAlarmPresent).
        const int Size = 128, AoAcOffset = 20;
        var buffer = Marshal.AllocHGlobal(Size);
        try
        {
            for (int i = 0; i < Size; i++) Marshal.WriteByte(buffer, i, 0);
            _modernStandby = GetPwrCapabilities(buffer) && Marshal.ReadByte(buffer, AoAcOffset) != 0;
        }
        catch (Exception ex)
        {
            Log.Warn("Reading the power capabilities failed", ex);
            _modernStandby = false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return _modernStandby.Value;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

    /// <summary>
    /// Turns the screens off (WM_SYSCOMMAND SC_MONITORPOWER 2, broadcast), which on a Modern Standby laptop lets
    /// it drop into standby. A window that doesn't answer within two seconds is skipped rather than waited on.
    /// Call off the UI thread.
    /// </summary>
    public static bool DisplayOff()
    {
        const uint WmSysCommand = 0x0112, AbortIfHung = 0x2;
        var broadcast = new IntPtr(0xFFFF);
        try
        {
            if (SendMessageTimeout(broadcast, WmSysCommand, new IntPtr(0xF170), new IntPtr(2), AbortIfHung, 2000, out _) != IntPtr.Zero) return true;
            // A hung window timing out still left the others to turn the screen off.
            const int Timeout = 1460;
            return Marshal.GetLastWin32Error() == Timeout;
        }
        catch (Exception ex)
        {
            Log.Warn("Turning the screen off failed", ex);
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
