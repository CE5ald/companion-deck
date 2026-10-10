using System.Runtime.InteropServices;

namespace CompanionDeck.Host.Telemetry;

/// <summary>
/// Direct binding to NVIDIA's management library (nvml.dll, shipped with the driver). Chosen over
/// PDH "GPU Engine" counters or spawning nvidia-smi because each query is a straight in-process
/// call costing microseconds.
/// </summary>
public sealed class Nvml : IDisposable
{
    private const string Dll = "nvml.dll";

    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization { public uint Gpu; public uint Memory; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo { public ulong Total; public ulong Free; public ulong Used; }

    [DllImport(Dll, EntryPoint = "nvmlInit_v2")] private static extern int Init();
    [DllImport(Dll, EntryPoint = "nvmlShutdown")] private static extern int Shutdown();
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] private static extern int GetHandle(uint index, out IntPtr device);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetUtilizationRates")] private static extern int GetUtil(IntPtr d, out Utilization u);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetTemperature")] private static extern int GetTemp(IntPtr d, uint sensor, out uint temp);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetMemoryInfo")] private static extern int GetMem(IntPtr d, out MemoryInfo m);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerUsage")] private static extern int GetPower(IntPtr d, out uint milliwatts);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetClockInfo")] private static extern int GetClock(IntPtr d, uint type, out uint mhz);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetName")] private static extern int GetName(IntPtr d, byte[] name, uint len);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetFanSpeed")] private static extern int GetFan(IntPtr d, out uint percent);

    private const uint TemperatureGpu = 0;
    private const uint ClockGraphics = 0;

    private IntPtr _device;
    private bool _ready;

    public string? DeviceName { get; private set; }
    public bool Available => _ready;

    public bool TryInitialize()
    {
        try
        {
            if (Init() != 0) return false;
            if (GetHandle(0, out _device) != 0) { Shutdown(); return false; }

            var buf = new byte[96];
            if (GetName(_device, buf, (uint)buf.Length) == 0)
            {
                int n = Array.IndexOf(buf, (byte)0);
                DeviceName = System.Text.Encoding.ASCII.GetString(buf, 0, n < 0 ? buf.Length : n);
            }

            _ready = true;
            Log.Info($"NVML ready: {DeviceName ?? "GPU 0"}");
            return true;
        }
        catch (DllNotFoundException)
        {
            Log.Info("NVML not present (no NVIDIA driver) - GPU metrics disabled");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"NVML init failed: {ex.Message}");
            return false;
        }
    }

    public GpuSample? Sample()
    {
        if (!_ready) return null;
        var s = new GpuSample();

        if (GetUtil(_device, out var u) == 0) { s.UtilPercent = u.Gpu; s.MemUtilPercent = u.Memory; }
        if (GetTemp(_device, TemperatureGpu, out var t) == 0) s.TempC = t;
        if (GetMem(_device, out var m) == 0)
        {
            s.VramUsedMb = (long)(m.Used / (1024 * 1024));
            s.VramTotalMb = (long)(m.Total / (1024 * 1024));
        }
        if (GetPower(_device, out var mw) == 0) s.PowerW = mw / 1000.0;
        if (GetClock(_device, ClockGraphics, out var mhz) == 0) s.ClockMhz = mhz;
        if (GetFan(_device, out var fan) == 0) s.FanPercent = fan;

        return s;
    }

    public void Dispose()
    {
        if (!_ready) return;
        try { Shutdown(); } catch { /* driver may already be gone at shutdown */ }
        _ready = false;
    }
}

public sealed class GpuSample
{
    public double? UtilPercent { get; set; }
    public double? MemUtilPercent { get; set; }
    public double? TempC { get; set; }
    public long? VramUsedMb { get; set; }
    public long? VramTotalMb { get; set; }
    public double? PowerW { get; set; }
    public double? ClockMhz { get; set; }
    public double? FanPercent { get; set; }
}
