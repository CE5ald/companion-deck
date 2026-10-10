using System.Text.Json;
using CompanionDeck.Host.Actions;
using CompanionDeck.Host.Assets;
using CompanionDeck.Host.Audio;
using CompanionDeck.Host.Context;
using CompanionDeck.Host.Launcher;
using CompanionDeck.Host.Media;
using CompanionDeck.Host.Protocol;
using CompanionDeck.Host.Telemetry;
using CompanionDeck.Host.Transport;

namespace CompanionDeck.Host.Server;

/// <summary>
/// Owns the capability services and serves whichever tablet link is currently up. The central
/// rule: nothing is collected unless a connected tablet has asked for it.
/// </summary>
public sealed class DeckServer : IAsyncDisposable
{
    private readonly AssetCache _assets = new();
    private readonly SystemCounters _counters = new();
    private readonly Nvml _nvml = new();
    private readonly GpuCounters _gpuCounters = new();
    private readonly Brightness _brightness = new();
    private readonly ThermalZone _thermal = new();
    private readonly PowerPlans _power = new();
    private readonly FpsProvider _fps = new();
    private readonly MediaService _media;
    private readonly AudioService _audio = new();
    private readonly ActionService _actions = new();
    private readonly ContextService _context = new();
    private readonly LauncherService _launcher;
    private readonly MediaSourceService _mediaSources;
    private readonly RunningAppsService _running = new();
    private readonly MicCastService _cast = new();
    private readonly SoundboardService _soundboard;
    private readonly RecorderService _recorder;

    private DeckLink? _link;
    private volatile HashSet<string> _sections = new();
    private volatile int _periodMs;                     // 0 = idle
    /// <summary>
    /// Wake handle for the publish loop, created fresh for each link. It used to be a single
    /// semaphore owned by the server and shared by every connection.
    /// </summary>
    private volatile SemaphoreSlim? _wake;
    private readonly object _sendLock = new();

    private string? _lastMediaJson;
    private string? _lastAudioJson;
    private string? _lastAppsJson;
    private string? _lastRunningJson;
    private DateTime _lastRunningScan;
    private string? _lastCastJson;
    private string? _lastSoundboardJson;
    private string? _lastSourcesJson;
    private string? _lastCtxJson;
    private DateTime _lastAppsScan = DateTime.MinValue;
    private double? _lastGpuPercent;
    private DateTime _lastPowerRead = DateTime.MinValue;
    private string? _lastPowerMode;

    // Smoothed at the source so the tablet's dials move deliberately instead of thrashing.
    private Ema _cpuEma = new(0.40);
    private Ema _gpuEma = new(0.40);
    private Ema _ramEma = new(0.55);   // already stable; only needs the edges taken off

    public DeckServer()
    {
        _media = new MediaService(_assets);
        _launcher = new LauncherService(_assets);
        _mediaSources = new MediaSourceService(_assets);
        _soundboard = new SoundboardService(_cast);
        _recorder = new RecorderService(_soundboard, _cast);
    }

    public async Task InitializeAsync()
    {
        _counters.Initialize();

        // Setting COMPANIONDECK_GENERIC=1 skips the vendor-specific providers, so the
        // portable path - Windows GPU counters and Windows power modes - can be exercised
        // on a machine that would otherwise take a vendor-specific route. There is no
        // other way to test what most people will actually run.
        bool generic = Environment.GetEnvironmentVariable("COMPANIONDECK_GENERIC") == "1";
        if (generic) Log.Info("COMPANIONDECK_GENERIC=1 - vendor providers skipped");

        // NVML is richer - temperature, power and clocks - so it wins where it exists.
        // The Windows counters are the fallback for AMD, Intel and driverless machines,
        // and are only opened when NVML is absent so no machine pays for both.
        if (generic || !_nvml.TryInitialize()) _gpuCounters.TryInitialize();
        _brightness.Probe();
        // Universal CPU temperature. Probed whether or not a vendor interface answered,
        // because the vendor one can be present and still report nothing useful.
        _thermal.Probe();
        // Only consulted when the machine has no vendor thermal interface, but probing is
        // cheap and knowing both lets the tablet label the control correctly.
        _power.Probe();
        _audio.Initialize();
        // Before anything else touches audio: if the last run died while casting, the
        // machine is still recording from a cable nothing is feeding.
        _cast.RecoverIfStranded();
        _soundboard.Initialize();
        // A file appearing in the folder, or a clip finishing, republishes rather than
        // being waited for on the next tick.
        _soundboard.Changed += () => { _lastSoundboardJson = null; if (_sections.Contains("soundboard")) PublishSoundboard(); };
        await _media.InitializeAsync().ConfigureAwait(false);

        _media.Changed += () => PublishMedia();

        // Endpoint changes are picked up by the publish loop rather than through a
        // callback; see the note on AudioService.NotificationClient.

        // Disk walking and icon rasterising takes a second or two; do it off the startup
        // path so the tablet can connect immediately and the catalogue fills in behind it.
        _ = Task.Run(() =>
        {
            try
            {
                _launcher.Scan();
                _context.SetKnownGames(_launcher.KnownGameExecutables);
                _mediaSources.Scan(_launcher);
                _lastAppsJson = null;
                _lastSourcesJson = null;
                if (_sections.Contains("mediaSources")) PublishMediaSources();
                if (_sections.Contains("apps")) PublishApps();
            }
            catch (Exception ex) { Log.Warn($"launcher scan failed: {ex.Message}"); }
        });
    }


    public async Task ServeAsync(DeckLink link, CancellationToken ct)
    {
        // A second link can arrive while the first is still tearing down - restarting the adb
        // server does exactly that. The newcomer wins, and the old one is ended now rather
        // than left to notice on its own: both share the publish state below, and a stale
        // link finishing late used to zero the period of the live one, leaving a connected
        // tablet with nothing arriving until it next changed page.
        var previous = Interlocked.Exchange(ref _link, link);
        if (previous is not null && !ReferenceEquals(previous, link))
        {
            Log.Info("a new link supersedes the previous one");
            previous.Abort();
        }
        _assets.ResetPeer();
        // Every section's dedupe cache must be cleared for a new link, not just media.
        // A reconnecting tablet has no state at all, so anything still matching the
        // previous peer's last payload would be suppressed and never arrive.
        ResetPublishCaches();
        _sections = new HashSet<string>();
        _periodMs = 0;

        link.OnMessage += (type, doc) => HandleAsync(link, type, doc);

        using var linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var wake = new SemaphoreSlim(0, 1);
        _wake = wake;
        var publisher = Task.Run(() => PublishLoopAsync(wake, linkCts.Token), linkCts.Token);

        try
        {
            await link.ReadLoopAsync(linkCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (IOException ex) { Log.Info($"link ended: {ex.Message}"); }
        catch (Exception ex)
        {
            // A cable being unplugged is an IOException and is ordinary. Anything else is a
            // defect in this agent, and logging only ex.Message reduced it to "Object
            // reference not set to an instance of an object" - which named neither the
            // field nor the method, and left the link reconnecting in a loop with no way to
            // tell what was wrong.
            Log.Error($"link ended unexpectedly: {ex}");
        }
        finally
        {
            linkCts.Cancel();

            // Only the link that still owns the server may park it. If a newer link has
            // already taken over, the publish period and wake handle are now its, and
            // resetting them here would silence a tablet that is working perfectly.
            bool owner = ReferenceEquals(_link, link);
            if (owner)
            {
                _periodMs = 0;
                TryWake();
            }
            // Bounded. This used to be an unconditional await, so a publish loop stuck on a
            // slow service - a Bluetooth audio device being torn down inside a lock, in the
            // case that found this - meant ServeAsync never returned, the adb forward was
            // never released, and the tablet could never reconnect. A wedged section is a
            // bug to fix, but it must not also cost the link.
            try
            {
                if (await Task.WhenAny(publisher, Task.Delay(4000)).ConfigureAwait(false) != publisher)
                    Log.Warn("publish loop did not stop in time; abandoning it so the link can reconnect");
                else await publisher.ConfigureAwait(false);
            }
            catch { }

            // Never leave an ETW session running once nobody is watching.
            if (owner) _fps.Stop();

            // Only clear the slots if they still refer to this link. A later connection may
            // already have claimed them while this one was tearing down.
            Interlocked.CompareExchange(ref _link, null, link);
            if (ReferenceEquals(_wake, wake)) _wake = null;
            wake.Dispose();

            await link.DisposeAsync().ConfigureAwait(false);
            Log.Info("link closed");
        }
    }

    private async Task HandleAsync(DeckLink link, string type, JsonDocument doc)
    {
        switch (type)
        {
            case "hello":
                if (link.Hello?.CachedAssets is { Count: > 0 } cached)
                    _assets.MarkPeerHas(cached);
                link.SendJson(BuildHostHello(link.Transport));
                PublishMedia();
                break;

            case "sub":
                ApplySubscription(doc);
                break;

            case "asset":
                if (doc.RootElement.TryGetProperty("key", out var k) && k.GetString() is { } key)
                    SendAsset(link, key);
                break;

            case "cmd":
                await HandleCommandAsync(link, doc).ConfigureAwait(false);
                break;
        }
    }

    private void ApplySubscription(JsonDocument doc)
    {
        var root = doc.RootElement;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("sections", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var s in arr.EnumerateArray())
                if (s.GetString() is { Length: > 0 } name) set.Add(name);

        double hz = root.TryGetProperty("hz", out var h) && h.TryGetDouble(out var v) ? v : 0;
        hz = Math.Clamp(hz, 0, 10);

        _sections = set;
        _periodMs = (set.Count == 0 || hz <= 0) ? 0 : (int)Math.Round(1000.0 / hz);

        Log.Debug(_periodMs == 0
            ? "subscription: idle (tablet not viewing)"
            : $"subscription: [{string.Join(",", set)}] every {_periodMs}ms");

        // Event-driven and rarely-changing sections repaint immediately on (re)subscribe,
        // rather than making the tablet wait for the next tick to show anything.
        if (set.Contains("media")) { _lastMediaJson = null; PublishMedia(); }
        if (set.Contains("apps")) { _lastAppsJson = null; _lastAppsScan = DateTime.UtcNow; PublishApps(); }
        if (set.Contains("ctx")) { _lastCtxJson = null; PublishContext(); }
        if (set.Contains("mediaSources")) { _lastSourcesJson = null; PublishMediaSources(); }
        if (set.Contains("running")) { _lastRunningJson = null; _lastRunningScan = DateTime.UtcNow; PublishRunning(); }
        if (set.Contains("cast")) { _lastCastJson = null; PublishCast(); }
        if (set.Contains("soundboard")) { _lastSoundboardJson = null; PublishSoundboard(); }
        TryWake();
    }


    private async Task PublishLoopAsync(SemaphoreSlim wake, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            int period = _periodMs;

            if (period == 0)
            {
                // Fully parked: no timers, no sampling, nothing measurable. The timeout is
                // a safety net only - if a wake signal is ever missed, the loop recovers on
                // its own instead of staying asleep with a live subscription.
                try { await wake.WaitAsync(2000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                continue;
            }

            try
            {
                // A headset plugged in or removed invalidates both the endpoint list and
                // the levels we last sent. Picked up here, on our own thread.
                if (_audio.ConsumeDevicesChanged()) { _lastAudioJson = null; _lastMediaJson = null; }

                if (_sections.Contains("sys")) PublishSys();
                if (_sections.Contains("audio")) PublishAudio();
                if (_sections.Contains("ctx")) PublishContext();

                // Running-state needs a full process enumeration, which is far too costly
                // to do at the telemetry rate. Every few seconds is plenty for a badge.
                if (_sections.Contains("apps") && (DateTime.UtcNow - _lastAppsScan).TotalSeconds >= 4)
                {
                    _lastAppsScan = DateTime.UtcNow;
                    PublishApps();
                }

                // Enumerating every process and every top-level window is heavier again, and
                // this list is read rather than watched, so it refreshes more slowly still.
                if (_sections.Contains("running") && (DateTime.UtcNow - _lastRunningScan).TotalSeconds >= 3)
                {
                    _lastRunningScan = DateTime.UtcNow;
                    PublishRunning();
                }

                // Cheap, and it carries the live level meters - which are the only proof
                // from the tablet that the cast is actually passing audio rather than
                // merely claiming to be armed.
                // Endpoints move when a headset is plugged in; anything holding one has to
                // notice rather than keep feeding a device nobody is using.
                if (_sections.Contains("cast") || _sections.Contains("soundboard"))
                {
                    _cast.CheckDevices();
                    _recorder.CheckDevice();
                }
                if (_sections.Contains("cast")) PublishCast();
                if (_sections.Contains("soundboard")) PublishSoundboard();
            }
            catch (Exception ex) { Log.Debug($"publish tick: {ex.Message}"); }

            try { await Task.Delay(period, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void TryWake()
    {
        var wake = _wake;
        if (wake is null) return;
        try { if (wake.CurrentCount == 0) wake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }   // link tore down underneath us
    }

    private void PublishSys()
    {
        var link = _link;
        if (link is null) return;

        var (memPct, usedMb, totalMb) = _counters.ReadMemory();
        var (down, up) = _counters.ReadNetwork();
        var (batt, charging) = _counters.ReadBattery();

        var s = new SysState
        {
            Cpu = Math.Round(_cpuEma.Push(_counters.ReadCpuPercent()), 1),
            RamPercent = Math.Round(_ramEma.Push(memPct), 1),
            RamUsedMb = usedMb,
            RamTotalMb = totalMb,
            DiskPercent = _counters.ReadDiskPercent() is { } d ? Math.Round(d, 1) : null,
            NetDownKbps = down is { } dn ? Math.Round(dn, 1) : null,
            NetUpKbps = up is { } u2 ? Math.Round(u2, 1) : null,
            Battery = batt,
            Charging = charging,
        };

        if ((_nvml.Sample() ?? _gpuCounters.Sample()) is { } g)
        {
            s.Gpu = g.UtilPercent is { } gu ? Math.Round(_gpuEma.Push(gu), 1) : null;
            _lastGpuPercent = s.Gpu;   // context detection uses this to corroborate "gaming"
            s.GpuTemp = g.TempC;
            s.VramUsedMb = g.VramUsedMb;
            s.VramTotalMb = g.VramTotalMb;
            s.GpuPowerW = g.PowerW is { } p ? Math.Round(p, 1) : null;
            s.GpuClockMhz = g.ClockMhz;
        }


        // Constant for the life of the process, but it travels with every sample because
        // the tablet has no other channel for it and the payload is already deduplicated
        // upstream - an unchanged sys block is never sent twice.

        s.CpuTemp ??= _thermal.Available ? _thermal.Read() : null;

        // The power scheme is the performance control on anything without a vendor EC.
        // Read at a tenth of the sample rate: it only changes when something changes it,
        // and each read is a process spawn.
        if (_power.Available && s.ThermalMode is null)
        {
            if ((DateTime.UtcNow - _lastPowerRead).TotalSeconds > 5)
            {
                _lastPowerRead = DateTime.UtcNow;
                _lastPowerMode = _power.CurrentMode();
            }
            s.ThermalMode = _lastPowerMode;
        }

        if (_brightness.CanRead) s.Brightness = _brightness.Read();

        // FPS costs an ETW session, so it runs only while a game is in front and this
        // tablet is actually displaying performance. Anything else stops it immediately.
        //
        // Dropping a file named "fps-debug" beside the log forces tracking of whatever is
        // in the foreground, so the pipeline can be exercised without launching a game.
        // Delete the file to return to normal behaviour.
        bool wantFps = _fps.Available && (_context.Mode == "gaming" || FpsDebugRequested());
        _fps.Track(wantFps ? _context.ForegroundPid : 0);
        if (_fps.Sample() is { } f) { s.Fps = f.Fps; s.Fps1Low = f.OnePercentLow; }

        lock (_sendLock) link.SendJson(s);
    }

    private readonly Dictionary<string, DateTime> _lastLogged = new();

    /// <summary>True at most once every two seconds for a given action.</summary>
    private bool ShouldLogThrottled(string action)
    {
        lock (_lastLogged)
        {
            if (_lastLogged.TryGetValue(action, out var when) &&
                (DateTime.UtcNow - when).TotalSeconds < 2) return false;
            _lastLogged[action] = DateTime.UtcNow;
            return true;
        }
    }

    private static readonly string FpsDebugFlag = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CompanionDeck", "fps-debug");

    private DateTime _fpsFlagCheckedAt;
    private bool _fpsFlagValue;

    /// <summary>Checked at most once a second; this sits on the telemetry path.</summary>
    private bool FpsDebugRequested()
    {
        if ((DateTime.UtcNow - _fpsFlagCheckedAt).TotalMilliseconds < 1000) return _fpsFlagValue;
        _fpsFlagCheckedAt = DateTime.UtcNow;
        try { _fpsFlagValue = File.Exists(FpsDebugFlag); } catch { _fpsFlagValue = false; }
        return _fpsFlagValue;
    }

    private void ResetPublishCaches()
    {
        _lastMediaJson = null;
        _lastAudioJson = null;
        _lastAppsJson = null;
        _lastSourcesJson = null;
        _lastCtxJson = null;
        _lastRunningJson = null;
        _lastCastJson = null;
        _lastSoundboardJson = null;
        _lastAppsScan = DateTime.MinValue;
        _lastRunningScan = DateTime.MinValue;
    }

    private void PublishContext()
    {
        var link = _link;
        if (link is null) return;

        var media = _media.Snapshot();
        var playing = media.Sessions.Any(s => s.Status == "playing");
        var playingApp = media.Sessions.FirstOrDefault(s => s.Status == "playing")?.AppName;

        var state = _context.Sample(_lastGpuPercent, playing, playingApp);

        var json = System.Text.Encoding.UTF8.GetString(Json.Serialize(state));
        if (json == _lastCtxJson) return;
        _lastCtxJson = json;

        lock (_sendLock) link.SendJson(state);
    }

    // The endpoint list is COM enumeration, and this section publishes eight times a second
    // for the meters. Refreshed on its own slower schedule instead.
    private List<AudioDeviceInfo> _castOutputs = new();
    private List<AudioDeviceInfo> _castInputs = new();
    private DateTime _castDevicesAt = DateTime.MinValue;

    private void PublishCast()
    {
        var link = _link;
        if (link is null) return;

        if ((DateTime.UtcNow - _castDevicesAt).TotalSeconds >= 2)
        {
            _castDevicesAt = DateTime.UtcNow;
            _castOutputs = _cast.ListDevices(render: true);
            _castInputs = _cast.ListDevices(render: false);
        }

        var (dPeak, mPeak) = _cast.ReadPeaks();
        var state = new CastState
        {
            Available = _cast.Available,
            Armed = _cast.Armed,
            MicName = _cast.MicName,
            MicEnabled = _cast.MicEnabled,
            DesktopGain = _cast.DesktopGain,
            MicGain = _cast.MicGain,
            DesktopPeak = dPeak,
            MicPeak = mPeak,
            Error = _cast.LastError,
            Outputs = _castOutputs,
            Inputs = _castInputs,
        };

        // Meters move every tick by design, so this section is deliberately not deduplicated
        // on the whole payload - only on everything except the levels, or an idle cast would
        // never stop sending.
        var stable = System.Text.Encoding.UTF8.GetString(Json.Serialize(new CastState
        {
            Available = state.Available, Armed = state.Armed, MicName = state.MicName,
            MicEnabled = state.MicEnabled, DesktopGain = state.DesktopGain,
            MicGain = state.MicGain, Error = state.Error,
            Outputs = state.Outputs, Inputs = state.Inputs,
        }));
        bool quiet = dPeak == 0 && mPeak == 0;
        if (quiet && stable == _lastCastJson) return;
        _lastCastJson = stable;

        lock (_sendLock) link.SendJson(state);
    }

    private void PublishSoundboard()
    {
        var link = _link;
        if (link is null) return;

        var state = _soundboard.Snapshot();
        state.RecorderArmed = _recorder.Armed;
        state.Recording = _recorder.Recording;
        state.BufferedSeconds = _recorder.BufferedSeconds;
        state.BufferCapacity = _recorder.Capacity;
        state.RecordingSeconds = _recorder.RecordingSeconds;
        state.Normalise = _soundboard.Normalise;
        state.LastSaved = _recorder.LastSaved;
        state.Error = _recorder.LastError;

        var json = System.Text.Encoding.UTF8.GetString(Json.Serialize(state));
        if (json == _lastSoundboardJson) return;
        _lastSoundboardJson = json;

        lock (_sendLock) link.SendJson(state);
    }

    private void PublishRunning()
    {
        var link = _link;
        if (link is null) return;

        var state = _running.Snapshot();
        var json = System.Text.Encoding.UTF8.GetString(Json.Serialize(state));
        if (json == _lastRunningJson) return;
        _lastRunningJson = json;

        lock (_sendLock) link.SendJson(state);
    }

    private void PublishApps()
    {
        var link = _link;
        if (link is null) return;

        var state = _launcher.Snapshot();
        var json = System.Text.Encoding.UTF8.GetString(Json.Serialize(state));
        if (json == _lastAppsJson) return;
        _lastAppsJson = json;

        lock (_sendLock)
        {
            foreach (var item in state.Items)
            {
                if (item.IconKey is { } key && _assets.PeerNeeds(key) && _assets.Get(key) is { } bytes)
                {
                    link.SendAsset(key, bytes);
                    _assets.MarkPeerHas(key);
                }
            }
            link.SendJson(state);
        }
    }

    /// <summary>
    /// The media source list changes only when something is installed or uninstalled, so it is
    /// published on subscribe and after a scan rather than on the telemetry tick.
    /// </summary>
    private void PublishMediaSources()
    {
        var link = _link;
        if (link is null || !_mediaSources.Any) return;

        var state = _mediaSources.Snapshot();
        var json = System.Text.Encoding.UTF8.GetString(Json.Serialize(state));
        if (json == _lastSourcesJson) return;
        _lastSourcesJson = json;

        lock (_sendLock)
        {
            foreach (var item in state.Items)
            {
                if (item.IconKey is { } key && _assets.PeerNeeds(key) && _assets.Get(key) is { } bytes)
                {
                    link.SendAsset(key, bytes);
                    _assets.MarkPeerHas(key);
                }
            }
            link.SendJson(state);
        }
    }

    private void PublishAudio()
    {
        var link = _link;
        if (link is null || !_audio.Available) return;

        var state = _audio.Snapshot();

        // A working PC always has at least one render endpoint, so an empty list is never a
        // true reading - it is an enumeration that came back before Core Audio was ready.
        // Publishing it would stick, because audio is sent once per link and only again
        // when something changes: the tablet would show no devices until the user happened
        // to plug a headset in. Skipping it costs one tick and the next one carries the
        // real list. Seen twice under test and never reproduced on demand, which is
        // precisely why this guards the symptom rather than trusting a diagnosis.
        if (state.Outputs.Count == 0)
        {
            Log.Debug("audio snapshot had no output devices; not publishing it");
            return;
        }

        var json = System.Text.Encoding.UTF8.GetString(Json.Serialize(state));
        // Volume and session lists are mostly static; only send when something moved.
        if (json == _lastAudioJson) return;
        _lastAudioJson = json;

        lock (_sendLock) link.SendJson(state);
    }

    private void PublishMedia()
    {
        var link = _link;
        if (link is null || !_sections.Contains("media")) return;

        var state = _media.Snapshot();

        // Pair each media session with its audio session so the card can offer that
        // application's own level instead of only the Windows master.
        if (_audio.Available)
        {
            foreach (var sess in state.Sessions)
            {
                var proc = MediaService.ProcessNameFor(sess.Id);
                if (_audio.GetAppLevels(proc) is { } lv) { sess.AppVolume = lv.volume; sess.AppMuted = lv.muted; }
            }
        }

        var json = System.Text.Encoding.UTF8.GetString(Json.Serialize(state));
        if (json == _lastMediaJson) return;
        _lastMediaJson = json;

        lock (_sendLock)
        {
            // Push any artwork the tablet has not cached before the state that references it.
            foreach (var sess in state.Sessions)
            {
                if (sess.ArtKey is { } key && _assets.PeerNeeds(key) && _assets.Get(key) is { } bytes)
                {
                    link.SendAsset(key, bytes);
                    _assets.MarkPeerHas(key);
                }
            }
            link.SendJson(state);
        }
    }

    private void SendAsset(DeckLink link, string key)
    {
        if (_assets.Get(key) is not { } bytes) return;
        lock (_sendLock) link.SendAsset(key, bytes);
        _assets.MarkPeerHas(key);
    }


    private async Task HandleCommandAsync(DeckLink link, JsonDocument doc)
    {
        var root = doc.RootElement;
        long id = root.TryGetProperty("id", out var i) && i.TryGetInt64(out var idv) ? idv : 0;
        string action = root.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
        JsonElement args = root.TryGetProperty("args", out var ae) ? ae : default;

        bool ok = false;
        string? error = null;

        try
        {
            // Checked ahead of the "media." prefix below: opening a source is not a
            // transport command against the current session, and the prefix test would
            // otherwise swallow it and report that the session rejected the command.
            if (action == "media.openSource")
            {
                string sourceId = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("id", out var sid)
                    ? sid.GetString() ?? "" : "";
                // Same contract as "launch": the tablet names an entry from a catalogue we
                // published, and the program or address behind it never leaves this process.
                ok = _mediaSources.Launch(sourceId);
                if (!ok) error = "unknown media source";
            }
            else if (action.StartsWith("media.", StringComparison.Ordinal))
            {
                string verb = action["media.".Length..];
                string session = args.ValueKind == JsonValueKind.Object &&
                                 args.TryGetProperty("session", out var se) ? se.GetString() ?? "" : "";
                double? value = args.ValueKind == JsonValueKind.Object &&
                                args.TryGetProperty("value", out var ve) && ve.TryGetDouble(out var vv) ? vv : null;

                // Volume for a media source is really an audio-mixer operation on the
                // process behind that session, so it is routed there rather than to GSMTC.
                if (verb is "appVolume" or "appMute")
                {
                    var proc = MediaService.ProcessNameFor(
                        session.Length > 0 ? session : _media.Snapshot().ActiveId ?? "");

                    ok = verb == "appVolume"
                        ? value is not null && _audio.SetAppVolume(proc, (int)value.Value)
                        : _audio.ToggleAppMute(proc);

                    // Name the process we looked for. When a media session cannot be paired
                    // with an audio session it is almost always because the AppUserModelId
                    // does not match the executable name, and that is impossible to work
                    // out from a bare failure.
                    if (!ok) error = $"no audio session for media source '{session}' (looked for process '{proc}')";
                    else { _lastMediaJson = null; _lastAudioJson = null; PublishMedia(); }
                }
                else
                {
                    ok = await _media.ControlAsync(session, verb, value).ConfigureAwait(false);
                    if (!ok) error = "media session rejected the command";
                }
            }
            else if (action == "game.shortcut")
            {
                string shortcutId = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("id", out var si)
                    ? si.GetString() ?? "" : "";
                ok = _actions.GameShortcut(shortcutId);
                if (!ok) error = $"shortcut '{shortcutId}' not available";
            }
            else if (action.StartsWith("sb.", StringComparison.Ordinal))
            {
                string what = action["sb.".Length..];
                string sbId = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("id", out var si2)
                    ? si2.GetString() ?? "" : "";

                switch (what)
                {
                    case "play": ok = _soundboard.Play(sbId); if (!ok) error = "that clip could not be played"; break;
                    case "stop": _soundboard.StopAll(); ok = true; break;
                    case "normalise":
                        _soundboard.SetNormalise(args.TryGetProperty("value", out var nv) && nv.TryGetInt32(out var ni) && ni != 0);
                        ok = true; break;
                    case "name":
                        ok = _soundboard.SetName(sbId, args.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "");
                        if (!ok) error = "that name was rejected";
                        break;
                    case "route":
                        ok = _soundboard.SetRoute(sbId, args.TryGetProperty("route", out var rt) ? rt.GetString() ?? "" : "");
                        if (!ok) error = "unknown routing";
                        break;
                    case "gain":
                        ok = _soundboard.SetGain(sbId,
                            args.TryGetProperty("value", out var gv) && gv.TryGetInt32(out var gi) ? gi : -1);
                        break;
                    default: error = $"unknown soundboard command '{what}'"; break;
                }

                if (ok) { _lastSoundboardJson = null; if (_sections.Contains("soundboard")) PublishSoundboard(); }
            }
            else if (action.StartsWith("rec.", StringComparison.Ordinal))
            {
                string what = action["rec.".Length..];
                switch (what)
                {
                    case "arm": ok = _recorder.Arm(); if (!ok) error = _recorder.LastError ?? "could not arm the recorder"; break;
                    case "disarm": _recorder.Disarm(); ok = true; break;
                    case "start": ok = _recorder.StartRecording(); if (!ok) error = _recorder.LastError; break;
                    case "stop": ok = _recorder.StopRecording() is not null; if (!ok) error = "nothing was being recorded"; break;
                    case "clip":
                        int secs = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("seconds", out var sv)
                            && sv.TryGetInt32(out var si3) ? si3 : 30;
                        ok = _recorder.SaveClip(secs) is not null;
                        if (!ok) error = _recorder.LastError ?? "nothing to save";
                        break;
                    default: error = $"unknown recorder command '{what}'"; break;
                }

                if (ok) { _lastSoundboardJson = null; if (_sections.Contains("soundboard")) PublishSoundboard(); }
            }
            else if (action.StartsWith("cast.", StringComparison.Ordinal))
            {
                string what = action["cast.".Length..];
                int value = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("value", out var cv)
                    && cv.TryGetInt32(out var iv) ? iv : -1;

                switch (what)
                {
                    case "arm": ok = _cast.Arm(); if (!ok) error = _cast.LastError ?? "could not arm the cast"; break;
                    case "disarm": _cast.Disarm(); ok = true; break;
                    case "desktopGain": _cast.SetDesktopGain(value); ok = value >= 0; break;
                    case "micGain": _cast.SetMicGain(value); ok = value >= 0; break;
                    case "micEnabled": _cast.SetMicEnabled(value != 0); ok = value >= 0; break;
                    case "source":
                    {
                        string kind = args.TryGetProperty("kind", out var sk) ? sk.GetString() ?? "" : "";
                        string? devId = args.TryGetProperty("device", out var sd) ? sd.GetString() : null;
                        ok = _cast.SetSource(kind, devId);
                        if (!ok) error = _cast.LastError ?? "could not switch source";
                        // The list carries which one is selected, so it must be rebuilt now
                        // rather than up to two seconds later.
                        _castDevicesAt = DateTime.MinValue;
                        break;
                    }
                    default: error = $"unknown cast command '{what}'"; break;
                }

                if (ok) { _lastCastJson = null; if (_sections.Contains("cast")) PublishCast(); }
            }
            else if (action == "running.close" || action == "running.kill")
            {
                int pid = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("pid", out var pe)
                    && pe.TryGetInt32(out var pv) ? pv : 0;

                bool force = action == "running.kill";
                ok = force ? _running.Kill(pid) : _running.Close(pid);
                if (!ok) error = force
                    ? "that process could not be ended"
                    : "the app did not accept a close request";

                // Reflect the change on the next tick rather than immediately: an app given
                // WM_CLOSE has not gone yet, and may put up a save prompt and stay.
                _lastRunningJson = null;
                _lastRunningScan = DateTime.MinValue;
                TryWake();
            }
            else if (action == "sys.thermalMode")
            {
                string mode = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("mode", out var tm)
                    ? tm.GetString() ?? "" : "";
                // One command, two implementations: the vendor EC where it exists,
                // Windows power schemes everywhere else.
                if (_power.Available)
                {
                    ok = _power.SetMode(mode);
                    if (ok) { _lastPowerRead = DateTime.MinValue; }
                    else error = "Windows refused that power scheme";
                }
                else
                {
                    error = "no performance control on this machine";
                }
                // Reflect the new mode immediately rather than waiting for the next sample.
                if (ok && _sections.Contains("sys")) PublishSys();
            }
            else if (action.StartsWith("audio.", StringComparison.Ordinal))
            {
                ok = HandleAudioCommand(action["audio.".Length..], args);
                if (!ok) error = "audio endpoint rejected the command";
                // Reflect the change back immediately rather than waiting for the next tick.
                if (ok) { _lastAudioJson = null; if (_sections.Contains("audio")) PublishAudio(); }
            }
            else if (action == "sys.brightness")
            {
                int level = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("value", out var be)
                            && be.TryGetInt32(out var bv) ? bv : -1;
                ok = level >= 0 && _brightness.CanWrite && _brightness.Set(level);
                if (!ok) error = _brightness.CanWrite ? "brightness change rejected" : "brightness needs the elevated agent";
            }
            else if (action.StartsWith("sys.", StringComparison.Ordinal))
            {
                ok = _actions.Execute(action["sys.".Length..]);
                if (!ok) error = "action not permitted";
            }
            else if (action.StartsWith("launcher.", StringComparison.Ordinal))
            {
                string entryId = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("id", out var lid)
                    ? lid.GetString() ?? "" : "";
                bool value = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("value", out var lv)
                             && lv.ValueKind == JsonValueKind.True;

                if (_launcher.Find(entryId) is null) { error = "unknown launcher entry"; }
                else
                {
                    switch (action["launcher.".Length..])
                    {
                        case "fav": _launcher.Prefs.SetFavourite(entryId, value); ok = true; break;
                        case "hide": _launcher.Prefs.SetHidden(entryId, value); ok = true; break;
                        default: error = "unknown launcher action"; break;
                    }
                    // Reflect the new ordering at once rather than on the next scan tick.
                    if (ok) { _lastAppsJson = null; if (_sections.Contains("apps")) PublishApps(); }
                }
            }
            else if (action == "launch")
            {
                string targetId = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("id", out var le)
                    ? le.GetString() ?? "" : "";
                // The tablet names a catalogue entry; the path comes from our own scan,
                // so a crafted message can never launch something we didn't discover.
                var entry = _launcher.Find(targetId);
                if (entry is null) error = "unknown launch target";
                else
                {
                    ok = _actions.Launch(entry.Target);
                    if (!ok) error = "launch failed";
                }
            }
            else
            {
                error = $"unknown action '{action}'";
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Warn($"command '{action}' threw: {ex.Message}");
        }

        // Log every command and its outcome. Without this a control that quietly does nothing is
        // indistinguishable from a tap that never arrived, which is exactly the ambiguity that
        // made the endpoint-switch bug hard to pin down.
        if (!ok)
        {
            Log.Warn($"cmd {action} FAILED: {error ?? "?"}");
        }
        else if (action.EndsWith("Volume", StringComparison.Ordinal) || action == "sys.brightness")
        {
            if (ShouldLogThrottled(action)) Log.Info($"cmd {action} -> ok (further updates suppressed)");
            else Log.Debug($"cmd {action} -> ok");
        }
        else Log.Info($"cmd {action} -> ok");

        link.SendJson(new CommandResult { Id = id, Ok = ok, Error = error });
    }

    private bool HandleAudioCommand(string verb, JsonElement args)
    {
        string id = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("id", out var ie)
            ? ie.GetString() ?? "" : "";
        int value = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("value", out var ve)
                    && ve.TryGetInt32(out var v) ? v : -1;

        return verb switch
        {
            "masterVolume" => value >= 0 && _audio.SetMasterVolume(value),
            "masterMuteToggle" => _audio.ToggleMasterMute(),
            "micVolume" => value >= 0 && _audio.SetMicVolume(value),
            "micMuteToggle" => _audio.ToggleMicMute(),
            "appVolume" => id.Length > 0 && value >= 0 && _audio.SetAppVolume(id, value),
            "appMuteToggle" => id.Length > 0 && _audio.ToggleAppMute(id),
            "setOutput" => id.Length > 0 && _audio.SetDefaultEndpoint(id),
            "setInput" => id.Length > 0 && _audio.SetDefaultEndpoint(id),
            _ => false,
        };
    }


    private HostHello BuildHostHello(string transport) => new()
    {
        HostName = Environment.MachineName,
        Version = Program.Version,
        Capabilities = new Capabilities
        {
            Elevated = Elevation.IsElevated,
            GpuTemp = _nvml.Available,
            // Split so the tablet can draw a GPU dial on an AMD laptop while leaving out
            // the power and clock tiles, which only NVML can fill. A tile that can never
            // hold a number should not be on screen at all.
            Gpu = _nvml.Available || _gpuCounters.Available,
            GpuDetail = _nvml.Available,
            MediaSeek = _media.Available,
            Transport = transport,
            // Declared only once genuinely implemented and probed, so the tablet never
            // renders a control that cannot work.
            AudioDeviceSwitch = _audio.CanSwitchDevices,
            // Probed, not assumed: without a virtual cable there is nothing to cast into,
            // and the tablet must not offer a switch that cannot do anything.
            MicCast = _cast.Available && _audio.CanSwitchDevices,
            // Each of these is reported from an actual probe on this machine, never
            // assumed, so the tablet never renders a control it cannot drive.
            Brightness = _brightness.CanWrite,
            CpuTemp = _thermal.Available,
            ThermalMode = _power.Available,
            ThermalModeKind = _power.Available ? "powerplan" : null,
            Fps = _fps.Available,
        },
    };

    public async ValueTask DisposeAsync()
    {
        await _media.DisposeAsync().ConfigureAwait(false);
        // First: it owns the machine's default input while armed, and everything else here
        // is recoverable by restarting the agent whereas that is not.
        _recorder.Dispose();
        _soundboard.Dispose();
        _cast.Dispose();
        _audio.Dispose();
        _counters.Dispose();
        _nvml.Dispose();
        _gpuCounters.Dispose();
        _fps.Dispose();
    }
}

public static class Elevation
{
    public static bool IsElevated { get; } = Check();

    private static bool Check()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
