using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace CompanionDeck.Host.Telemetry;

/// <summary>
/// GPU utilisation and dedicated-memory use for any vendor, through the GPU performance counters
/// Windows itself publishes (the ones Task Manager reads). This is the fallback for machines
/// without NVML - an AMD or Intel laptop, or a desktop with no NVIDIA driver.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GpuCounters : IDisposable
{
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(
        IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);
    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue { public int CStatus; private readonly int _pad; public double Double; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem { public IntPtr Name; public CounterValue Value; }

    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtNoCap100 = 0x00008000;
    private const uint PdhMoreData = 0x800007D2;

    private IntPtr _query, _util, _vram;
    private bool _ready;

    // Reused between samples. These counters carry one instance per process per engine, so
    // a busy machine produces hundreds of items and reallocating each tick would be waste.
    private IntPtr _buffer;
    private uint _bufferSize;

    public string? DeviceName { get; private set; }
    public long? VramTotalMb { get; private set; }
    public bool Available => _ready;

    public bool TryInitialize()
    {
        // Registry first and synchronously: it is a handful of key reads, and VramTotalMb
        // has to be ready before the first sample or the tablet shows a usage with no
        // ceiling to compare it against.
        VramTotalMb = ReadVramFromRegistry();

        // The adapter name comes from WMI, which measured 7.6s cold on a laptop with a
        // hybrid GPU. It is a log line and a caption - nothing waits on it - so it must
        // not sit on the startup path.
        _ = Task.Run(() =>
        {
            var name = ReadAdapterName();
            if (name is null) return;
            DeviceName = name;
            Log.Info($"GPU adapter: {name}");
        });

        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) return false;

            // Per-process, per-engine instances. Summing the 3D engines across them is what
            // produces a figure comparable to Task Manager's own GPU column.
            if (PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage",
                                      IntPtr.Zero, out _util) != 0)
            {
                PdhCloseQuery(_query);
                _query = IntPtr.Zero;
                Log.Info("GPU performance counters not published on this Windows build");
                return false;
            }

            // Best effort: present on most drivers, absent on some. Utilisation alone is
            // still worth having, so a failure here is not fatal.
            if (PdhAddEnglishCounterW(_query, @"\GPU Adapter Memory(*)\Dedicated Usage",
                                      IntPtr.Zero, out _vram) != 0)
                _vram = IntPtr.Zero;

            PdhCollectQueryData(_query);
            _ready = true;
            Log.Info("GPU counters ready" + (VramTotalMb is { } v ? $", {v}MB VRAM" : ""));
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"GPU counters unavailable: {ex.Message}");
            return false;
        }
    }

    public GpuSample? Sample()
    {
        if (!_ready) return null;
        if (PdhCollectQueryData(_query) != 0) return null;

        var s = new GpuSample { VramTotalMb = VramTotalMb };

        if (SumInstances(_util, "engtype_3D") is { } util)
            s.UtilPercent = Math.Clamp(Math.Round(util, 1), 0, 100);

        if (_vram != IntPtr.Zero && MaxInstances(_vram) is { } bytes)
            s.VramUsedMb = (long)(bytes / (1024 * 1024));

        // Once, so a wrong counter path or a bad buffer walk is visible in the log rather
        // than showing up as a plausible-looking number on the tablet.
        if (!_loggedFirst)
        {
            _loggedFirst = true;
            Log.Info($"GPU first sample: util={s.UtilPercent?.ToString() ?? "null"}%, " +
                     $"vram={s.VramUsedMb?.ToString() ?? "null"}/{s.VramTotalMb?.ToString() ?? "null"}MB");
        }

        return s;
    }

    private bool _loggedFirst;

    /// <summary>
    /// Sums every instance whose name contains the filter. Returns 0 rather than null when the
    /// counter is live but every engine is idle, so an idle GPU reads as 0% instead of
    /// disappearing from the dashboard.
    /// </summary>
    private unsafe double? SumInstances(IntPtr counter, string nameFilter)
    {
        if (!Fill(counter, out uint count)) return null;

        var items = (CounterItem*)_buffer;
        double total = 0;
        for (uint i = 0; i < count; i++)
        {
            ref var item = ref items[i];
            if (item.Value.CStatus != 0) continue;
            if (!NameContains((char*)item.Name, nameFilter)) continue;
            total += item.Value.Double;
        }
        return total;
    }

    private unsafe double? MaxInstances(IntPtr counter)
    {
        if (!Fill(counter, out uint count)) return null;

        var items = (CounterItem*)_buffer;
        double best = 0;
        for (uint i = 0; i < count; i++)
        {
            ref var item = ref items[i];
            if (item.Value.CStatus == 0) best = Math.Max(best, item.Value.Double);
        }
        return best > 0 ? best : null;
    }

    /// <summary>Substring test straight over the native UTF-16 buffer, no allocation.</summary>
    private static unsafe bool NameContains(char* name, string needle)
    {
        if (name is null) return false;
        int len = 0;
        while (name[len] != '\0' && len < 512) len++;
        return new ReadOnlySpan<char>(name, len).Contains(needle, StringComparison.Ordinal);
    }

    private bool Fill(IntPtr counter, out uint count)
    {
        count = 0;
        uint size = _bufferSize;
        uint rc = _buffer == IntPtr.Zero
            ? PdhMoreData
            : PdhGetFormattedCounterArrayW(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out count, _buffer);

        if (rc == PdhMoreData)
        {
            size = 0;
            PdhGetFormattedCounterArrayW(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out count, IntPtr.Zero);
            if (size == 0) return false;
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = Marshal.AllocHGlobal((int)size);
            _bufferSize = size;
            rc = PdhGetFormattedCounterArrayW(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out count, _buffer);
        }

        return rc == 0 && count > 0;
    }

    private static string? ReadAdapterName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_VideoController");
            foreach (ManagementObject o in searcher.Get())
            {
                using (o)
                {
                    if (o["Name"] is not string name || string.IsNullOrWhiteSpace(name)) continue;
                    // The Microsoft Basic Display Adapter reports no useful utilisation.
                    if (name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)) continue;
                    return name.Trim();
                }
            }
        }
        catch (Exception ex) { Log.Debug($"video controller query: {ex.Message}"); }
        return null;
    }

    /// <summary>
    /// VRAM size. Win32_VideoController.AdapterRAM is a 32-bit field and so reports 4095MB
    /// for anything larger, which is most modern cards; the driver's own registry key
    /// carries the real 64-bit figure.
    /// </summary>
    private static long? ReadVramFromRegistry()
    {
        const string classKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(classKey);
            if (root is null) return null;

            long best = 0;
            foreach (var sub in root.GetSubKeyNames())
            {
                if (!int.TryParse(sub, out _)) continue;
                using var k = root.OpenSubKey(sub);
                if (k?.GetValue("HardwareInformation.qwMemorySize") is not { } raw) continue;
                long bytes = raw switch
                {
                    long l => l,
                    int i => i,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                    _ => 0,
                };
                best = Math.Max(best, bytes);
            }
            return best > 0 ? best / (1024 * 1024) : null;
        }
        catch (Exception ex)
        {
            Log.Debug($"VRAM size from registry: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_ready)
        {
            try { PdhCloseQuery(_query); } catch { }
            _ready = false;
        }
        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }
}
