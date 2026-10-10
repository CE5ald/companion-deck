using System.Runtime.InteropServices;

namespace CompanionDeck.Host.Audio;

/// <summary>
/// Switching the default audio endpoint from code. Windows has never shipped a public API for
/// this - the Sound control panel drives the private IPolicyConfig interface, and every tool that
/// offers "set as default device" reaches for the same thing.
/// </summary>
public static class PolicyConfig
{
    public enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();

        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);

        [PreserveSig] int SetEndpointVisibility();
    }

    /// <summary>
    /// Makes the given endpoint the default for every role, which is what a person means
    /// when they tap "headphones" - console, multimedia and communications together.
    /// </summary>
    public static bool SetDefault(string deviceId)
    {
        object? raw = null;
        try
        {
            raw = new PolicyConfigClient();
            if (raw is not IPolicyConfig cfg)
            {
                Log.Warn("IPolicyConfig unavailable on this Windows build");
                return false;
            }

            foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
            {
                int hr = cfg.SetDefaultEndpoint(deviceId, role);
                if (hr != 0)
                {
                    Log.Warn($"SetDefaultEndpoint({role}) returned 0x{hr:X8}");
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"endpoint switch failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (raw is not null && Marshal.IsComObject(raw)) Marshal.ReleaseComObject(raw);
        }
    }

    /// <summary>Probe used to decide whether to advertise the capability at all.</summary>
    public static bool IsAvailable()
    {
        object? raw = null;
        try
        {
            raw = new PolicyConfigClient();
            return raw is IPolicyConfig;
        }
        catch { return false; }
        finally
        {
            if (raw is not null && Marshal.IsComObject(raw)) Marshal.ReleaseComObject(raw);
        }
    }
}
