using System.Diagnostics;
using CompanionDeck.Host.Protocol;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace CompanionDeck.Host.Audio;

/// <summary>
/// The laptop's audio surface: master and microphone levels, endpoint switching, and a
/// per-application mixer. Two correctness rules drive the shape of this class: 1.
/// </summary>
public sealed class AudioService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly NotificationClient _notifications;
    private readonly object _gate = new();

    private MMDevice? _render;
    private MMDevice? _capture;
    private volatile bool _stale = true;

    public bool Available { get; private set; }
    public bool CanSwitchDevices { get; private set; }

    /// <summary>
    /// Set when Windows reports an endpoint change. Callers check this rather than being
    /// called back, deliberately - see NotificationClient below.
    /// </summary>
    public bool ConsumeDevicesChanged()
    {
        if (Interlocked.Exchange(ref _devicesChanged, 0) == 0) return false;
        InvalidateDeviceCache();
        return true;
    }

    private int _devicesChanged;

    public AudioService()
    {
        _notifications = new NotificationClient(this);
    }

    public bool Initialize()
    {
        try
        {
            _enumerator.RegisterEndpointNotificationCallback(_notifications);
            Resolve(force: true);
            CanSwitchDevices = PolicyConfig.IsAvailable();
            Available = true;

            lock (_gate)
            {
                Log.Info($"Core Audio ready - out: {_render?.FriendlyName ?? "none"}, " +
                         $"in: {_capture?.FriendlyName ?? "none"}, " +
                         $"switching: {(CanSwitchDevices ? "yes" : "no")}");
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Core Audio unavailable: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Endpoint change notifications. These callbacks arrive on the audio engine's own thread and
    /// must return immediately.
    /// </summary>
    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly AudioService _owner;
        public NotificationClient(AudioService owner) => _owner = owner;

        private void Mark()
        {
            _owner._stale = true;
            Interlocked.Exchange(ref _owner._devicesChanged, 1);
        }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => Mark();
        public void OnDeviceAdded(string id) => Mark();
        public void OnDeviceRemoved(string id) => Mark();
        public void OnDeviceStateChanged(string id, DeviceState state) => Mark();
        public void OnPropertyValueChanged(string id, PropertyKey key) { }
    }

    /// <summary>
    /// Re-resolves the default endpoints, disposing the previous handles. Called lazily so
    /// a quiet machine does no COM work at all between device changes.
    /// </summary>
    private void Resolve(bool force = false)
    {
        if (!force && !_stale) return;

        lock (_gate)
        {
            if (!force && !_stale) return;
            _stale = false;

            var newRender = TryGetDefault(DataFlow.Render);
            var newCapture = TryGetDefault(DataFlow.Capture);

            // Only swap when the identity actually changed, so a snapshot mid-call keeps working
            // against the handle it started with. The freshly queried duplicate is safe to drop -
            // nobody else has seen it.
            if (newRender?.ID != _render?.ID)
            {
                Log.Info($"default output is now: {newRender?.FriendlyName ?? "none"}");
                Swap(ref _render, newRender);
            }
            else try { newRender?.Dispose(); } catch { }

            if (newCapture?.ID != _capture?.ID) Swap(ref _capture, newCapture);
            else try { newCapture?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Replaces a cached endpoint without disposing the outgoing one. A snapshot or a volume
    /// command running on another thread may still be part-way through a call on the old
    /// MMDevice; disposing it releases the COM object underneath them and every subsequent
    /// operation throws ObjectDisposedException - which presented as the controls going dead
    /// right after switching output.
    /// </summary>
    private static void Swap(ref MMDevice? slot, MMDevice? replacement) => slot = replacement;

    /// <summary>Console is the role the Windows Sound panel presents as "Default Device".</summary>
    private MMDevice? TryGetDefault(DataFlow flow)
    {
        try { return _enumerator.GetDefaultAudioEndpoint(flow, Role.Console); }
        catch { return null; }
    }

    private MMDevice? Render { get { Resolve(); lock (_gate) return _render; } }
    private MMDevice? Capture { get { Resolve(); lock (_gate) return _capture; } }


    // Endpoint enumeration is by far the most expensive thing here - each device means a
    // property-store open and a FriendlyName read - and the device list changes only when
    // hardware is plugged in or the default moves, both of which we are notified about.
    // Caching it takes the cost out of the once-per-second path entirely.
    private List<AudioDeviceInfo>? _cachedOutputs;
    private List<AudioDeviceInfo>? _cachedInputs;
    private string? _cachedForRenderId;
    private string? _cachedForCaptureId;

    public AudioState Snapshot()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var state = new AudioState();
        var render = Render;
        var capture = Capture;

        if (render is not null)
        {
            try
            {
                state.MasterVolume = (int)Math.Round(render.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
                state.MasterMuted = render.AudioEndpointVolume.Mute;
            }
            catch (Exception ex) { Log.Debug($"master volume: {ex.Message}"); _stale = true; }
        }

        if (capture is not null)
        {
            try
            {
                state.MicVolume = (int)Math.Round(capture.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
                state.MicMuted = capture.AudioEndpointVolume.Mute;
            }
            catch (Exception ex) { Log.Debug($"mic volume: {ex.Message}"); _stale = true; }
        }

        long tLevels = sw.ElapsedMilliseconds;

        EnsureDeviceLists(render, capture);

        state.Outputs = _cachedOutputs ?? new List<AudioDeviceInfo>();
        state.Inputs = _cachedInputs ?? new List<AudioDeviceInfo>();
        state.Apps = ListApps(render);

        if (sw.ElapsedMilliseconds > 120)
            Log.Debug($"audio snapshot {sw.ElapsedMilliseconds}ms " +
                      $"(levels {tLevels}, apps {sw.ElapsedMilliseconds - tLevels})");

        return state;
    }

    /// <summary>
    /// Refreshes the endpoint lists on a background thread, serving the previous lists until it
    /// finishes. Enumeration measured 4.4 seconds on this machine - a Bluetooth endpoint makes
    /// the audio stack wait - so it must never happen inline.
    /// </summary>
    private void EnsureDeviceLists(MMDevice? render, MMDevice? capture)
    {
        string? renderId = render?.ID;
        string? captureId = capture?.ID;

        bool stale = _cachedOutputs is null || _cachedInputs is null
                     || _cachedForRenderId != renderId || _cachedForCaptureId != captureId;
        if (!stale) return;

        // One refresh at a time; later calls simply keep using what is already cached.
        if (Interlocked.Exchange(ref _refreshingDevices, 1) == 1) return;

        Task.Run(() =>
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var outs = ListDevices(DataFlow.Render, renderId);
                var ins = ListDevices(DataFlow.Capture, captureId);

                _cachedOutputs = outs;
                _cachedInputs = ins;
                _cachedForRenderId = renderId;
                _cachedForCaptureId = captureId;

                Log.Debug($"endpoint enumeration took {sw.ElapsedMilliseconds}ms " +
                          $"({outs.Count} out, {ins.Count} in)");
            }
            catch (Exception ex) { Log.Debug($"endpoint refresh: {ex.Message}"); }
            finally { Interlocked.Exchange(ref _refreshingDevices, 0); }
        });
    }

    private int _refreshingDevices;

    /// <summary>Drops the cached endpoint lists so the next snapshot re-enumerates.</summary>
    private void InvalidateDeviceCache()
    {
        _cachedOutputs = null;
        _cachedInputs = null;
    }

    private List<AudioDeviceInfo> ListDevices(DataFlow flow, string? defaultId)
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            foreach (var d in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                try
                {
                    list.Add(new AudioDeviceInfo
                    {
                        Id = d.ID,
                        Name = CleanDeviceName(d.FriendlyName),
                        // Compared against the endpoint we actually drive, so the tick can
                        // never disagree with the device the master slider controls.
                        IsDefault = d.ID == defaultId,
                    });
                }
                finally { d.Dispose(); }
            }
        }
        catch (Exception ex) { Log.Debug($"enumerate {flow}: {ex.Message}"); }

        return list.OrderByDescending(d => d.IsDefault)
                   .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                   .ToList();
    }

    private static string CleanDeviceName(string name) =>
        name.Replace("(R)", "").Replace("  ", " ").Trim();

    /// <summary>Current sessions for an endpoint. The refresh is the whole point.</summary>
    private static SessionCollection? LiveSessions(MMDevice? device)
    {
        if (device is null) return null;
        try
        {
            var mgr = device.AudioSessionManager;
            mgr.RefreshSessions();
            return mgr.Sessions;
        }
        catch (Exception ex) { Log.Debug($"refresh sessions: {ex.Message}"); return null; }
    }

    private List<AudioAppInfo> ListApps(MMDevice? render)
    {
        var byProcess = new Dictionary<string, AudioAppInfo>(StringComparer.OrdinalIgnoreCase);
        if (render is null) return new List<AudioAppInfo>();

        try
        {
            var sessions = LiveSessions(render);
            if (sessions is null) return new List<AudioAppInfo>();

            for (int i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var s = sessions[i];
                    if (s.IsSystemSoundsSession) continue;
                    if (s.State == AudioSessionState.AudioSessionStateExpired) continue;

                    uint pid = s.GetProcessID;
                    if (pid == 0) continue;

                    string name = ProcessName(pid);
                    if (name.Length == 0) continue;

                    bool active = s.State == AudioSessionState.AudioSessionStateActive;
                    int vol = (int)Math.Round(s.SimpleAudioVolume.Volume * 100);
                    bool muted = s.SimpleAudioVolume.Mute;

                    if (byProcess.TryGetValue(name, out var existing))
                    {
                        // One app, many sessions (every browser does this): show it as
                        // active if any session is, and report the loudest.
                        existing.Active |= active;
                        existing.Volume = Math.Max(existing.Volume, vol);
                        existing.Muted &= muted;
                    }
                    else
                    {
                        byProcess[name] = new AudioAppInfo
                        {
                            Id = name.ToLowerInvariant(),
                            Name = Pretty(name),
                            Volume = vol,
                            Muted = muted,
                            Active = active,
                        };
                    }
                }
                catch (Exception ex) { Log.Debug($"session {i}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Debug($"session enumeration: {ex.Message}"); _stale = true; }

        // Alphabetical only. Sorting by "currently making sound" first looks tidier but
        // means a row can jump position the moment an app falls silent - including the
        // row the user is dragging. The live dot already conveys activity, so ordering
        // stays fixed and the mixer never moves under a finger.
        return byProcess.Values
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static readonly Dictionary<uint, string> ProcessNameCache = new();

    private static string ProcessName(uint pid)
    {
        lock (ProcessNameCache)
        {
            if (ProcessNameCache.TryGetValue(pid, out var cached)) return cached;
        }

        string name;
        try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; }
        catch { name = ""; }

        lock (ProcessNameCache)
        {
            if (ProcessNameCache.Count > 512) ProcessNameCache.Clear();
            ProcessNameCache[pid] = name;
        }
        return name;
    }

    private static string Pretty(string proc) => proc switch
    {
        "chrome" => "Chrome",
        "msedge" => "Edge",
        "brave" => "Brave",
        "firefox" => "Firefox",
        "Spotify" => "Spotify",
        "Discord" => "Discord",
        "vlc" => "VLC",
        _ => proc.Length > 0 ? char.ToUpperInvariant(proc[0]) + proc[1..] : proc,
    };


    /// <summary>
    /// Raising a volume also clears mute, which is what every volume control on Windows does -
    /// the taskbar flyout and the keyboard volume keys both unmute on the way up. Without this
    /// the tablet looked broken in a way that gave no clue: a muted endpoint ignores its scalar
    /// entirely, so dragging the slider moved the number, reported success, and produced no sound
    /// at all.
    /// </summary>
    private static void Unmute(AudioEndpointVolume vol, int percent)
    {
        if (percent > 0 && vol.Mute) vol.Mute = false;
    }

    public bool SetMasterVolume(int percent)
    {
        var d = Render;
        if (d is null) return false;
        try
        {
            int v = Math.Clamp(percent, 0, 100);
            d.AudioEndpointVolume.MasterVolumeLevelScalar = v / 100f;
            Unmute(d.AudioEndpointVolume, v);
            return true;
        }
        catch (Exception ex) { Log.Warn($"set master volume: {ex.Message}"); _stale = true; return false; }
    }

    public bool ToggleMasterMute()
    {
        var d = Render;
        if (d is null) return false;
        try { d.AudioEndpointVolume.Mute = !d.AudioEndpointVolume.Mute; return true; }
        catch (Exception ex) { Log.Warn($"toggle master mute: {ex.Message}"); _stale = true; return false; }
    }

    public bool SetMicVolume(int percent)
    {
        var d = Capture;
        if (d is null) return false;
        try
        {
            int v = Math.Clamp(percent, 0, 100);
            d.AudioEndpointVolume.MasterVolumeLevelScalar = v / 100f;
            Unmute(d.AudioEndpointVolume, v);
            return true;
        }
        catch (Exception ex) { Log.Warn($"set mic volume: {ex.Message}"); _stale = true; return false; }
    }

    public bool ToggleMicMute()
    {
        var d = Capture;
        if (d is null) return false;
        try { d.AudioEndpointVolume.Mute = !d.AudioEndpointVolume.Mute; return true; }
        catch (Exception ex) { Log.Warn($"toggle mic mute: {ex.Message}"); _stale = true; return false; }
    }

    /// <summary>
    /// Per-application level, and the same mute rule as the endpoints: a muted session
    /// stays silent whatever its volume is, so turning it up has to release the mute or
    /// the control does nothing audible. This is the "cannot control the music player"
    /// case - Spotify muted in the mixer ignores every volume change sent to it.
    /// </summary>
    public bool SetAppVolume(string id, int percent)
    {
        int v = Math.Clamp(percent, 0, 100);
        return ForEachSessionOf(id, s =>
        {
            s.SimpleAudioVolume.Volume = v / 100f;
            if (v > 0 && s.SimpleAudioVolume.Mute) s.SimpleAudioVolume.Mute = false;
        });
    }

    public bool ToggleAppMute(string id)
    {
        bool? target = null;
        return ForEachSessionOf(id, s =>
        {
            target ??= !s.SimpleAudioVolume.Mute;
            s.SimpleAudioVolume.Mute = target.Value;
        });
    }

    /// <summary>Current level for one process, used to show a media session's own volume.</summary>
    public (int volume, bool muted)? GetAppLevels(string processName)
    {
        var render = Render;
        if (render is null || processName.Length == 0) return null;

        try
        {
            var sessions = LiveSessions(render);
            if (sessions is null) return null;

            for (int i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var s = sessions[i];
                    if (s.IsSystemSoundsSession) continue;
                    uint pid = s.GetProcessID;
                    if (pid == 0) continue;
                    if (!string.Equals(ProcessName(pid), processName, StringComparison.OrdinalIgnoreCase)) continue;
                    return ((int)Math.Round(s.SimpleAudioVolume.Volume * 100), s.SimpleAudioVolume.Mute);
                }
                catch { }
            }
        }
        catch (Exception ex) { Log.Debug($"app levels {processName}: {ex.Message}"); }
        return null;
    }

    private bool ForEachSessionOf(string id, Action<AudioSessionControl> apply)
    {
        var render = Render;
        if (render is null) return false;

        bool any = false;
        try
        {
            var sessions = LiveSessions(render);
            if (sessions is null) return false;

            for (int i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var s = sessions[i];
                    if (s.IsSystemSoundsSession) continue;
                    uint pid = s.GetProcessID;
                    if (pid == 0) continue;
                    if (!string.Equals(ProcessName(pid), id, StringComparison.OrdinalIgnoreCase)) continue;
                    apply(s);
                    any = true;
                }
                catch (Exception ex) { Log.Debug($"apply to session {i}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn($"session mutate: {ex.Message}"); _stale = true; }
        return any;
    }

    public bool SetDefaultEndpoint(string deviceId)
    {
        if (!CanSwitchDevices) return false;
        if (!PolicyConfig.SetDefault(deviceId)) return false;

        // Do not wait for the notification callback; the caller publishes immediately after.
        _stale = true;
        InvalidateDeviceCache();
        Resolve();
        return true;
    }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(_notifications); } catch { }
        lock (_gate)
        {
            try { _render?.Dispose(); } catch { }
            try { _capture?.Dispose(); } catch { }
            _render = _capture = null;
        }
        try { _enumerator.Dispose(); } catch { }
    }
}
