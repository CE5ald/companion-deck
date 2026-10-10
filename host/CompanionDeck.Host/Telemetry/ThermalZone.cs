using System.Management;
using System.Runtime.Versioning;

namespace CompanionDeck.Host.Telemetry;

/// <summary>
/// CPU temperature from the ACPI thermal zone, which needs no vendor interface and no kernel
/// driver. This is the universal path.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ThermalZone
{
    public bool Available { get; private set; }

    public void Probe()
    {
        Available = Read() is not null;
        Log.Info(Available
            ? "ACPI thermal zone available for CPU temperature"
            : "no ACPI thermal zone - CPU temperature unavailable without a vendor interface");
    }

    /// <summary>Hottest reported zone in Celsius, or null when none is readable.</summary>
    public double? Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");

            double best = 0;
            foreach (ManagementObject o in searcher.Get())
            {
                using (o)
                {
                    if (o["CurrentTemperature"] is not { } raw) continue;
                    // Reported in tenths of a Kelvin.
                    double c = (Convert.ToDouble(raw) / 10.0) - 273.15;
                    if (c > 0 && c < 120) best = Math.Max(best, c);
                }
            }
            return best > 0 ? Math.Round(best, 1) : null;
        }
        catch (Exception ex)
        {
            Log.Debug($"ACPI thermal zone: {ex.Message}");
            return null;
        }
    }
}
