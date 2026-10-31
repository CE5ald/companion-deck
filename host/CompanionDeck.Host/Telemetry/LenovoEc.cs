using System.Management;
using System.Runtime.Versioning;

namespace CompanionDeck.Host.Telemetry;

/// <summary>
/// Lenovo Legion embedded-controller data: CPU and GPU temperature, fan speeds and the thermal
/// (performance) mode. This is the reason the agent wants elevation.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LenovoEc
{
    private const string Scope = @"root\wmi";
    private const string ClassName = "LENOVO_GAMEZONE_DATA";

    public bool Available { get; private set; }

    /// <summary>"Legion" on a Legion, "Lenovo" on any other Lenovo with this interface; null elsewhere.</summary>
    public string? Brand { get; private set; }
    public bool HasFans { get; private set; }
    public bool HasCpuTemp { get; private set; }
    public bool HasThermalMode { get; private set; }
    public bool HasChargeMode { get; private set; }
    public bool HasKeyboardLight { get; private set; }

    /// <summary>
    /// Ceiling the fans are rated to, read once at probe time. Without it an RPM is an
    /// uncalibrated number: 2000 means nothing until you know whether the fan tops out at 3400 or
    /// 6000.
    /// </summary>
    public int? FanMaxRpm { get; private set; }

    /// <summary>True when the interface exists but refused us for lack of privilege.</summary>
    public bool NeedsElevation { get; private set; }

    /// <summary>
    /// The interface is Lenovo's, so nothing is asked of it on any other make of PC. The WMI class
    /// would not exist there anyway; checking the maker first means a stranger's machine is never
    /// even sent a query meant for different firmware.
    /// </summary>
    private static string? ReadBrand()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model, SystemFamily FROM Win32_ComputerSystem");
            foreach (ManagementObject o in searcher.Get())
            {
                var text = $"{o["Manufacturer"]} {o["Model"]} {o["SystemFamily"]}";
                if (!text.Contains("LENOVO", StringComparison.OrdinalIgnoreCase)) return null;
                return text.Contains("Legion", StringComparison.OrdinalIgnoreCase) ? "Legion" : "Lenovo";
            }
        }
        catch (Exception ex) { Log.Debug($"computer system query: {ex.Message}"); }
        return null;
    }

    public void Probe()
    {
        try
        {
            Brand = ReadBrand();
            if (Brand is null)
            {
                Log.Info("vendor interface skipped: not a Lenovo");
                return;
            }

            using var instance = GetInstance(out var denied);
            if (instance is null)
            {
                // Distinguishing these two matters: one is "this laptop cannot do it",
                // the other is "run the installer". Reporting both as absent sends
                // someone hunting for a hardware problem that does not exist.
                NeedsElevation = denied;
                Log.Info(denied
                    ? "Lenovo EC present but access denied - run scripts\\Install-Elevated.ps1 to enable CPU temp and fans"
                    : "Lenovo GameZone interface not present on this machine");
                return;
            }

            Available = true;
            Log.Info($"Lenovo firmware interface present ({Brand})");

            // Log the raw readings once. Vendor EC methods return sentinels rather than
            // errors for idle or unsupported sensors, so seeing the actual numbers is the
            // only way to set sane validity ranges.
            var rawCpu = TryInvoke(instance, "GetCPUTemp", null);
            var rawF1 = TryInvoke(instance, "GetFan1Speed", null);
            var rawF2 = TryInvoke(instance, "GetFan2Speed", null);
            var rawMode = TryInvoke(instance, "GetThermalMode", null);
            Log.Info($"Lenovo EC raw: cpuTemp={rawCpu?.ToString() ?? "null"}, " +
                     $"fan1={rawF1?.ToString() ?? "null"}, fan2={rawF2?.ToString() ?? "null"}, " +
                     $"thermalMode={rawMode?.ToString() ?? "null"}");

            // GetCPUTemp returns 0 on this AMD Legion - the GameZone temperature methods
            // appear to be wired up only on the Intel variants. Fall back to the standard
            // ACPI thermal zone, which needs no vendor interface and no driver.
            HasCpuTemp = ReadGameZoneCpuTemp(instance) is not null;
            // This model exposes GetFan1Speed/GetFan2Speed with no arguments, not the
            // indexed GetFanSpeed(FanID) that some other Legion firmware uses.
            HasFans = TryInvoke(instance, "GetFan1Speed", null) is not null;
            if (!HasFans && ReadFanViaFanMethod(1) is not null) { _fanMethod = true; HasFans = true; }
            HasThermalMode = TryInvoke(instance, "GetThermalMode", null) is not null;

            // Rated ceiling, not a live reading, so it is read once and cached. Guarded
            // against the sentinel 0 some firmware returns for "not wired up", which would
            // otherwise make every fan look pinned at 100%.
            var maxRpm = TryInvoke(instance, "GetFanMaxSpeed", null);
            FanMaxRpm = maxRpm is > 0 and < 20000 ? (int)maxRpm : null;

            // Read-only. This firmware exposes GetPowerChargeMode with no matching setter -
            // there is no SetPowerChargeMode, and LENOVO_SUPERKEY_DATA returned no settable
            // entries when probed - so the charge mode can be reported but not changed
            // without writing EC registers behind the firmware's back.
            HasChargeMode = TryInvoke(instance, "GetPowerChargeMode", null) is not null;

            // IsSupportLightingFeature is a bitmask describing which KIND of lighting exists, and
            // only the low bits are the white backlight that SetKeyboardLight drives. This
            // machine reports 4 - RGB - and accordingly accepts SetKeyboardLight, returns
            // success, and does nothing: the read-back stays at 0 even after claiming lighting
            // ownership from Vantage.
            var lightMask = TryInvoke(instance, "IsSupportLightingFeature", null) ?? 0;
            HasKeyboardLight = (lightMask & 0x3) != 0;
            if (lightMask != 0 && !HasKeyboardLight)
                Log.Info($"keyboard lighting present but not the white backlight (mask {lightMask}) - " +
                         "no WMI control, so it is not offered");

            Log.Info($"Lenovo EC: cpuTemp={HasCpuTemp}, fans={HasFans}, thermalMode={HasThermalMode}, " +
                     $"fanMax={FanMaxRpm?.ToString() ?? "?"}, chargeMode={HasChargeMode}, kbdLight={HasKeyboardLight}");
        }
        catch (UnauthorizedAccessException)
        {
            Log.Info("Lenovo EC present but denied - the agent is not elevated");
        }
        catch (Exception ex)
        {
            Log.Debug($"Lenovo EC probe: {ex.Message}");
        }
    }

    // Newer Legion firmware reports fan speed through a separate class rather than GameZone. Read-only, and
    // written from the interface's published shape; it has NOT been run against a machine that needs it
    // (the Legion this was built on answers the GameZone calls), so it can only ever add a fan reading,
    // never take one away.
    private bool _fanMethod;

    private static uint? ReadFanViaFanMethod(int fanId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(OpenScope(), new ObjectQuery("SELECT * FROM LENOVO_FAN_METHOD"));
            foreach (ManagementObject o in searcher.Get())
            {
                using var p = o.GetMethodParameters("Fan_Get_CurrentFanSpeed");
                p["FanID"] = (uint)fanId;
                using var r = o.InvokeMethod("Fan_Get_CurrentFanSpeed", p, null);
                var v = r?["CurrentFanSpeed"];
                if (v is not null && Convert.ToUInt32(v) is > 0 and < 10000 and var rpm) return rpm;
                return null;
            }
        }
        catch (Exception ex) { Log.Debug($"LENOVO_FAN_METHOD fan {fanId}: {ex.Message}"); }
        return null;
    }

    /// <summary>
    /// Opens root\wmi with impersonation and privileges enabled. Being elevated is not by itself
    /// sufficient here: the WMI client has to ask for the caller's privileges to be enabled on
    /// the connection, otherwise vendor classes that gate on them still answer Access Denied to
    /// an administrator.
    /// </summary>
    private static ManagementScope OpenScope()
    {
        var scope = new ManagementScope(@"\\.\" + Scope, new ConnectionOptions
        {
            Impersonation = ImpersonationLevel.Impersonate,
            Authentication = AuthenticationLevel.PacketPrivacy,
            EnablePrivileges = true,
        });
        scope.Connect();
        return scope;
    }

    private static ManagementObject? GetInstance(out bool denied)
    {
        denied = false;
        try
        {
            var scope = OpenScope();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM {ClassName}"));
            foreach (ManagementObject o in searcher.Get()) return o;   // single instance

            Log.Debug($"{ClassName}: query succeeded but returned no instances");
        }
        catch (ManagementException ex)
        {
            denied = ex.ErrorCode == ManagementStatus.AccessDenied;
            Log.Debug($"{ClassName} query: {ex.ErrorCode} - {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            denied = true;
            Log.Debug($"{ClassName} query denied: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Debug($"{ClassName} query failed: {ex.GetType().Name}: {ex.Message}");
        }
        return null;
    }

    private static ManagementObject? GetInstance() => GetInstance(out _);

    private static double? ReadGameZoneCpuTemp(ManagementObject instance)
    {
        var v = TryInvoke(instance, "GetCPUTemp", null);
        return v is > 0 and < 120 ? v : null;
    }

    private static uint? TryInvoke(ManagementObject instance, string method, Dictionary<string, object>? args)
    {
        try
        {
            ManagementBaseObject? inParams = null;
            if (args is not null)
            {
                inParams = instance.GetMethodParameters(method);
                foreach (var (k, v) in args) inParams[k] = v;
            }

            using var result = instance.InvokeMethod(method, inParams, null);
            var data = result?["Data"];
            return data is null ? null : Convert.ToUInt32(data);
        }
        catch (Exception ex)
        {
            Log.Debug($"{method}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Switches the performance profile - the same thing the keyboard's Fn+Q cycles through.
    /// Separate from <see cref="TryInvoke"/> because that reads the method's "Data" out parameter
    /// to decide whether the call worked, and a setter has no out parameter: it would report
    /// failure on every successful call.
    /// </summary>
    public bool SetThermalMode(string mode)
    {
        if (!Available || !HasThermalMode) return false;

        uint? value = mode.ToLowerInvariant() switch
        {
            "quiet" or "eco" => 1u,
            "balanced" or "normal" => 2u,
            "performance" or "gaming" => 3u,
            _ => null,
        };
        if (value is null) { Log.Warn($"unknown thermal mode '{mode}'"); return false; }

        try
        {
            using var instance = GetInstance();
            if (instance is null) return false;

            using var inParams = instance.GetMethodParameters("SetSmartFanMode");
            inParams["Data"] = value.Value;
            using var _ = instance.InvokeMethod("SetSmartFanMode", inParams, null);

            // The call succeeds on firmware that then ignores it, which is exactly what a different Legion
            // generation can do. Ask the firmware what it is in now, and only say yes if it agrees.
            Thread.Sleep(200);
            var now = CurrentThermalMode();
            string want = value.Value switch { 1 => "quiet", 2 => "balanced", _ => "performance" };
            Log.Info($"thermal mode -> {mode} ({value}); firmware now reports {now ?? "nothing"}");
            if (now is not null && now != want)
            {
                Log.Warn("the firmware accepted the call but did not change mode; this model may need a different interface");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            // Unelevated this is Access Denied; the agent runs elevated, so anything else
            // here means the firmware refused the value.
            Log.Warn($"SetSmartFanMode({value}) failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Reads back the live mode, so a switch can be confirmed rather than assumed.</summary>
    public string? CurrentThermalMode()
    {
        if (!Available || !HasThermalMode) return null;
        try
        {
            using var instance = GetInstance();
            if (instance is null) return null;
            return TryInvoke(instance, "GetThermalMode", null) switch
            {
                1 => "quiet", 2 => "balanced", 3 => "performance", 255 => "custom", _ => null,
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// Sets the keyboard backlight level: 0 off, 1 dim, 2 bright. This is the white backlight,
    /// which is all LENOVO_GAMEZONE_DATA reaches.
    /// </summary>
    public bool SetKeyboardLight(int level)
    {
        if (!Available || !HasKeyboardLight) return false;
        if (level is < 0 or > 2) { Log.Warn($"keyboard light level {level} out of range"); return false; }

        try
        {
            using var instance = GetInstance();
            if (instance is null) return false;

            // Lighting is owned by Vantage until something claims it. Without this the
            // firmware accepts SetKeyboardLight and does nothing - the call returns success
            // and the read-back never moves, which is exactly what this machine did.
            TryOwnLighting(instance, true);

            using var inParams = instance.GetMethodParameters("SetKeyboardLight");
            inParams["Data"] = (uint)level;
            using var _ = instance.InvokeMethod("SetKeyboardLight", inParams, null);

            var after = TryInvoke(instance, "GetKeyboardLight", null);
            Log.Info($"keyboard backlight -> {level} (reads back {after?.ToString() ?? "null"})");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"SetKeyboardLight({level}) failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Claims (or releases) the lighting control channel from Lenovo Vantage. Best effort by
    /// design: a laptop whose lighting is the 4-zone RGB controller rather than a plain white
    /// backlight will accept this and still ignore SetKeyboardLight, because that lighting is
    /// driven over vendor HID reports and has no WMI surface.
    /// </summary>
    private static void TryOwnLighting(ManagementObject instance, bool own)
    {
        try
        {
            using var p = instance.GetMethodParameters("SetLightControlOwner");
            p["Data"] = own ? 1u : 0u;
            using var _ = instance.InvokeMethod("SetLightControlOwner", p, null);
        }
        catch (Exception ex)
        {
            Log.Debug($"SetLightControlOwner({own}): {ex.Message}");
        }
    }

    // The firmware hands back a bare integer with no published mapping, and this laptop
    // reports 1 while charging normally. The labels below are provisional; the raw value is
    // logged whenever it changes so the mapping can be confirmed against Vantage rather
    // than guessed at permanently.
    private uint? _lastChargeRaw;

    private string? ChargeLabel(uint? raw)
    {
        if (raw is null) return null;
        if (raw != _lastChargeRaw)
        {
            Log.Info($"power charge mode raw = {raw}");
            _lastChargeRaw = raw;
        }
        return raw switch
        {
            1 => "normal",
            2 => "rapid",
            3 => "conservation",
            _ => null,
        };
    }

    public sealed class Reading
    {
        public double? CpuTempC { get; init; }
        public int? Fan1Rpm { get; init; }
        public int? Fan2Rpm { get; init; }
        public string? ThermalMode { get; init; }
        public string? ChargeMode { get; init; }
        public int? KeyboardLight { get; init; }
    }

    public Reading? Sample()
    {
        if (!Available) return null;

        try
        {
            using var instance = GetInstance();
            if (instance is null) return null;

            double? cpu = HasCpuTemp ? ReadGameZoneCpuTemp(instance) : null;
            uint? f1 = HasFans ? (_fanMethod ? ReadFanViaFanMethod(1) : TryInvoke(instance, "GetFan1Speed", null)) : null;
            uint? f2 = HasFans ? (_fanMethod ? ReadFanViaFanMethod(2) : TryInvoke(instance, "GetFan2Speed", null)) : null;
            uint? mode = HasThermalMode ? TryInvoke(instance, "GetThermalMode", null) : null;
            uint? charge = HasChargeMode ? TryInvoke(instance, "GetPowerChargeMode", null) : null;
            uint? kbd = HasKeyboardLight ? TryInvoke(instance, "GetKeyboardLight", null) : null;

            return new Reading
            {
                ChargeMode = ChargeLabel(charge),
                KeyboardLight = kbd is <= 2 ? (int?)kbd : null,
                CpuTempC = cpu,
                Fan1Rpm = f1 is > 0 and < 10000 ? (int)f1 : null,
                Fan2Rpm = f2 is > 0 and < 10000 ? (int)f2 : null,
                ThermalMode = mode switch
                {
                    1 => "quiet",
                    2 => "balanced",
                    3 => "performance",
                    255 => "custom",
                    _ => null,
                },
            };
        }
        catch (Exception ex)
        {
            Log.Debug($"Lenovo EC sample: {ex.Message}");
            return null;
        }
    }
}
