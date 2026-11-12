using System.Diagnostics;
using System.Runtime.InteropServices;
using Shammy.Core.MeetingSystem;
using Windows.System.Power;

namespace Shammy.Modules.MeetingSystem;

internal sealed record SystemSample(
    DateTime At,
    double Cpu,
    uint MemoryLoad,
    ulong TotalMemory,
    ulong AvailableMemory,
    ProcessUsage? Top,
    bool HasBattery,
    int BatteryPercent,
    bool PluggedIn,
    bool Charging,
    double? ReportedDrain)
{
    public ulong UsedMemory => TotalMemory - AvailableMemory;
}

/// <summary>
/// One reading of CPU, memory, the busiest app and the battery. CPU and memory are two cheap kernel
/// calls; walking every process is the expensive part, so callers ask for it only when needed.
/// Thread-safe; meant to run off the UI thread.
/// </summary>
internal sealed class SystemSampler
{
    readonly object _gate = new();
    readonly ProcessCpuTracker _processes = new();
    readonly Action<string, Exception?> _warn;
    ulong _idle, _kernel, _user;
    bool _batteryReportFailed;

    public SystemSampler(Action<string, Exception?> warn)
    {
        _warn = warn;
        lock (_gate) ReadCpu();
    }

    public SystemSample Sample(bool includeProcesses, double processThreshold = 0)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            double cpu = ReadCpu();

            var mem = new MeetingSystemNative.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MeetingSystemNative.MEMORYSTATUSEX>() };
            if (!MeetingSystemNative.GlobalMemoryStatusEx(ref mem)) mem = default;

            // No single app can be above the threshold unless the whole machine is.
            ProcessUsage? top = null;
            if (includeProcesses || (processThreshold > 0 && cpu >= processThreshold)) top = TopProcess(now);
            else _processes.Reset();

            bool hasBattery = false, plugged = true, charging = false;
            int percent = 0;
            double? reported = null;
            try
            {
                hasBattery = PowerManager.BatteryStatus != BatteryStatus.NotPresent;
                plugged = PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;
                percent = PowerManager.RemainingChargePercent;
                charging = PowerManager.BatteryStatus == BatteryStatus.Charging;
                // On battery this is the drain; while charging it comes out negative (the charge rate, for "Full by").
                if (hasBattery && (!plugged || charging)) reported = ReportedDrain();
            }
            catch (Exception ex)
            {
                _warn("Reading battery failed", ex);
            }

            return new SystemSample(now, cpu, mem.dwMemoryLoad, mem.ullTotalPhys, mem.ullAvailPhys, top,
                hasBattery, percent, plugged, charging, reported);
        }
    }

    double ReadCpu()
    {
        if (!MeetingSystemNative.GetSystemTimes(out var idleFt, out var kernelFt, out var userFt)) return 0;
        ulong idle = CpuMath.FileTime(idleFt.Low, idleFt.High);
        ulong kernel = CpuMath.FileTime(kernelFt.Low, kernelFt.High);
        ulong user = CpuMath.FileTime(userFt.Low, userFt.High);
        double cpu = _kernel == 0 ? 0 : CpuMath.SystemPercent(idle - _idle, kernel - _kernel, user - _user);
        (_idle, _kernel, _user) = (idle, kernel, user);
        return cpu;
    }

    ProcessUsage? TopProcess(DateTime now)
    {
        var samples = new List<ProcessSample>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                // The idle "process" is the machine doing nothing. Protected/system processes deny their times
                // (they are rarely the culprit): asked with the limited right, they just say no, with no exception.
                if (p.Id != 0 && MeetingSystemNative.ProcessCpuTime((uint)p.Id) is { } cpu)
                    samples.Add(new ProcessSample(p.Id, p.ProcessName, cpu));
            }
        }
        var usage = _processes.Update(samples, now, Environment.ProcessorCount);
        return usage.Count > 0 ? usage[0] : null;
    }

    /// <summary>Instantaneous drain from the battery's own report (%/h), when the driver provides one.</summary>
    double? ReportedDrain()
    {
        if (_batteryReportFailed) return null;
        try
        {
            var report = Windows.Devices.Power.Battery.AggregateBattery.GetReport();
            return DrainEstimator.FromReport(report.ChargeRateInMilliwatts, report.FullChargeCapacityInMilliwattHours);
        }
        catch (Exception ex)
        {
            _batteryReportFailed = true;
            _warn("Battery report unavailable; estimating drain from percentages", ex);
            return null;
        }
    }
}
