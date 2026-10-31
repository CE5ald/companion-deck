using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace CompanionDeck.Host.Telemetry;

/// <summary>
/// CPU, memory, disk, network and battery, read through the cheapest API available for each. CPU
/// uses GetSystemTimes and memory uses GlobalMemoryStatusEx - both are single syscalls with no
/// allocation, unlike WMI or a PerformanceCounter for the same values.
/// </summary>
public sealed class SystemCounters : IDisposable
{

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint Low; public uint High;
        public readonly ulong Value => ((ulong)High << 32) | Low; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength; public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);


    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);
    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE { public int CStatus; public double DoubleValue; }

    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtNoCap100 = 0x00008000;

    private IntPtr _pdhQuery, _diskCounter;
    private bool _pdhReady;

    // Kept in a query of its own, so reading it never disturbs the disk counter's interval.
    private IntPtr _freqQuery, _freqCounter;
    private bool _freqReady;


    private ulong _prevIdle, _prevKernel, _prevUser;
    private long _prevRx, _prevTx;
    private DateTime _prevNetAt = DateTime.MinValue;

    public long TotalRamMb { get; private set; }

    public void Initialize()
    {
        // Prime the CPU delta so the first published sample is meaningful.
        if (GetSystemTimes(out var i, out var k, out var u))
        {
            _prevIdle = i.Value; _prevKernel = k.Value; _prevUser = u.Value;
        }

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem)) TotalRamMb = (long)(mem.ullTotalPhys / (1024 * 1024));

        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _pdhQuery) == 0 &&
                PdhAddEnglishCounterW(_pdhQuery, @"\PhysicalDisk(_Total)\% Disk Time", IntPtr.Zero, out _diskCounter) == 0)
            {
                PdhCollectQueryData(_pdhQuery); // rate counters need a baseline
                _pdhReady = true;
            }
        }
        catch (Exception ex) { Log.Debug($"PDH disk counter unavailable: {ex.Message}"); }

        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _freqQuery) == 0 &&
                PdhAddEnglishCounterW(_freqQuery, @"\Processor Information(_Total)\% of Maximum Frequency", IntPtr.Zero, out _freqCounter) == 0)
            {
                PdhCollectQueryData(_freqQuery);
                _freqReady = true;
            }
        }
        catch (Exception ex) { Log.Debug($"PDH frequency counter unavailable: {ex.Message}"); }
    }

    /// <summary>The CPU clock as a percentage of its rated maximum; above 100 while boosting.</summary>
    public double? ReadCpuClockPercent()
    {
        if (!_freqReady) return null;
        if (PdhCollectQueryData(_freqQuery) != 0) return null;
        if (PdhGetFormattedCounterValue(_freqCounter, PdhFmtDouble | PdhFmtNoCap100, out _, out var v) != 0) return null;
        return v.DoubleValue is > 0 and < 400 ? v.DoubleValue : null;
    }

    public double ReadCpuPercent()
    {
        if (!GetSystemTimes(out var i, out var k, out var u)) return 0;

        ulong idle = i.Value, kernel = k.Value, user = u.Value;
        ulong dIdle = idle - _prevIdle, dKernel = kernel - _prevKernel, dUser = user - _prevUser;
        _prevIdle = idle; _prevKernel = kernel; _prevUser = user;

        // Kernel time already includes idle time.
        ulong total = dKernel + dUser;
        if (total == 0) return 0;
        double busy = (double)(total - dIdle) / total * 100.0;
        return Math.Clamp(busy, 0, 100);
    }

    public (double percent, long usedMb, long totalMb) ReadMemory()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref mem)) return (0, 0, TotalRamMb);

        long total = (long)(mem.ullTotalPhys / (1024 * 1024));
        long avail = (long)(mem.ullAvailPhys / (1024 * 1024));
        return (mem.dwMemoryLoad, total - avail, total);
    }

    public double? ReadDiskPercent()
    {
        if (!_pdhReady) return null;
        if (PdhCollectQueryData(_pdhQuery) != 0) return null;
        if (PdhGetFormattedCounterValue(_diskCounter, PdhFmtDouble | PdhFmtNoCap100, out _, out var v) != 0) return null;
        return Math.Clamp(v.DoubleValue, 0, 100);
    }

    public (double? downKbps, double? upKbps) ReadNetwork()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var st = nic.GetIPStatistics();
                rx += st.BytesReceived;
                tx += st.BytesSent;
            }
        }
        catch { return (null, null); }

        var now = DateTime.UtcNow;
        if (_prevNetAt == DateTime.MinValue)
        {
            _prevRx = rx; _prevTx = tx; _prevNetAt = now;
            return (0, 0);
        }

        double seconds = (now - _prevNetAt).TotalSeconds;
        if (seconds <= 0.01) return (null, null);

        double down = (rx - _prevRx) * 8.0 / 1000.0 / seconds;
        double up = (tx - _prevTx) * 8.0 / 1000.0 / seconds;
        _prevRx = rx; _prevTx = tx; _prevNetAt = now;

        return (Math.Max(0, down), Math.Max(0, up));
    }

    public (int? percent, bool? charging) ReadBattery()
    {
        if (!GetSystemPowerStatus(out var s)) return (null, null);
        int? pct = s.BatteryLifePercent == 255 ? null : s.BatteryLifePercent;
        bool? charging = s.ACLineStatus == 255 ? null : s.ACLineStatus == 1;
        return (pct, charging);
    }

    public void Dispose()
    {
        if (_freqReady)
        {
            try { PdhCloseQuery(_freqQuery); } catch { }
            _freqReady = false;
        }
        if (_pdhReady)
        {
            try { PdhCloseQuery(_pdhQuery); } catch { }
            _pdhReady = false;
        }
    }
}
