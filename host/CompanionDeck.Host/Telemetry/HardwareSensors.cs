using System.Runtime.Versioning;
using LibreHardwareMonitor.Hardware;

namespace CompanionDeck.Host.Telemetry;

/// <summary>
/// CPU temperature, GPU temperature for AMD and Intel graphics, and every fan the machine's sensor
/// chips report, through LibreHardwareMonitor. Windows publishes none of these through a standard API:
/// the ACPI thermal zone is empty on most modern PCs and there is no fan interface at all, so the
/// sensors have to be read from the chips themselves, which needs a signed kernel driver (PawnIO) and
/// an elevated process.
///
/// It is the broad answer for "any PC": Intel and AMD CPUs, motherboard fan headers on desktops, and
/// the fans some laptops expose on a standard embedded controller. A laptop whose fans sit behind a
/// private vendor interface (Lenovo's, for instance) is covered by that vendor's own provider instead.
/// Nothing is installed by this agent: when the driver is missing, <see cref="Problem"/> says what to
/// install and the readings stay "not available".
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardwareSensors : IDisposable
{
    public sealed record Fan(string Name, int Rpm);

    public sealed class Reading
    {
        public double? CpuTempC { get; init; }
        public double? GpuTempC { get; init; }
        public IReadOnlyList<Fan> Fans { get; init; } = Array.Empty<Fan>();
    }

    private Computer? _pc;

    /// <summary>The library opened and produced at least one sensor we use.</summary>
    public bool Available { get; private set; }
    public bool HasCpuTemp { get; private set; }
    public bool HasGpuTemp { get; private set; }
    public bool HasFans { get; private set; }

    /// <summary>Why nothing is available, in words the user can act on. Empty when something is.</summary>
    public string Problem { get; private set; } = "";

    private readonly HashSet<string> _fansEverSpinning = new();

    /// <summary>
    /// <paramref name="gpuToo"/> is false when NVIDIA's own library already supplies the GPU: reading
    /// it twice costs a driver call per second for nothing.
    /// </summary>
    public void Probe(bool gpuToo)
    {
        if (!Server.Elevation.IsElevated)
        {
            Problem = "needs the elevated agent";
            Log.Info("hardware sensors skipped: the agent is not elevated");
            return;
        }

        try
        {
            _pc = new Computer
            {
                IsCpuEnabled = true,
                IsMotherboardEnabled = true,   // the super-I/O chip: fan headers and board temperatures
                IsGpuEnabled = gpuToo,
            };
            _pc.Open();

            var first = Read();
            int sensors = CountSensors();
            Log.Info($"hardware sensors: {sensors} sensor(s); cpuTemp={first.CpuTempC?.ToString("F0") ?? "-"}, " +
                     $"gpuTemp={first.GpuTempC?.ToString("F0") ?? "-"}, fans={first.Fans.Count}");

            HasCpuTemp = first.CpuTempC is not null;
            HasGpuTemp = gpuToo && first.GpuTempC is not null;
            HasFans = first.Fans.Count > 0;
            Available = HasCpuTemp || HasGpuTemp || HasFans;

            if (!Available)
            {
                Problem = PawnIoInstalled()
                    ? "no readable sensor on this PC"
                    : "needs the PawnIO driver (winget install namazso.PawnIO)";
                Log.Info($"hardware sensors: nothing readable - {Problem}");
                Close();
            }
        }
        catch (Exception ex)
        {
            Problem = "the sensor library could not start";
            Log.Warn($"hardware sensors failed to start: {ex.Message}");
            Close();
        }
    }

    private static bool PawnIoInstalled()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            return k is not null;
        }
        catch { return false; }
    }

    private int CountSensors()
    {
        int n = 0;
        if (_pc is null) return 0;
        foreach (var hw in _pc.Hardware)
        {
            n += hw.Sensors.Length;
            foreach (var sub in hw.SubHardware) n += sub.Sensors.Length;
        }
        return n;
    }

    /// <summary>Reads everything once. Call from one thread only.</summary>
    public Reading Read()
    {
        if (_pc is null) return new Reading();

        double? cpu = null, gpu = null;
        var fans = new List<Fan>();
        int fanNo = 0;

        foreach (var hw in _pc.Hardware)
        {
            try { hw.Update(); } catch { continue; }

            Visit(hw, ref cpu, ref gpu, fans, ref fanNo);
            foreach (var sub in hw.SubHardware)
            {
                try { sub.Update(); } catch { continue; }
                Visit(sub, ref cpu, ref gpu, fans, ref fanNo);
            }
        }

        return new Reading { CpuTempC = cpu, GpuTempC = gpu, Fans = fans };
    }

    private void Visit(IHardware hw, ref double? cpu, ref double? gpu, List<Fan> fans, ref int fanNo)
    {
        bool isCpu = hw.HardwareType == HardwareType.Cpu;
        bool isGpu = hw.HardwareType is HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.GpuNvidia;

        if (isCpu)
        {
            // Package temperature where there is one (Intel "CPU Package", AMD "Core (Tctl/Tdie)"), otherwise
            // the hottest core, which is what a throttle decision actually cares about.
            double? package = null, hottest = null;
            foreach (var s in hw.Sensors)
            {
                if (s.SensorType != SensorType.Temperature || s.Value is not { } v || v <= 0 || v > 125) continue;
                if (s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                    s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                    s.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase))
                    package = Math.Max(package ?? 0, v);
                hottest = Math.Max(hottest ?? 0, v);
            }
            cpu = package ?? hottest ?? cpu;
        }
        else if (isGpu)
        {
            foreach (var s in hw.Sensors)
            {
                if (s.SensorType == SensorType.Temperature && s.Value is { } v && v > 0 && v < 125 &&
                    (s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) || gpu is null))
                    gpu = v;
            }
        }

        foreach (var s in hw.Sensors)
        {
            if (s.SensorType != SensorType.Fan || s.Value is not { } rpm) continue;
            string key = hw.Identifier + "/" + s.Name;

            // A header with nothing plugged into it reads zero for ever. A fan that has spun once is a
            // real fan and keeps its tile even while it is stopped (zero-RPM idle modes exist).
            if (rpm > 0) _fansEverSpinning.Add(key);
            else if (!_fansEverSpinning.Contains(key)) continue;

            fanNo++;
            string name = isGpu ? "GPU fan" : TidyName(s.Name, fanNo);
            fans.Add(new Fan(name, (int)Math.Round(rpm)));
        }
    }

    private static string TidyName(string raw, int n) =>
        string.IsNullOrWhiteSpace(raw) ? $"Fan {n}" : raw.Replace("Fan #", "Fan ", StringComparison.OrdinalIgnoreCase);

    private void Close()
    {
        try { _pc?.Close(); } catch { }
        _pc = null;
    }

    public void Dispose() => Close();
}
