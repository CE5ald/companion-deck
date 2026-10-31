using System.Runtime.InteropServices;

namespace CompanionDeck.Host.Monitoring;

/// <summary>
/// How hard each process is working the GPU, from the same performance counters Task Manager reads.
/// Windows publishes one instance per process per engine ("pid_1234_luid_..._engtype_3D"); a process's
/// figure is its busiest engine, as Task Manager shows it. Opened only when someone asks, because
/// nothing else needs per-process numbers.
/// </summary>
public sealed class GpuProcessCounters : IDisposable
{
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue { public int CStatus; private readonly int _pad; public double Double; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem { public IntPtr Name; public CounterValue Value; }

    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtNoCap100 = 0x00008000;
    private const uint PdhMoreData = 0x800007D2;

    private IntPtr _query, _counter, _buffer;
    private uint _bufferSize;

    public bool Available { get; private set; }

    public bool TryOpen()
    {
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) return false;
            if (PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter) != 0)
            {
                PdhCloseQuery(_query);
                return false;
            }
            PdhCollectQueryData(_query);
            Available = true;
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"per-process GPU counters unavailable: {ex.Message}");
            return false;
        }
    }

    /// <summary>Process id to its busiest engine's utilisation, for processes using the GPU at all.</summary>
    public unsafe Dictionary<int, double> Read()
    {
        var result = new Dictionary<int, double>();
        if (!Available || PdhCollectQueryData(_query) != 0) return result;

        uint size = _bufferSize, count = 0;
        uint rc = _buffer == IntPtr.Zero
            ? PdhMoreData
            : PdhGetFormattedCounterArrayW(_counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out count, _buffer);
        if (rc == PdhMoreData)
        {
            size = 0;
            PdhGetFormattedCounterArrayW(_counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out count, IntPtr.Zero);
            if (size == 0) return result;
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = Marshal.AllocHGlobal((int)size);
            _bufferSize = size;
            rc = PdhGetFormattedCounterArrayW(_counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out count, _buffer);
        }
        if (rc != 0 || count == 0) return result;

        var perEngine = new Dictionary<(int pid, string type), double>();
        var items = (CounterItem*)_buffer;
        for (uint i = 0; i < count; i++)
        {
            ref var item = ref items[i];
            if (item.Value.CStatus != 0 || item.Value.Double <= 0) continue;
            string name = Marshal.PtrToStringUni(item.Name) ?? "";
            if (!TryParse(name, out int pid, out string type)) continue;
            perEngine[(pid, type)] = perEngine.GetValueOrDefault((pid, type)) + item.Value.Double;
        }
        foreach (var ((pid, _), v) in perEngine)
            result[pid] = Math.Max(result.GetValueOrDefault(pid), Math.Min(100, v));
        return result;
    }

    /// <summary>"pid_1234_luid_0x0_0x1_phys_0_eng_0_engtype_3D" gives (1234, "3D").</summary>
    public static bool TryParse(string name, out int pid, out string engineType)
    {
        pid = 0; engineType = "";
        if (!name.StartsWith("pid_", StringComparison.Ordinal)) return false;
        int end = name.IndexOf('_', 4);
        if (end < 0 || !int.TryParse(name.AsSpan(4, end - 4), out pid)) return false;
        int t = name.LastIndexOf("engtype_", StringComparison.Ordinal);
        engineType = t >= 0 ? name[(t + 8)..] : "";
        return true;
    }

    public void Dispose()
    {
        if (Available) { try { PdhCloseQuery(_query); } catch { } Available = false; }
        if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; }
    }
}
