using System.Management;
using System.Runtime.Versioning;

namespace CompanionDeck.Host.Telemetry;

/// <summary>
/// Internal display brightness through WMI. An earlier attempt at this project concluded the
/// laptop exposed no brightness control. It does - WmiMonitorBrightness reports 101 levels here.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Brightness
{
    private const string Scope = @"root\wmi";

    public bool CanRead { get; private set; }
    public bool CanWrite { get; private set; }

    public void Probe()
    {
        CanRead = Read() is not null;

        if (!CanRead) { CanWrite = false; return; }

        // The only honest test of write access is a write. Setting brightness to its
        // current value is a no-op the user cannot perceive.
        var current = Read();
        CanWrite = current is not null && Set(current.Value, probing: true);

        Log.Info($"Brightness: read={CanRead}, write={CanWrite}" +
                 (CanRead && !CanWrite ? " (needs elevation)" : ""));
    }

    public int? Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject o in searcher.Get())
            {
                using (o)
                {
                    var v = o["CurrentBrightness"];
                    if (v is not null) return Convert.ToInt32(v);
                }
            }
        }
        catch (Exception ex) { Log.Debug($"brightness read: {ex.Message}"); }
        return null;
    }

    public bool Set(int percent, bool probing = false)
    {
        percent = Math.Clamp(percent, 0, 100);
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject o in searcher.Get())
            {
                using (o)
                {
                    // Timeout 0: apply immediately and do not revert.
                    o.InvokeMethod("WmiSetBrightness", new object[] { (uint)0, (byte)percent });
                    return true;
                }
            }
        }
        catch (ManagementException ex)
        {
            if (!probing) Log.Warn($"brightness set failed: {ex.Message}");
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            if (!probing) Log.Warn("brightness set denied - the agent is not elevated");
            return false;
        }
        catch (Exception ex)
        {
            if (!probing) Log.Warn($"brightness set failed: {ex.Message}");
            return false;
        }
        return false;
    }
}
