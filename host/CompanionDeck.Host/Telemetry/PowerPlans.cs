using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CompanionDeck.Host.Telemetry;

/// <summary>
/// Windows power mode, used as the cross-vendor stand-in for a laptop's performance profile. On a
/// a gaming laptop the thermal mode comes from its embedded controller and is the better control - it
/// moves fan curves and power limits together.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PowerPlans
{
    // Overlay GUIDs. Stable across installs; the all-zero one means "no overlay", which
    // is what Windows shows as Balanced or Recommended.
    private const string OverlayEfficiency = "961cc777-2547-4f9d-8174-7d86181b8a7a";
    private const string OverlayBalanced = "00000000-0000-0000-0000-000000000000";
    private const string OverlayPerformance = "ded574b5-45a0-0341-a416-67033d9bdc27";

    // Scheme GUIDs, for the fallback path.
    private const string SchemeSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";
    private const string SchemeBalanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string SchemeHighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string SchemeUltimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    private const string SchemeKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    private readonly HashSet<string> _schemes = new(StringComparer.OrdinalIgnoreCase);
    private bool _useOverlays;

    /// <summary>
    /// True only when all three modes the tablet offers can actually be selected.
    ///
    /// Offering a partial set would put a control on screen one of whose three buttons
    /// silently does nothing.
    /// </summary>
    public bool Available { get; private set; }

    /// <summary>Which mechanism is in play, for the log and for diagnostics.</summary>
    public string? Mechanism => !Available ? null : _useOverlays ? "overlay" : "scheme";

    public void Probe()
    {
        try
        {
            if (ProbeOverlays()) { _useOverlays = true; Available = true; }
            else if (ProbeSchemes()) { _useOverlays = false; Available = true; }

            Log.Info(Available
                ? $"Power mode control: {Mechanism}, currently {CurrentMode() ?? "unknown"}"
                : "no usable Windows power mode control - the performance card is not offered");
        }
        catch (Exception ex)
        {
            Log.Debug($"power mode probe: {ex.Message}");
        }
    }

    /// <summary>
    /// Confirms overlays work by re-applying the one already active. A no-op write is the only
    /// honest test - the GUIDs exist on paper everywhere, but a machine with no overlay support,
    /// or one where policy has locked the setting, refuses the call.
    /// </summary>
    private bool ProbeOverlays()
    {
        var current = ReadOverlayGuid();
        if (current is null) return false;
        return Run($"/overlaysetactive {current}") is not null;
    }

    private bool ProbeSchemes()
    {
        var list = Run("/list");
        if (list is null) return false;

        foreach (Match m in Regex.Matches(list, "[0-9a-fA-F-]{36}"))
            _schemes.Add(m.Value);

        bool fast = _schemes.Contains(SchemeHighPerf) || _schemes.Contains(SchemeUltimate);
        return _schemes.Contains(SchemeSaver) && _schemes.Contains(SchemeBalanced) && fast;
    }

    public bool SetMode(string mode)
    {
        if (!Available) return false;

        var target = GuidFor(mode);
        if (target is null) { Log.Warn($"no power mode for '{mode}'"); return false; }

        if (Run(_useOverlays ? $"/overlaysetactive {target}" : $"/setactive {target}") is null)
            return false;

        // Confirm from the machine rather than trusting the exit code: powercfg returns 0
        // for a change group policy then declines to apply.
        var now = CurrentMode();
        bool ok = Normalise(mode) == now;
        Log.Info($"power mode -> {mode} ({target}); reads back {now ?? "null"}");
        return ok;
    }

    public string? CurrentMode()
    {
        if (!_useOverlays) return SchemeMode();

        return ReadOverlayGuid()?.ToLowerInvariant() switch
        {
            OverlayEfficiency => "quiet",
            OverlayBalanced => "balanced",
            OverlayPerformance => "performance",
            null => null,
            // An OEM overlay the tablet has no button for. Reported so the card can say
            // what is active instead of showing nothing selected.
            _ => "custom",
        };
    }

    private string? GuidFor(string mode) => Normalise(mode) switch
    {
        "quiet" => _useOverlays ? OverlayEfficiency
                 : _schemes.Contains(SchemeSaver) ? SchemeSaver : null,
        "balanced" => _useOverlays ? OverlayBalanced
                    : _schemes.Contains(SchemeBalanced) ? SchemeBalanced : null,
        "performance" => _useOverlays ? OverlayPerformance
                       // Ultimate Performance is the better target where an OEM unhid it.
                       : _schemes.Contains(SchemeUltimate) ? SchemeUltimate
                       : _schemes.Contains(SchemeHighPerf) ? SchemeHighPerf : null,
        _ => null,
    };

    /// <summary>Accepts the aliases the tablet and any vendor path both use.</summary>
    private static string? Normalise(string mode) => mode.ToLowerInvariant() switch
    {
        "quiet" or "eco" => "quiet",
        "balanced" or "normal" => "balanced",
        "performance" or "gaming" => "performance",
        _ => null,
    };

    /// <summary>
    /// The active overlay for the power source the machine is actually on. Read from the
    /// registry because powercfg has no documented way to report it.
    /// </summary>
    private static string? ReadOverlayGuid()
    {
        bool onBattery = GetSystemPowerStatus(out var st) && st.ACLineStatus == 0;
        var name = onBattery ? "ActiveOverlayDcPowerScheme" : "ActiveOverlayAcPowerScheme";
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(SchemeKey);
            return k?.GetValue(name) as string;
        }
        catch (Exception ex)
        {
            Log.Debug($"read {name}: {ex.Message}");
            return null;
        }
    }

    private string? SchemeMode()
    {
        var text = Run("/getactivescheme");
        if (text is null) return null;

        var m = Regex.Match(text, "[0-9a-fA-F-]{36}");
        if (!m.Success) return null;

        return m.Value.ToLowerInvariant() switch
        {
            SchemeSaver => "quiet",
            SchemeBalanced => "balanced",
            SchemeHighPerf or SchemeUltimate => "performance",
            _ => "custom",
        };
    }

    private static string? Run(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg.exe", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;

            var stdout = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(4000)) return null;
            return p.ExitCode == 0 ? stdout : null;
        }
        catch (Exception ex)
        {
            Log.Debug($"powercfg {args}: {ex.Message}");
            return null;
        }
    }
}
