using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using Shammy.SystemInfo;
using Windows.Devices.Radios;

namespace Shammy.Modules.Palette;

/// <summary>
/// The built-in display's brightness, through the same power-plan setting the Windows brightness slider
/// changes (no WMI: see BrightnessService for why). External monitors don't follow it.
/// </summary>
internal static class BrightnessControl
{
    static readonly Guid VideoSubgroup = new("7516b95f-f776-4464-8c53-06167f40cc99");
    static readonly Guid Brightness = new("aded5e82-b909-4619-9949-f5d71dac0bcb");

    /// <summary>The level for the current power source (plugged in or on battery), 0–100; null if unreadable.</summary>
    public static int? Get()
    {
        // The palette asks on every keystroke of "brightness"; the value only changes through keys or us.
        if (DateTime.UtcNow - _readAt < TimeSpan.FromSeconds(2)) return _last;
        _last = Read();
        _readAt = DateTime.UtcNow;
        return _last;
    }

    static int? _last;
    static DateTime _readAt = DateTime.MinValue;

    static int? Read()
    {
        if (!ActiveScheme(out var scheme)) return null;
        var sub = VideoSubgroup;
        var setting = Brightness;
        uint value;
        uint err = OnBattery()
            ? PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out value)
            : PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out value);
        return err == 0 ? (int)Math.Clamp(value, 0, 100) : null;
    }

    /// <summary>Writes the level for the current power source and re-applies the plan so it takes effect now.</summary>
    public static bool Set(int percent)
    {
        if (!ActiveScheme(out var scheme)) return false;
        var sub = VideoSubgroup;
        var setting = Brightness;
        uint value = (uint)Math.Clamp(percent, 0, 100);
        uint err = OnBattery()
            ? PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value)
            : PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
        bool ok = err == 0 && PowerSetActiveScheme(IntPtr.Zero, ref scheme) == 0;
        if (ok)
        {
            _last = (int)value;
            _readAt = DateTime.UtcNow;
        }
        return ok;
    }

    static bool ActiveScheme(out Guid scheme)
    {
        scheme = Guid.Empty;
        if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero) return false;
        try { scheme = Marshal.PtrToStructure<Guid>(ptr); }
        finally { LocalFree(ptr); }
        return true;
    }

    static bool OnBattery() => GetSystemPowerStatus(out var s) && s.ACLineStatus == 0;

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);

    [DllImport("powrprof.dll")]
    static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);

    [DllImport("powrprof.dll")]
    static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);

    [DllImport("powrprof.dll")]
    static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);

    [DllImport("powrprof.dll")]
    static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
}

internal sealed record AudioOutput(string Id, string Name, bool IsDefault);

/// <summary>
/// Output devices, the default one's volume and mute, and switching the default. Windows has no public
/// API for switching, so it uses IPolicyConfig, the interface the Sound control panel itself calls
/// (also used by EarTrumpet and SoundSwitch); the id passed is always one Windows just enumerated.
/// </summary>
internal sealed class AudioOutputs : IDisposable
{
    /// <summary>How long a device list is trusted before a keystroke asks for a fresh one in the background.</summary>
    static readonly TimeSpan ListLifetime = TimeSpan.FromSeconds(2);

    readonly MMDeviceEnumerator _enumerator = new();
    IReadOnlyList<(string Id, string Name)> _devices = Array.Empty<(string, string)>();
    DateTime _listedAt = DateTime.MinValue;
    Task? _listing;

    /// <summary>
    /// The cached output devices, the default one first. Reading every device's name from its property
    /// store costs ~250 ms, so that never happens here: a stale list is refreshed on a worker thread and
    /// <see cref="ListChanged"/> says when the new one is in. The default flag is read live (~1 ms).
    /// </summary>
    public IReadOnlyList<AudioOutput> List()
    {
        RefreshIfStale();
        string? current = DefaultId();
        return _devices
            .Select(d => new AudioOutput(d.Id, d.Name, d.Id == current))
            .OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Raised on the calling (UI) thread's context when a background refresh found a different list.</summary>
    public event Action? ListChanged;

    /// <summary>Starts a background refresh when the list is older than <see cref="ListLifetime"/> (or <paramref name="force"/>).</summary>
    public void RefreshIfStale(bool force = false)
    {
        if (_listing is not null || (!force && DateTime.UtcNow - _listedAt < ListLifetime)) return;
        var context = SynchronizationContext.Current;
        _listing = Task.Run(ReadDevices).ContinueWith(t =>
        {
            _listing = null;
            _listedAt = DateTime.UtcNow;
            if (t.Result is not { } fresh || fresh.SequenceEqual(_devices)) return;
            _devices = fresh;
            ListChanged?.Invoke();
        }, context is null ? TaskScheduler.Default : TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>On a worker thread, with its own enumerator (Core Audio is free-threaded).</summary>
    static IReadOnlyList<(string Id, string Name)>? ReadDevices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var list = new List<(string, string)>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try { list.Add((device.ID, device.FriendlyName)); }
                finally { AudioDeviceRelease.Dispose(device); }
            }
            return list;
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Warn("Listing audio outputs failed", ex);
            return null;
        }
    }

    /// <summary>For the test pipe: waits for a fresh list.</summary>
    public IReadOnlyList<AudioOutput> ListNow()
    {
        if (ReadDevices() is { } fresh)
        {
            _devices = fresh;
            _listedAt = DateTime.UtcNow;
        }
        return List();
    }

    string? DefaultId()
    {
        MMDevice? device = null;
        try
        {
            device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            AudioDeviceRelease.Dispose(device);
        }
    }

    /// <summary>Level 0–1 and mute of the default output; null when there is none.</summary>
    public (float Level, bool Muted)? Volume()
    {
        MMDevice? device = null;
        try
        {
            device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return (device.AudioEndpointVolume.MasterVolumeLevelScalar, device.AudioEndpointVolume.Mute);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            AudioDeviceRelease.Dispose(device);
        }
    }

    public void SetVolume(float level, bool? mute = null)
    {
        MMDevice? device = null;
        try
        {
            device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var v = device.AudioEndpointVolume;
            v.MasterVolumeLevelScalar = Math.Clamp(level, 0, 1);
            // Turning it up from a muted state means "I want sound", like the hardware keys.
            if (mute is { } m) v.Mute = m;
            else if (v.Mute && level > 0) v.Mute = false;
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Warn("Changing volume failed", ex);
        }
        finally
        {
            AudioDeviceRelease.Dispose(device);
        }
    }

    public void ToggleMute()
    {
        if (Volume() is { } v) SetVolume(v.Level, !v.Muted);
    }

    /// <summary>Makes the device the default for every role, as the Sound panel's "Set Default" does.</summary>
    public bool SetDefault(string deviceId)
    {
        if (List().All(d => d.Id != deviceId)) return false;
        object? client = null;
        try
        {
            client = new PolicyConfigClient();
            var config = (IPolicyConfig)client;
            bool ok = true;
            for (int role = 0; role <= 2; role++) ok &= config.SetDefaultEndpoint(deviceId, role) >= 0;
            return ok;
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Warn("Switching audio output failed", ex);
            return false;
        }
        finally
        {
            if (client is not null) Marshal.ReleaseComObject(client);
        }
    }

    public void Dispose() => _enumerator.Dispose();

    // Undocumented but stable since Windows 7; the method order is the vtable order and must not change.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int store, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int store, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient
    {
    }
}

/// <summary>The Bluetooth radio's on/off switch (the same one as in Quick Settings).</summary>
internal static class BluetoothRadio
{
    /// <summary>True/false for on/off; null when there is no radio or Windows won't let us see it.</summary>
    public static async Task<bool?> GetAsync()
    {
        try
        {
            return await FindAsync() is { } radio ? radio.State == RadioState.On : null;
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Warn("Reading the Bluetooth radio failed", ex);
            return null;
        }
    }

    public static async Task<bool> SetAsync(bool on)
    {
        try
        {
            if (await FindAsync() is not { } radio) return false;
            return await radio.SetStateAsync(on ? RadioState.On : RadioState.Off) == RadioAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Warn("Switching Bluetooth failed", ex);
            return false;
        }
    }

    /// <summary>
    /// The state without waiting, once the radio has been found (reading <c>State</c> is a cheap property);
    /// null before that or without a radio. Finding it is slow (see <see cref="Radios"/>), so that runs
    /// ahead (<see cref="Prefetch"/>), never on a keystroke.
    /// </summary>
    public static bool? Cached
    {
        get
        {
            try { return Radios.Cached(RadioKind.Bluetooth) is { } r ? r.State == RadioState.On : null; }
            catch (Exception) { return null; }
        }
    }

    public static void Prefetch() => _ = FindAsync();

    static Task<Radio?> FindAsync() => Radios.FindAsync(RadioKind.Bluetooth);
}

/// <summary>
/// Finds the Wi-Fi and Bluetooth radios (the switches in Quick Settings) for the palette and the Laptop card.
/// The first look takes ~230 ms, so it runs on a worker thread, one look at a time; radios found are kept.
/// </summary>
internal static class Radios
{
    static readonly Dictionary<RadioKind, Radio> Found = new();
    static Task? _finding;

    public static Radio? Cached(RadioKind kind)
    {
        lock (Found) return Found.GetValueOrDefault(kind);
    }

    public static async Task<Radio?> FindAsync(RadioKind kind)
    {
        if (Cached(kind) is { } known) return known;
        Task finding;
        lock (Found) finding = _finding ??= Task.Run(LookAsync);
        await finding.ConfigureAwait(false);
        return Cached(kind);
    }

    static async Task LookAsync()
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return;
            foreach (var radio in await Radio.GetRadiosAsync())
                lock (Found) Found.TryAdd(radio.Kind, radio);
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Warn("Finding the radios failed", ex);
        }
        finally
        {
            // Without a radio (or access), the next ask looks again.
            lock (Found) _finding = null;
        }
    }
}
