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
    /// <summary>Random per connection. The PC's proof is made over it, so it cannot be replayed.</summary>
    public string? Nonce { get; set; }
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

    /// <summary>The PC's half of the network handshake: its own nonce, and proof it holds the key.</summary>
    public string? Nonce { get; set; }
    public string? Proof { get; set; }
    /// <summary>"required" on the network, where the tablet must answer before anything is served.</summary>
    public string? Auth { get; set; }
    /// <summary>The key itself, sent only over the cable. See <see cref="Transport.Pairing"/>.</summary>
    public string? Pair { get; set; }
}

public sealed class Capabilities
{
    public bool Elevated { get; set; }
    public bool Brightness { get; set; }
    public bool Fps { get; set; }
    /// <summary>The overlay and capture shortcuts whose software is installed here.</summary>
    public List<string>? GameShortcuts { get; set; }
    /// <summary>Why frame rate is unavailable (for example it needs the elevated agent). Null when it works.</summary>
    public string? FpsNote { get; set; }
    /// <summary>Why CPU temperature or fans are not available and what would fix it. Null when they work.</summary>
    public string? SensorsNote { get; set; }
    /// <summary>The laptop brand whose firmware profile the performance control drives ("Legion", "Lenovo"), or null for Windows' own.</summary>
    public string? VendorName { get; set; }
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
    public bool ChargeMode { get; set; }
    public bool KeyboardLight { get; set; }
    public bool AudioDeviceSwitch { get; set; }
    /// <summary>A virtual cable is installed, so desktop audio can be sent to the mic input.</summary>
    public bool MicCast { get; set; }
    public bool MediaSeek { get; set; }
    public string Transport { get; set; } = "";
}

/// <summary>Sent once the tablet has proved it holds the key; everything is served from here.</summary>
public sealed class AuthResult
{
    public string Type { get; set; } = "authed";
}

public sealed class CommandResult
{
    public string Type { get; set; } = "result";
    public long Id { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
}


public sealed class FanInfo
{
    public string Name { get; set; } = "";
    public int Rpm { get; set; }
    /// <summary>Rated ceiling when the vendor reports one, so the tablet can show a fraction.</summary>
    public int? MaxRpm { get; set; }
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

    /// <summary>"power", "heat" or "both" while the GPU is being held back; null otherwise.</summary>
    public string? GpuThrottle { get; set; }
    /// <summary>"heat", "power" or "battery" while the CPU runs far below its clock under load.</summary>
    public string? CpuThrottle { get; set; }

    /// <summary>Every fan this PC reports, however many: two on a gaming laptop, none on a fanless one,
    /// six on a desktop. Empty when there is no readable fan.</summary>
    public List<FanInfo> Fans { get; set; } = new();
    public int? Fan1Rpm { get; set; }
    public int? Fan2Rpm { get; set; }
    /// <summary>Rated ceiling, so the tablet can express a fan speed as a fraction.</summary>
    public int? FanMaxRpm { get; set; }
    public string? ThermalMode { get; set; }
    public int? Brightness { get; set; }

    /// <summary>Read-only: this firmware exposes no setter for the charge mode.</summary>
    public string? ChargeMode { get; set; }
    /// <summary>White keyboard backlight level, 0-2. Not the RGB/Spectrum profile.</summary>
    public int? KeyboardLight { get; set; }
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
    /// <summary>The folder it is filed under: a sub-folder of the soundboard folder, or one chosen on the tablet.</summary>
    public string Group { get; set; } = "";
    /// <summary>A palette name, or empty for the default.</summary>
    public string Color { get; set; } = "";
    /// <summary>Plays over and over until pressed again.</summary>
    public bool Loop { get; set; }
}

/// <summary>Bars for the visualiser, 0 to 100 each, low frequencies first.</summary>
public sealed class SpectrumState
{
    public string Type { get; set; } = "spectrum";
    public int[] Bands { get; set; } = Array.Empty<int>();
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
    /// <summary>This endpoint's own level. Only filled for output devices.</summary>
    public int? Volume { get; set; }
    public bool? Muted { get; set; }
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
    /// <summary>Its level is kept and put back each time the app starts.</summary>
    public bool Remembered { get; set; }
}

public sealed class AudioState
{
    public string Type { get; set; } = "audio";
    public int MasterVolume { get; set; }
    public bool MasterMuted { get; set; }
    public int MicVolume { get; set; }
    public bool MicMuted { get; set; }
    /// <summary>Speakers and microphone are both muted. Derived, so it cannot drift from the real state.</summary>
    public bool Deafened { get; set; }
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

    /// <summary>Whole minutes with the game in front; null for something never played.</summary>
    public int? PlayMinutes { get; set; }
    public long? LastPlayedMs { get; set; }
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
    /// <summary>For a web service, which one (spotify, ytmusic). The tablet draws that identity itself.</summary>
    public string? Service { get; set; }
    /// <summary>The browser that opens it, named and with its icon. A badge, never the identity.</summary>
    public string? Via { get; set; }
    public string? ViaIconKey { get; set; }
}

public sealed class MediaSourcesState
{
    public string Type { get; set; } = "mediaSources";
    public List<MediaSourceInfo> Items { get; set; } = new();
}


public sealed class TilesState
{
    public string Type { get; set; } = "tiles";
    /// <summary>Id of the profile being shown.</summary>
    public string Active { get; set; } = "";
    /// <summary>The profile was chosen by what is in front, not pinned.</summary>
    public bool Auto { get; set; }
    public string? Pinned { get; set; }
    /// <summary>Editing is offered only over the cable; see DeckServer.</summary>
    public bool CanEdit { get; set; }
    public List<Tiles.TileProfile> Profiles { get; set; } = new();
    public List<string> Running { get; set; } = new();
    public TileErrorInfo? Error { get; set; }
    public TileCatalog Catalog { get; set; } = new();
}

public sealed class TileErrorInfo
{
    public string Tile { get; set; } = "";
    public string Message { get; set; } = "";
    public long AtMs { get; set; }
}

/// <summary>The vocabulary the editor offers, so the tablet cannot invent a step the host would refuse.</summary>
public sealed class TileCatalog
{
    public string[] Sys { get; set; } = Array.Empty<string>();
    public string[] Keys { get; set; } = Array.Empty<string>();
}

public sealed class NotificationItem
{
    public long Id { get; set; }
    public string App { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public long AtMs { get; set; }
}

public sealed class NotificationsState
{
    public string Type { get; set; } = "notifications";
    /// <summary>Switched on by the user. Off by default, and nothing is read while it is off.</summary>
    public bool Enabled { get; set; }
    /// <summary>This PC's Windows lets us read its notifications at all.</summary>
    public bool Available { get; set; }
    public string? Problem { get; set; }
    public bool CanEdit { get; set; }
    public List<NotificationItem> Items { get; set; } = new();
}

public sealed class TopProcessInfo
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public double? Cpu { get; set; }
    public long RamMb { get; set; }
    public double? Gpu { get; set; }
    /// <summary>Not offered for closing: the system, or security software.</summary>
    public bool Protected { get; set; }
    /// <summary>Already running at a lowered priority.</summary>
    public bool Low { get; set; }
    /// <summary>How many processes share this name.</summary>
    public int Count { get; set; } = 1;
}

public sealed class TopState
{
    public string Type { get; set; } = "top";
    /// <summary>CPU is a rate; the first look after a quiet spell has none.</summary>
    public bool HaveCpu { get; set; }
    public bool GpuAvailable { get; set; }
    public List<TopProcessInfo> Cpu { get; set; } = new();
    public List<TopProcessInfo> Ram { get; set; } = new();
    public List<TopProcessInfo> Gpu { get; set; } = new();
}

public sealed class HistoryState : Monitoring.HistoryData
{
    public string Type { get; set; } = "history";
    public int Seconds { get; set; }
    public long NowMs { get; set; }
}

public sealed class AlertsState
{
    public string Type { get; set; } = "alerts";
    public bool Enabled { get; set; }
    public int CpuTemp { get; set; }
    public int GpuTemp { get; set; }
    public int Ram { get; set; }
    public int Fan { get; set; }
    public bool CpuTempAvailable { get; set; }
    public bool GpuTempAvailable { get; set; }
    public bool FanAvailable { get; set; }
    public List<string> Active { get; set; } = new();
}

/// <summary>One crossing, sent the moment it happens whether or not the tablet is looking.</summary>
public sealed class AlertMessage
{
    public string Type { get; set; } = "alert";
    public string Metric { get; set; } = "";
    public string Label { get; set; } = "";
    public double Value { get; set; }
    public double Limit { get; set; }
    public long AtMs { get; set; }
}

public sealed class SessionsState
{
    public string Type { get; set; } = "sessions";
    public bool Enabled { get; set; }
    /// <summary>Frame rates need PresentMon; without it a session still records its length and load.</summary>
    public bool FpsAvailable { get; set; }
    public List<Monitoring.SessionRecord> Items { get; set; } = new();
    public bool AutoPower { get; set; }
    public bool AutoPowerApplied { get; set; }
    public bool AutoPowerAvailable { get; set; }
    public bool CanEdit { get; set; }
}

public sealed class FramesState
{
    public string Type { get; set; } = "frames";
    public double[] Times { get; set; } = Array.Empty<double>();
    public double? Fps { get; set; }
    /// <summary>The game being recorded now, if one is.</summary>
    public Monitoring.LiveSession? Live { get; set; }
}

public sealed class MacroInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public long Created { get; set; }
    public int Events { get; set; }
    public double Seconds { get; set; }
}

public sealed class MacroRecording
{
    public int Count { get; set; }
    public double Seconds { get; set; }
    /// <summary>The 60 second or 400 event limit was reached; nothing more is being kept.</summary>
    public bool Full { get; set; }
}

public sealed class MacrosState
{
    public string Type { get; set; } = "macros";
    public List<MacroInfo> Items { get; set; } = new();
    public MacroRecording? Recording { get; set; }
    public bool CanEdit { get; set; }
}

public sealed class CalendarEvent
{
    public string Title { get; set; } = "";
    public long StartMs { get; set; }
    public long EndMs { get; set; }
    public bool AllDay { get; set; }
    public string? Place { get; set; }
}

public sealed class ClockState
{
    public string Type { get; set; } = "clock";
    /// <summary>The PC's clock. The tablet's own may be wrong; it counts on from this.</summary>
    public long NowMs { get; set; }
    public int UtcOffsetMin { get; set; }
    public string Zone { get; set; } = "";
    public List<CalendarEvent> Events { get; set; } = new();
    /// <summary>Where calendar files go, shown so it is obvious how to add one.</summary>
    public string Folder { get; set; } = "";
    public int CalendarFiles { get; set; }
    /// <summary>Calendars subscribed by link. The address itself never leaves the PC.</summary>
    public List<CalendarSubInfo> Subscriptions { get; set; } = new();
    /// <summary>Adding or removing a calendar is offered only over the cable.</summary>
    public bool CanEdit { get; set; }
}

public sealed class CalendarSubInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public long LastOkMs { get; set; }
    /// <summary>Why the last fetch failed, or null. The previous copy is still shown meanwhile.</summary>
    public string? Error { get; set; }
}

public sealed class TimerInfo
{
    public string Id { get; set; } = "";
    /// <summary>countdown | stopwatch | pomodoro</summary>
    public string Kind { get; set; } = "countdown";
    public string Label { get; set; } = "";
    /// <summary>running | paused | ringing</summary>
    public string State { get; set; } = "running";
    public long DurationMs { get; set; }
    /// <summary>Wall-clock moment a running countdown reaches zero.</summary>
    public long? EndsAtMs { get; set; }
    public long? RemainingMs { get; set; }
    /// <summary>Stopwatch: the moment it would have started if never paused.</summary>
    public long? StartedAtMs { get; set; }
    public long? ElapsedMs { get; set; }
    /// <summary>work | short | long, for a pomodoro.</summary>
    public string? Phase { get; set; }
    public int? Round { get; set; }
    public int? Rounds { get; set; }
}

public sealed class TimersState
{
    public string Type { get; set; } = "timers";
    public long NowMs { get; set; }
    public List<TimerInfo> Items { get; set; } = new();
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
