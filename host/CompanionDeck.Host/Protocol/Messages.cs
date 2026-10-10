using System.Text.Json;
using System.Text.Json.Serialization;

namespace CompanionDeck.Host.Protocol;

// ---------------------------------------------------------------------------
// Tablet -> Host
// ---------------------------------------------------------------------------

public sealed class ClientHello
{
    public string Type { get; set; } = "hello";
    public string? Device { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? Version { get; set; }
    /// <summary>Asset keys the tablet already has cached, so we never resend artwork.</summary>
    public List<string>? CachedAssets { get; set; }
}

/// <summary>
/// The heart of the "no overhead when nobody's looking" design. The tablet tells us which
/// sections are actually on screen and how fast it wants them. An empty section list means
/// the tablet is asleep or backgrounded and the host stops collecting entirely.
/// </summary>
public sealed class Subscribe
{
    public string Type { get; set; } = "sub";
    public List<string> Sections { get; set; } = new();
    /// <summary>Desired refresh rate for volatile sections. Clamped host-side.</summary>
    public double Hz { get; set; } = 2;
}

public sealed class Command
{
    public string Type { get; set; } = "cmd";
    public long Id { get; set; }
    public string Action { get; set; } = "";
    public JsonElement? Args { get; set; }
}

public sealed class AssetRequest
{
    public string Type { get; set; } = "asset";
    public string Key { get; set; } = "";
}

// ---------------------------------------------------------------------------
// Host -> Tablet
// ---------------------------------------------------------------------------

public sealed class HostHello
{
    public string Type { get; set; } = "hello";
    public string HostName { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>
    /// Advertised so the tablet hides controls we genuinely cannot drive, instead of
    /// showing dead buttons. Nothing here is claimed unless it was probed successfully.
    /// </summary>
    public Capabilities Capabilities { get; set; } = new();
}

public sealed class Capabilities
{
    public bool Elevated { get; set; }
    public bool Brightness { get; set; }
    public bool Fps { get; set; }
    public bool CpuTemp { get; set; }
    /// <summary>Any GPU readings at all: load, and usually VRAM.</summary>
    public bool Gpu { get; set; }
    /// <summary>Temperature, power draw and clocks. Needs NVML; Windows does not publish them.</summary>
    public bool GpuDetail { get; set; }
    public bool GpuTemp { get; set; }
    public bool Fans { get; set; }
    public bool ThermalMode { get; set; }
    /// <summary>"vendor" for a laptop EC, "powerplan" for Windows power schemes.</summary>
    public string? ThermalModeKind { get; set; }
    /// <summary>Charge mode is readable. Deliberately not paired with a write capability:
    /// the firmware has no setter, so the tablet must render it as status, not a control.</summary>
    public bool AudioDeviceSwitch { get; set; }
    /// <summary>A virtual cable is installed, so desktop audio can be sent to the mic input.</summary>
    public bool MicCast { get; set; }
    public bool MediaSeek { get; set; }
    public string Transport { get; set; } = "";
}

public sealed class CommandResult
{
    public string Type { get; set; } = "result";
    public long Id { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
}


public sealed class SysState
{
    public string Type { get; set; } = "sys";

    public double Cpu { get; set; }
    public double? CpuTemp { get; set; }
    public double? CpuClockMhz { get; set; }

    public double RamPercent { get; set; }
    public long RamUsedMb { get; set; }
    public long RamTotalMb { get; set; }

    public double? Gpu { get; set; }
    public double? GpuTemp { get; set; }
    public long? VramUsedMb { get; set; }
    public long? VramTotalMb { get; set; }
    public double? GpuPowerW { get; set; }
    public double? GpuClockMhz { get; set; }

    public double? DiskPercent { get; set; }
    public double? NetDownKbps { get; set; }
    public double? NetUpKbps { get; set; }

    public int? Battery { get; set; }
    public bool? Charging { get; set; }

    public double? Fps { get; set; }
    public double? Fps1Low { get; set; }

    public int? Fan1Rpm { get; set; }
    public int? Fan2Rpm { get; set; }
    /// <summary>Rated ceiling, so the tablet can express a fan speed as a fraction.</summary>
    public int? FanMaxRpm { get; set; }
    public string? ThermalMode { get; set; }
    public int? Brightness { get; set; }

}

public sealed class MediaSessionInfo
{
    public string Id { get; set; } = "";
    public string? AppName { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    /// <summary>Content-addressed asset key; the bytes travel once and are cached tablet-side.</summary>
    public string? ArtKey { get; set; }
    public string Status { get; set; } = "stopped";
    public double? PositionSec { get; set; }
    public double? DurationSec { get; set; }
    public bool CanPlay { get; set; }
    public bool CanPause { get; set; }
    public bool CanNext { get; set; }
    public bool CanPrevious { get; set; }
    public bool CanSeek { get; set; }
    public bool? Shuffle { get; set; }
    public string? Repeat { get; set; }

    /// <summary>
    /// This application's own mixer level, not the Windows master. Null when the media
    /// session could not be paired with an audio session, in which case the tablet hides
    /// the slider rather than showing one that would silently move the wrong thing.
    /// </summary>
    public int? AppVolume { get; set; }
    public bool? AppMuted { get; set; }
}

public sealed class MediaState
{
    public string Type { get; set; } = "media";
    public List<MediaSessionInfo> Sessions { get; set; } = new();
    public string? ActiveId { get; set; }
}

public sealed class CastState
{
    public string Type { get; set; } = "cast";
    public bool Available { get; set; }
    public bool Armed { get; set; }
    /// <summary>The real microphone being mixed in, resolved when the cast was armed.</summary>
    public string? MicName { get; set; }
    public bool MicEnabled { get; set; }
    public int DesktopGain { get; set; }
    public int MicGain { get; set; }
    /// <summary>Peak levels, 0-100, so the tablet can show the cast is actually carrying audio.</summary>
    public int DesktopPeak { get; set; }
    public int MicPeak { get; set; }
    public string? Error { get; set; }

    /// <summary>Which output is tapped and which microphone is mixed. The cable is excluded
    /// from both lists: choosing it could only ever produce a feedback loop.</summary>
    public List<AudioDeviceInfo> Outputs { get; set; } = new();
    public List<AudioDeviceInfo> Inputs { get; set; } = new();
}

public sealed class SoundboardState
{
    public string Type { get; set; } = "soundboard";
    public List<SoundClipInfo> Items { get; set; } = new();
    /// <summary>Shown on the tablet so it is obvious where to drop files on the laptop.</summary>
    public string Folder { get; set; } = "";

    public bool RecorderArmed { get; set; }
    public bool Recording { get; set; }
    /// <summary>Whether clips are level-matched to each other on playback.</summary>
    public bool Normalise { get; set; }
    public int BufferedSeconds { get; set; }
    public int BufferCapacity { get; set; }
    public int RecordingSeconds { get; set; }
    public string? LastSaved { get; set; }
    public string? Error { get; set; }
}

public sealed class SoundClipInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>both | mic | ears</summary>
    public string Route { get; set; } = "both";
    public int Gain { get; set; } = 100;
    public int Order { get; set; }
    public double Seconds { get; set; }
    public bool Playing { get; set; }
    /// <summary>Set when the file could not be decoded, so the pad can say so.</summary>
    public bool Broken { get; set; }
    /// <summary>Decoded and found to contain no audible signal - almost always a capture
    /// made while nothing was playing. Shown so a silent pad explains itself.</summary>
    public bool Silent { get; set; }
}

public sealed class RunningAppsState
{
    public string Type { get; set; } = "running";
    public List<RunningAppInfo> Items { get; set; } = new();
}

public sealed class RunningAppInfo
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Window title, when it has a window. Null for background entries.</summary>
    public string? Title { get; set; }
    public long MemoryMb { get; set; }
    /// <summary>True when this is a taskbar app; false when it is a background process.</summary>
    public bool Windowed { get; set; }
}

public sealed class AudioDeviceInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
}

public sealed class AudioAppInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? IconKey { get; set; }
    public int Volume { get; set; }
    public bool Muted { get; set; }
    /// <summary>True when the session is currently producing sound, used to de-clutter the mixer.</summary>
    public bool Active { get; set; }
}

public sealed class AudioState
{
    public string Type { get; set; } = "audio";
    public int MasterVolume { get; set; }
    public bool MasterMuted { get; set; }
    public int MicVolume { get; set; }
    public bool MicMuted { get; set; }
    public List<AudioDeviceInfo> Outputs { get; set; } = new();
    public List<AudioDeviceInfo> Inputs { get; set; } = new();
    public List<AudioAppInfo> Apps { get; set; } = new();
}

public sealed class ContextState
{
    public string Type { get; set; } = "ctx";
    /// <summary>desktop | gaming | music | video</summary>
    public string Mode { get; set; } = "desktop";
    public string? ForegroundApp { get; set; }
    public string? WindowTitle { get; set; }
    public bool Fullscreen { get; set; }
}

public sealed class LaunchTarget
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? IconKey { get; set; }
    /// <summary>game | app</summary>
    public string Kind { get; set; } = "app";
    /// <summary>steam | epic | xbox | shortcut | uwp</summary>
    public string Source { get; set; } = "shortcut";
    public bool Running { get; set; }

    /// <summary>Pinned by the user; sorts above everything else.</summary>
    public bool Fav { get; set; }

    /// <summary>Hidden by the user. Still sent, so the edit mode can offer to restore it.</summary>
    public bool Hidden { get; set; }
}

public sealed class AppsState
{
    public string Type { get; set; } = "apps";
    public List<LaunchTarget> Items { get; set; } = new();
}

/// <summary>
/// Somewhere media can be started from. Only the id crosses the link; the executable or
/// address behind it stays host-side, exactly as it does for <see cref="LaunchTarget"/>.
/// </summary>
public sealed class MediaSourceInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>app | web | folder - the tablet uses this only to pick a fallback glyph.</summary>
    public string Kind { get; set; } = "app";
    public string? IconKey { get; set; }
    public bool Running { get; set; }
}

public sealed class MediaSourcesState
{
    public string Type { get; set; } = "mediaSources";
    public List<MediaSourceInfo> Items { get; set; } = new();
}


public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8) => JsonSerializer.Deserialize<T>(utf8, Options);
}
