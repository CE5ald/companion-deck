using System.Text.Json;
using CompanionDeck.Host.Actions;
using CompanionDeck.Host.Assets;
using CompanionDeck.Host.Audio;
using CompanionDeck.Host.Clock;
using CompanionDeck.Host.Context;
using CompanionDeck.Host.Launcher;
using CompanionDeck.Host.Media;
using CompanionDeck.Host.Monitoring;
using CompanionDeck.Host.Notify;
using CompanionDeck.Host.Protocol;
using CompanionDeck.Host.Telemetry;
using CompanionDeck.Host.Tiles;
using CompanionDeck.Host.Transport;

namespace CompanionDeck.Host.Server;

/// <summary>
/// Owns the capability services and serves whichever tablet link is currently up. The central
/// rule: nothing is collected unless a connected tablet has asked for it.
/// </summary>
public sealed partial class DeckServer : IAsyncDisposable
{
    private readonly AssetCache _assets = new();
    private readonly SystemCounters _counters = new();
    private readonly Nvml _nvml = new();
    private readonly GpuCounters _gpuCounters = new();
    private readonly Brightness _brightness = new();
    private readonly LenovoEc _lenovo = new();
    private readonly ThermalZone _thermal = new();
    private readonly HardwareSensors _sensors = new();
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
    private readonly TileStore _tiles = new();
    private readonly TileRunner _runner;
    private readonly TimerService _timers = new();
    private readonly Pairing _pairing = new();
    private readonly MacroRecorder _macroRec = new();
    private readonly SpectrumService _spectrum = new();
    private readonly NotificationMirror _notify = new();
    private readonly HostSettings _settings = new();
    private readonly HashSet<long> _hiddenNotes = new();
    private string? _lastNotifyJson;
    private readonly MacroStore _macros = new();
    private readonly CalendarService _calendar = new();
    private readonly CalendarSubscriptions _calSubs;
    private Timer? _calTimer;
    private Timer? _restoreTimer;

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
    private string? _lastTilesJson;
    private string? _lastClockKey;
    private string? _lastTimersJson;
    private string? _lastMacrosJson;
    private DateTime _lastClockSent = DateTime.MinValue;
    private DateTime _lastClockEval = DateTime.MinValue;
    // What is in front on the PC, kept from the context sample so the tile profile can follow it.
    private volatile string? _fgProc;
    private volatile string _ctxMode = "desktop";
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
        _calSubs = new CalendarSubscriptions(_calendar.Folder);
        _media = new MediaService(_assets);
        _launcher = new LauncherService(_assets);
        _mediaSources = new MediaSourceService(_assets);
        _soundboard = new SoundboardService(_cast);
        _recorder = new RecorderService(_soundboard, _cast);
        _runner = new TileRunner(ExecuteStepAsync);
        _runner.Changed += () => { _lastTilesJson = null; if (_sections.Contains("tiles")) PublishTiles(); };
        _timers.Changed += () => { _lastTimersJson = null; if (_sections.Contains("timers")) PublishTimers(); };
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
        // The broad route to CPU temperature and fans on any PC. The GPU is read through it only when
        // NVIDIA's own library is not already supplying it.
        _sensors.Probe(gpuToo: !_nvml.Available);
        if (!generic) _lenovo.Probe();
        // Universal CPU temperature. Probed whether or not a vendor interface answered,
        // because the vendor one can be present and still report nothing useful.
        if (!_lenovo.HasCpuTemp) _thermal.Probe();
        // Only consulted when the machine has no vendor thermal interface, but probing is
        // cheap and knowing both lets the tablet label the control correctly.
        if (!_lenovo.HasThermalMode) _power.Probe();
        _audio.Initialize();
        // Before anything else touches audio: if the last run died while casting, the
        // machine is still recording from a cable nothing is feeding.
        _cast.RecoverIfStranded();
        _soundboard.Initialize();
        // A game is recognised by where it is installed, so Steam titles (which record no executable) and
        // launcher-stub Epic titles are both found, whatever their process happens to be called.
        _context.GameMatcher = (pid, name) => _launcher.GameForProcess(pid, name) is not null;
        _gameWatch.GameMatcher = _context.GameMatcher;
        _tiles.Load();
        _timers.Load();
        _calSubs.Load();
        // Subscribed calendars are fetched again every half hour, quietly, whether or not a tablet is looking.
        _calTimer = new Timer(_ => _ = RefreshCalendarsAsync(), null, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(30));
        _pairing.Load();
        _macros.Load();
        _settings.Load();
        StartMonitor();
        _spectrum.Frame += bands =>
        {
            var link = _link;
            if (link is null || !_sections.Contains("spectrum")) return;
            lock (_sendLock) link.SendJson(new SpectrumState { Bands = bands });
        };
        _notify.Probe();
        if (!_notify.Available) Log.Info($"notification mirror unavailable: {_notify.Problem}");
        // Pinned levels go back on applications as they start, whether or not a tablet is
        // connected; that is the whole point of pinning one. Cheap when nothing is pinned.
        _restoreTimer = new Timer(_ =>
        {
            try { _audio.RestoreRemembered(); }
            catch (Exception ex) { Log.Debug($"restore levels: {ex.Message}"); }
        }, null, 5000, 4000);
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
                SetGameWatchGames(_launcher.KnownGameExecutables);
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
        using var linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        SemaphoreSlim? wake = null;
        Task? publisher = null;
        bool claimed = false;

        // Taking over the PC is separate from connecting. A cable is trusted at once; a network
        // peer claims it only after proving it holds the pairing key, so a stranger who merely
        // connects cannot knock a working link off, or reset what it was showing.
        void Claim()
        {
            if (claimed) return;
            claimed = true;

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
            _spectrum.Stop();

            wake = new SemaphoreSlim(0, 1);
            _wake = wake;
            var w = wake;
            publisher = Task.Run(() => PublishLoopAsync(w, linkCts.Token), linkCts.Token);
        }

        link.Claim = Claim;
        link.OnMessage += (type, doc) => HandleAsync(link, type, doc);

        if (link.Authenticated) Claim();
        else
        {
            // A network peer has a few seconds to answer the challenge, and is dropped if it does not.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(8000, linkCts.Token).ConfigureAwait(false);
                    if (!link.Authenticated)
                    {
                        Log.Warn("a network peer did not authenticate in time; dropping it");
                        link.Abort();
                    }
                }
                catch (OperationCanceledException) { }
            });
        }

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
            // Whatever the tablet was recording stops with the tablet. Hooks are never left installed.
            if (claimed && ReferenceEquals(_link, link)) _macroRec.Cancel();

            if (claimed)
            {
                // Only the link that still owns the server may park it. If a newer link has
                // already taken over, the publish period and wake handle are now its, and
                // resetting them here would silence a tablet that is working perfectly.
                bool owner = ReferenceEquals(_link, link);
                if (owner)
                {
                    _periodMs = 0;
                    _spectrum.Stop();
                    TryWake();
                }
                // Bounded. This used to be an unconditional await, so a publish loop stuck on a
                // slow service - a Bluetooth audio device being torn down inside a lock, in the
                // case that found this - meant ServeAsync never returned, the adb forward was
                // never released, and the tablet could never reconnect. A wedged section is a
                // bug to fix, but it must not also cost the link.
                try
                {
                    if (publisher is not null)
                    {
                        if (await Task.WhenAny(publisher, Task.Delay(4000)).ConfigureAwait(false) != publisher)
                            Log.Warn("publish loop did not stop in time; abandoning it so the link can reconnect");
                        else await publisher.ConfigureAwait(false);
                    }
                }
                catch { }

                // Never leave an ETW session running once nobody is watching.
                if (owner) _fps.Stop();

                // Only clear the slots if they still refer to this link. A later connection may
                // already have claimed them while this one was tearing down.
                Interlocked.CompareExchange(ref _link, null, link);
                if (ReferenceEquals(_wake, wake)) _wake = null;
                wake?.Dispose();
            }

            await link.DisposeAsync().ConfigureAwait(false);
            Log.Info("link closed");
        }
    }

    private async Task HandleAsync(DeckLink link, string type, JsonDocument doc)
    {
        // A network peer is answered, and nothing more, until it has proved it holds the key.
        // The reply carries no capabilities and the peer's other messages are dropped unread.
        if (!link.Authenticated)
        {
            if (type == "hello") BeginChallenge(link);
            else if (type == "auth") await FinishChallengeAsync(link, doc).ConfigureAwait(false);
            return;
        }

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

    /// <summary>First half of the network handshake: show we hold the key, and set a challenge.</summary>
    private void BeginChallenge(DeckLink link)
    {
        link.HostNonce = Pairing.NewNonce();
        link.SendJson(new HostHello
        {
            HostName = Environment.MachineName,
            Version = Program.Version,
            Nonce = link.HostNonce,
            Proof = _pairing.HostProof(link.Hello?.Nonce ?? "", link.HostNonce),
            Auth = "required",
        });
    }

    private async Task FinishChallengeAsync(DeckLink link, JsonDocument doc)
    {
        string? proof = doc.RootElement.TryGetProperty("proof", out var p) ? p.GetString() : null;
        if (!_pairing.VerifyTablet(link.Hello?.Nonce, link.HostNonce, proof))
        {
            link.AuthFailures++;
            Log.Warn("a network peer failed authentication; dropping it");
            // Slow, so guessing is not worth trying; the key is 256 bits, so this is manners.
            await Task.Delay(1500).ConfigureAwait(false);
            link.Abort();
            return;
        }

        link.Authenticated = true;
        link.Claim?.Invoke();
        link.SendJson(new AuthResult());
        Log.Info("network tablet authenticated");

        if (link.Hello?.CachedAssets is { Count: > 0 } cached)
            _assets.MarkPeerHas(cached);
        link.SendJson(BuildHostHello(link.Transport));
        PublishMedia();
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
        if (set.Contains("tiles")) { _lastTilesJson = null; PublishTiles(); }
        if (set.Contains("clock")) { _lastClockKey = null; PublishClock(force: true); }
        if (set.Contains("timers")) { _lastTimersJson = null; PublishTimers(); }
        if (set.Contains("macros")) { _lastMacrosJson = null; PublishMacros(); }
        if (set.Contains("notifications")) { _lastNotifyJson = null; PublishNotifications(); }
        if (set.Contains("alerts")) { _lastAlertsJson = null; PublishAlerts(); }
        if (set.Contains("sessions")) { _lastSessionsJson = null; PublishSessions(); }
        if (set.Contains("top")) { _lastTopAt = 0; }
        // Showing the frame rate is one of the reasons to run the trace session.
        _tabletWantsFps = set.Contains("frames") || set.Contains("sys");
        // The audio capture exists only while the visualiser is on screen.
        if (set.Contains("spectrum")) _spectrum.Start(); else _spectrum.Stop();
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
                if (_sections.Contains("tiles")) PublishTiles();
                if (_sections.Contains("clock")) PublishClock(force: false);
                // The count of events while recording; otherwise this changes only on a command.
                if (_sections.Contains("macros") && _macroRec.Recording) PublishMacros();
                // Read only while someone is looking, and only if it was switched on.
                if (_sections.Contains("notifications") && _settings.Notifications) PublishNotifications();
                if (_sections.Contains("frames")) PublishFrames();
                if (_sections.Contains("spectrum")) _spectrum.CheckDevice();
                // Enumerating every process is heavy, so the list refreshes every two seconds at most.
                if (_sections.Contains("top") && Environment.TickCount64 - _lastTopAt >= 2000)
                {
                    _lastTopAt = Environment.TickCount64;
                    PublishTop();
                }

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
            Cpu = Math.Round(_cpuEma.Push(_latest.Cpu), 1),
            RamPercent = Math.Round(_ramEma.Push(memPct), 1),
            RamUsedMb = usedMb,
            RamTotalMb = totalMb,
            DiskPercent = _counters.ReadDiskPercent() is { } d ? Math.Round(d, 1) : null,
            NetDownKbps = down is { } dn ? Math.Round(dn, 1) : null,
            NetUpKbps = up is { } u2 ? Math.Round(u2, 1) : null,
            Battery = batt,
            Charging = charging,
        };

        s.GpuThrottle = _latest.GpuThrottle;
        s.CpuThrottle = _latest.CpuThrottle;

        if (_latest.Gpu is { } g)
        {
            s.Gpu = g.UtilPercent is { } gu ? Math.Round(_gpuEma.Push(gu), 1) : null;
            _lastGpuPercent = s.Gpu;   // context detection uses this to corroborate "gaming"
            s.GpuTemp = _latest.GpuTemp ?? g.TempC;
            s.VramUsedMb = g.VramUsedMb;
            s.VramTotalMb = g.VramTotalMb;
            s.GpuPowerW = g.PowerW is { } p ? Math.Round(p, 1) : null;
            s.GpuClockMhz = g.ClockMhz;
        }

        if (_latest.Ec is { } ec)
        {
            s.Fan1Rpm = ec.Fan1Rpm;
            s.Fan2Rpm = ec.Fan2Rpm;
            s.ThermalMode = ec.ThermalMode;
            s.ChargeMode = ec.ChargeMode;
            s.KeyboardLight = ec.KeyboardLight;
        }
        s.CpuTemp = _latest.CpuTemp;
        s.GpuTemp ??= _latest.GpuTemp;
        s.Fans = _latest.Fans.Select(f => new FanInfo { Name = f.Name, Rpm = f.Rpm, MaxRpm = f.Max }).ToList();

        // Constant for the life of the process, but it travels with every sample because
        // the tablet has no other channel for it and the payload is already deduplicated
        // upstream - an unchanged sys block is never sent twice.
        s.FanMaxRpm = _lenovo.FanMaxRpm;

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

        // Whether the frame-rate trace runs is decided by the monitor, which is the one place that knows
        // both whether a game is in front and whether anyone wants the number. This only reads it.
        //
        // Dropping a file named "fps-debug" beside the log forces tracking of whatever is in the
        // foreground, so the pipeline can be exercised without launching a game. Delete it to return
        // to normal behaviour.
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
    private int _fpsFlagPid;

    /// <summary>Checked at most once a second; this sits on the telemetry path.</summary>
    private bool FpsDebugRequested()
    {
        if ((DateTime.UtcNow - _fpsFlagCheckedAt).TotalMilliseconds < 1000) return _fpsFlagValue;
        _fpsFlagCheckedAt = DateTime.UtcNow;
        try
        {
            _fpsFlagValue = File.Exists(FpsDebugFlag);
            // The file may name a process id to measure instead of the foreground window's, which is
            // what a browser needs: its frames come from a GPU child process, not the window's owner.
            _fpsFlagPid = _fpsFlagValue && int.TryParse(File.ReadAllText(FpsDebugFlag).Trim(), out var p) ? p : 0;
        }
        catch { _fpsFlagValue = false; _fpsFlagPid = 0; }
        return _fpsFlagValue;
    }

    private int FpsDebugPid => _fpsFlagPid;

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
        _lastTilesJson = null;
        _lastClockKey = null;
        _lastTimersJson = null;
        _lastMacrosJson = null;
        _lastNotifyJson = null;
        _lastAlertsJson = null;
        _lastSessionsJson = null;
        _lastFramesJson = null;
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
        _fgProc = state.ForegroundApp;
        _ctxMode = state.Mode;

        var json = System.Text.Encoding.UTF8.GetString(Json.Serialize(state));
        if (json == _lastCtxJson) return;
        _lastCtxJson = json;

        lock (_sendLock) link.SendJson(state);
    }

    private void PublishTiles()
    {
        var link = _link;
        if (link is null) return;

        var doc = _tiles.Snapshot();
        var (active, auto) = _tiles.Resolve(_fgProc, _ctxMode);
        var err = _runner.LastError;

        var state = new TilesState
        {
            Active = active.Id,
            Auto = auto,
            Pinned = doc.Pinned,
            // Rewriting a tile is rewriting what a button does to this PC, so it is allowed
            // only over the cable, where holding the tablet is holding the link.
            CanEdit = link.Transport == "usb",
            Profiles = doc.Profiles,
            Running = _runner.Running(),
            Error = err is { } e ? new TileErrorInfo { Tile = e.tile, Message = e.message, AtMs = e.atMs } : null,
            Catalog = new TileCatalog { Sys = TileStore.SysActions, Keys = Actions.KeyNames.Palette },
        };

        var json = System.Text.Encoding.UTF8.GetString(Json.Serialize(state));
        if (json == _lastTilesJson) return;
        _lastTilesJson = json;

        lock (_sendLock) link.SendJson(state);
    }

    private void PublishClock(bool force)
    {
        var link = _link;
        if (link is null) return;

        // Events only change when a file does or an event ends, so work them out every twenty
        // seconds, not on every tick.
        if (!force && (DateTime.UtcNow - _lastClockEval).TotalSeconds < 20) return;
        _lastClockEval = DateTime.UtcNow;

        _calendar.Refresh();
        var now = DateTimeOffset.Now;
        var state = new ClockState
        {
            NowMs = now.ToUnixTimeMilliseconds(),
            UtcOffsetMin = (int)now.Offset.TotalMinutes,
            Zone = TimeZoneInfo.Local.Id,
            Events = _calendar.Upcoming(now),
            Folder = _calendar.Folder,
            CalendarFiles = _calendar.Files,
            Subscriptions = _calSubs.Info(),
            CanEdit = link.Transport == "usb",
        };

        // The time itself is left out of the comparison, or this would send every time. It is
        // re-sent every few minutes anyway so the tablet's count cannot drift far.
        string key = System.Text.Encoding.UTF8.GetString(Json.Serialize(new ClockState
        {
            UtcOffsetMin = state.UtcOffsetMin, Zone = state.Zone, Events = state.Events,
            Folder = state.Folder, CalendarFiles = state.CalendarFiles,
            Subscriptions = state.Subscriptions, CanEdit = state.CanEdit,
        }));
        if (!force && key == _lastClockKey && (DateTime.UtcNow - _lastClockSent).TotalMinutes < 5) return;
        _lastClockKey = key;
        _lastClockSent = DateTime.UtcNow;

        lock (_sendLock) link.SendJson(state);
    }

    private void PublishNotifications()
    {
        var link = _link;
        if (link is null) return;

        var state = new NotificationsState
        {
            Enabled = _settings.Notifications,
            Available = _notify.Available,
            Problem = _notify.Problem,
            CanEdit = link.Transport == "usb",
        };
        if (state.Enabled && state.Available)
        {
            HashSet<long> hidden;
            lock (_hiddenNotes) hidden = new HashSet<long>(_hiddenNotes);
            state.Items = _notify.Read(hidden);
        }

        var json = System.Text.Encoding.UTF8.GetString(Json.Serialize(state));
        if (json == _lastNotifyJson) return;
        _lastNotifyJson = json;

        lock (_sendLock) link.SendJson(state);
    }

    private void PublishMacros()
    {
        var link = _link;
        if (link is null) return;

        var state = new MacrosState
        {
            Items = _macros.Summary(),
            CanEdit = link.Transport == "usb",
            Recording = _macroRec.Recording
                ? new MacroRecording { Count = _macroRec.Count, Seconds = Math.Round(_macroRec.ElapsedMs / 1000.0, 1), Full = _macroRec.Full }
                : null,
        };
        var json = System.Text.Encoding.UTF8.GetString(Json.Serialize(state));
        if (json == _lastMacrosJson) return;
        _lastMacrosJson = json;

        lock (_sendLock) link.SendJson(state);
    }

    private void PublishTimers()
    {
        var link = _link;
        if (link is null) return;

        var state = _timers.Snapshot();
        string key = System.Text.Encoding.UTF8.GetString(Json.Serialize(state.Items));
        if (key == _lastTimersJson) return;
        _lastTimersJson = key;

        lock (_sendLock) link.SendJson(state);
    }

    // The endpoint list is COM enumeration, and this section publishes eight times a second
    // for the meters. Refreshed on its own slower schedule instead.
    private List<AudioDeviceInfo> _castOutputs = new();
    private List<AudioDeviceInfo> _castInputs = new();
    private DateTime _castDevicesAt = DateTime.MinValue;

    private int _castRefreshing;

    private async Task RefreshCalendarsAsync()
    {
        try
        {
            await _calSubs.RefreshAllAsync().ConfigureAwait(false);
            _calendar.Refresh();
            _lastClockKey = null;
            if (_sections.Contains("clock")) PublishClock(force: true);
        }
        catch (Exception ex) { Log.Debug($"calendar refresh: {ex.Message}"); }
    }

    private void PublishCast()
    {
        var link = _link;
        if (link is null) return;

        // Listing endpoints can take seconds when a Bluetooth device is paired, so it happens off the
        // publish loop: done inline it held up every other section and the reply to the command that
        // had just been sent (arming the cast answered four seconds late). What was found last time
        // is shown meanwhile, and the next publish picks up the new list.
        if ((DateTime.UtcNow - _castDevicesAt).TotalSeconds >= 2 && Interlocked.Exchange(ref _castRefreshing, 1) == 0)
        {
            _castDevicesAt = DateTime.UtcNow;
            _ = Task.Run(() =>
            {
                try
                {
                    _castOutputs = _cast.ListDevices(render: true);
                    _castInputs = _cast.ListDevices(render: false);
                }
                catch (Exception ex) { Log.Debug($"cast device list: {ex.Message}"); }
                finally { Interlocked.Exchange(ref _castRefreshing, 0); }
            });
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

        var state = _launcher.Snapshot(id => _playtime.Get(id) is { Seconds: >= 60 } p ? p.LastMs : null);
        foreach (var item in state.Items)
        {
            if (_playtime.Get(item.Id) is { Seconds: >= 60 } p)
            {
                item.PlayMinutes = (int)(p.Seconds / 60);
                item.LastPlayedMs = p.LastMs;
            }
        }
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
                foreach (var key in new[] { item.IconKey, item.ViaIconKey })
                {
                    if (key is not null && _assets.PeerNeeds(key) && _assets.Get(key) is { } bytes)
                    {
                        link.SendAsset(key, bytes);
                        _assets.MarkPeerHas(key);
                    }
                }
            }
            link.SendJson(state);
        }
    }

    private int _rescanning;

    /// <summary>Scans the installed programs again, once at a time, and republishes what changed.</summary>
    private void RescanLauncher()
    {
        if (Interlocked.Exchange(ref _rescanning, 1) == 1) return;
        try
        {
            _launcher.Scan();
            _context.SetKnownGames(_launcher.KnownGameExecutables);
            SetGameWatchGames(_launcher.KnownGameExecutables);
            _mediaSources.Scan(_launcher);
            _lastAppsJson = null; _lastSourcesJson = null;
            if (_sections.Contains("mediaSources")) PublishMediaSources();
            if (_sections.Contains("apps")) PublishApps();
        }
        catch (Exception ex) { Log.Warn($"launcher rescan failed: {ex.Message}"); }
        finally { Interlocked.Exchange(ref _rescanning, 0); }
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
                    case "folder":
                        ok = _soundboard.SetFolder(sbId, Str(args, "folder"));
                        if (!ok) error = "that folder name is not allowed";
                        break;
                    case "color":
                        ok = _soundboard.SetColor(sbId, Str(args, "color"));
                        if (!ok) error = "unknown colour";
                        break;
                    case "loop":
                        ok = _soundboard.SetLoop(sbId, Num(args, "value", 0) != 0);
                        if (!ok) error = "unknown clip";
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
                string? why = null;
                ok = force ? _running.Kill(pid, out why) : _running.Close(pid);
                if (!ok) error = force
                    ? why ?? "that process could not be ended"
                    : "the app did not accept a close request (it may have no window)";

                // Reflect the change on the next tick rather than immediately: an app given
                // WM_CLOSE has not gone yet, and may put up a save prompt and stay.
                _lastRunningJson = null;
                _lastRunningScan = DateTime.MinValue;
                TryWake();
            }
            else if (action == "sys.keyboardLight")
            {
                int level = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("level", out var kl)
                    && kl.TryGetInt32(out var lv) ? lv : -1;
                ok = _lenovo.SetKeyboardLight(level);
                if (!ok) error = _lenovo.HasKeyboardLight
                    ? "the firmware refused that backlight level"
                    : "keyboard backlight needs the elevated agent";
                if (ok && _sections.Contains("sys")) PublishSys();
            }
            else if (action == "sys.thermalMode")
            {
                string mode = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("mode", out var tm)
                    ? tm.GetString() ?? "" : "";
                // One command, two implementations: the vendor EC where it exists, Windows power
                // modes everywhere else. The automatic rule goes through the same method.
                ok = TrySetPerformanceMode(mode, out error);
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
                    if (ok) { _launcher.Prefs.RecordUse(entry.Id); _lastAppsJson = null; }
                    else
                    {
                        error = "launch failed";
                        // A shortcut can outlive its program. Look again, so a dead entry leaves the list
                        // instead of failing every time it is tapped.
                        _ = Task.Run(RescanLauncher);
                    }
                }
            }
            else if (action == "tile.run")
            {
                string tileId = Str(args, "id");
                string? profileId = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("profile", out var pe2)
                    ? pe2.GetString() : null;

                // The tablet names a tile; what it does is read from the layout kept here.
                var tile = _tiles.FindTile(profileId, tileId, _fgProc, _ctxMode);
                if (tile is null) error = "unknown tile";
                else
                {
                    ok = _runner.Toggle(tile.Id, tile.Name, tile.Steps);
                    if (!ok) error = "too many tiles are running";
                }
            }
            else if (action == "tiles.setProfile")
            {
                if (link.Transport != "usb") error = "layouts can only be changed over the cable";
                else if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("profile", out var pr)) error = "no profile";
                else
                {
                    ok = _tiles.SetProfile(pr, out error);
                    if (ok) { _lastTilesJson = null; PublishTiles(); }
                }
            }
            else if (action == "tiles.deleteProfile")
            {
                if (link.Transport != "usb") error = "layouts can only be changed over the cable";
                else
                {
                    ok = _tiles.DeleteProfile(Str(args, "id"), out error);
                    if (ok) { _lastTilesJson = null; PublishTiles(); }
                }
            }
            else if (action == "tiles.pin")
            {
                // Choosing which stored layout to look at changes nothing on the PC, so it is
                // allowed on any link.
                string pin = Str(args, "id");
                ok = _tiles.Pin(pin.Length == 0 ? null : pin, out error);
                if (ok) { _lastTilesJson = null; PublishTiles(); }
            }
            else if (action.StartsWith("macro.", StringComparison.Ordinal))
            {
                ok = HandleMacroCommand(link, action["macro.".Length..], args, out error);
                if (ok) { _lastMacrosJson = null; PublishMacros(); }
            }
            else if (action is "history.get" or "alerts.set" or "sessions.enable" or "sessions.delete" or "sessions.clear"
                     or "autopower.set" or "top.close" or "top.kill" or "top.priority")
            {
                ok = HandleMonitorCommand(link, action, args, out error);
            }
            else if (action.StartsWith("notify.", StringComparison.Ordinal))
            {
                ok = HandleNotifyCommand(link, action["notify.".Length..], args, out error);
                if (ok) { _lastNotifyJson = null; PublishNotifications(); }
            }
            else if (action == "pair.reset")
            {
                if (link.Transport != "usb") error = "tablets can only be unpaired over the cable";
                else
                {
                    _pairing.Rotate();
                    // The tablet on the cable takes the new key from this hello, so it stays paired.
                    link.SendJson(BuildHostHello(link.Transport));
                    ok = true;
                }
            }
            else if (action.StartsWith("calendar.", StringComparison.Ordinal))
            {
                // A calendar link is a secret and adding one makes this PC fetch from the internet, so,
                // like the other things that reach outside the PC, it is the cable's to change.
                if (link.Transport != "usb") { error = "calendars can only be changed over the cable"; ok = false; }
                else
                {
                    switch (action["calendar.".Length..])
                    {
                        case "add":
                            (ok, error) = await _calSubs.AddAsync(Str(args, "url")).ConfigureAwait(false);
                            break;
                        case "paste":
                        {
                            // Whatever was just copied on the PC. The content is never echoed back, in
                            // the reply or the log: if it is not a calendar link it is only said to be wrong.
                            var text = CalendarSubscriptions.ClipboardText();
                            if (string.IsNullOrWhiteSpace(text)) { ok = false; error = "the PC's clipboard has no text"; }
                            else (ok, error) = await _calSubs.AddAsync(text.Trim().Split('\n')[0]).ConfigureAwait(false);
                            if (!ok && error == "that is not an https calendar link") error = "the PC's clipboard does not hold a calendar link";
                            break;
                        }
                        case "remove":
                            ok = _calSubs.Remove(Str(args, "id"));
                            if (!ok) error = "unknown calendar";
                            break;
                        case "refresh":
                            await RefreshCalendarsAsync().ConfigureAwait(false);
                            ok = true;
                            break;
                        default: ok = false; error = "unknown calendar command"; break;
                    }
                    if (ok) { _calendar.Refresh(); _lastClockKey = null; _lastClockEval = DateTime.MinValue; PublishClock(force: true); }
                }
            }
            else if (action.StartsWith("timer.", StringComparison.Ordinal))
            {
                ok = HandleTimerCommand(action["timer.".Length..], args, out error);
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
            "micMute" => value >= 0 && _audio.SetMicMute(value != 0),
            "masterMute" => value >= 0 && _audio.SetMasterMute(value != 0),
            "deafen" => value >= 0 && _audio.SetDeafened(value != 0),
            "deafenToggle" => _audio.ToggleDeafened(),
            "deviceVolume" => id.Length > 0 && value >= 0 && _audio.SetDeviceVolume(id, value),
            "rememberApp" => id.Length > 0 && value >= 0 && _audio.RememberApp(id, value != 0),
            _ => false,
        };
    }

    private static string Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? "" : "";

    private static long Num(JsonElement args, string name, long fallback) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var e) && e.TryGetInt64(out var v)
            ? v : fallback;

    private bool HandleNotifyCommand(DeckLink link, string verb, JsonElement args, out string? error)
    {
        error = null;
        switch (verb)
        {
            case "enable":
                // What other applications say to you is read only on the word of the cable.
                if (link.Transport != "usb") { error = "notifications can only be switched on over the cable"; return false; }
                bool on = Num(args, "value", 0) != 0;
                if (on && !_notify.Available) { error = _notify.Problem ?? "not available on this PC"; return false; }
                _settings.SetNotifications(on);
                if (!on) lock (_hiddenNotes) _hiddenNotes.Clear();
                Log.Info($"notification mirror {(on ? "switched on" : "switched off")}");
                return true;

            // Hiding is for the tablet only. Windows keeps its own copy, and nothing here can remove it.
            case "dismiss":
                lock (_hiddenNotes) { if (_hiddenNotes.Count > 500) _hiddenNotes.Clear(); _hiddenNotes.Add(Num(args, "id", -1)); }
                return true;

            case "clear":
                foreach (var n in _notify.Read(new HashSet<long>()))
                    lock (_hiddenNotes) _hiddenNotes.Add(n.Id);
                return true;

            default:
                error = $"unknown notification command '{verb}'";
                return false;
        }
    }

    private bool HandleMacroCommand(DeckLink link, string verb, JsonElement args, out string? error)
    {
        error = null;

        // Capturing everything typed on this PC is done only on the word of the cable.
        if (verb != "cancel" && link.Transport != "usb") { error = "macros can only be recorded over the cable"; return false; }

        switch (verb)
        {
            case "record":
                return _macroRec.Start(out error);

            case "stop":
            {
                var events = _macroRec.Stop();
                if (events is null) { error = "nothing is being recorded"; return false; }
                return _macros.Add(Str(args, "name"), events, out error);
            }

            case "cancel":
                _macroRec.Cancel();
                return true;

            case "delete":
                if (_macros.Delete(Str(args, "id"))) return true;
                error = "unknown macro";
                return false;

            default:
                error = $"unknown macro command '{verb}'";
                return false;
        }
    }

    private bool HandleTimerCommand(string verb, JsonElement args, out string? error)
    {
        error = null;
        string id = Str(args, "id");
        string label = Str(args, "label");

        bool ok;
        switch (verb)
        {
            case "countdown": ok = _timers.StartCountdown(Num(args, "seconds", 0), label); break;
            case "stopwatch": ok = _timers.StartStopwatch(label); break;
            case "pomodoro":
                ok = _timers.StartPomodoro((int)Num(args, "work", 25), (int)Num(args, "short", 5),
                                           (int)Num(args, "long", 15), (int)Num(args, "rounds", 4), label);
                break;
            case "pause": ok = _timers.Pause(id); break;
            case "resume": ok = _timers.Resume(id); break;
            case "remove": ok = _timers.Remove(id); break;
            case "skip": ok = _timers.Skip(id); break;
            case "clear": _timers.Clear(); ok = true; break;
            default: error = $"unknown timer command '{verb}'"; return false;
        }
        if (!ok) error = "that timer command was refused";
        return ok;
    }

    /// <summary>
    /// Performs one step of a tile. Everything here is looked up on this machine at the moment it
    /// runs: a launcher entry that has since been uninstalled, or a clip that has been deleted,
    /// stops the tile with a message instead of doing something else.
    /// </summary>
    private async Task<string?> ExecuteStepAsync(TileStep s, CancellationToken ct)
    {
        switch (s.Do)
        {
            case "wait":
                await Task.Delay(s.Ms ?? 0, ct).ConfigureAwait(false);
                return null;

            case "sys":
                return await Task.Run(() => _actions.Execute(s.Id ?? ""), ct).ConfigureAwait(false)
                    ? null : $"'{s.Id}' did not run";

            case "key":
                if (!KeyNames.TryParse(s.Keys, out var keys)) return "that shortcut is not valid";
                return await Task.Run(() => _actions.Press(keys), ct).ConfigureAwait(false)
                    ? null : "the shortcut was refused; an elevated window may have focus";

            case "launch":
            {
                var entry = _launcher.Find(s.Id ?? "");
                if (entry is null) return "that app is no longer in the launcher";
                return await Task.Run(() => _actions.Launch(entry.Target), ct).ConfigureAwait(false)
                    ? null : $"could not start {entry.Name}";
            }

            case "audio":
            {
                bool ok = s.Verb switch
                {
                    "masterMuteToggle" => _audio.ToggleMasterMute(),
                    "micMuteToggle" => _audio.ToggleMicMute(),
                    "deafenToggle" => _audio.ToggleDeafened(),
                    "masterVolume" => _audio.SetMasterVolume(s.Value ?? 0),
                    "setOutput" => _audio.Snapshot().Outputs.Any(o => o.Id == s.Id) && _audio.SetDefaultEndpoint(s.Id!),
                    _ => false,
                };
                if (ok) { _lastAudioJson = null; if (_sections.Contains("audio")) PublishAudio(); }
                return ok ? null : s.Verb == "setOutput" ? "that output device is not connected" : "the audio change was refused";
            }

            case "media":
                return await _media.ControlAsync("", s.Verb ?? "", null).ConfigureAwait(false)
                    ? null : "nothing is playing";

            case "sound":
                if (s.Verb == "stop") { _soundboard.StopAll(); return null; }
                return _soundboard.Play(s.Id ?? "") ? null : "that clip could not be played";

            case "macro":
            {
                var macro = _macros.Find(s.Id ?? "");
                if (macro is null) return "that macro was deleted";
                return await MacroPlayer.PlayAsync(macro.Events, _actions, ct).ConfigureAwait(false);
            }

            case "timer":
                return s.Verb switch
                {
                    "countdown" => _timers.StartCountdown(s.Value ?? 0, null) ? null : "no room for another timer",
                    "stopwatch" => _timers.StartStopwatch(null) ? null : "no room for another timer",
                    "pomodoro" => _timers.StartPomodoro(25, 5, 15, 4, null) ? null : "no room for another timer",
                    "clear" => ClearTimers(),
                    _ => "unknown timer step",
                };

            default:
                return "unknown step";
        }
    }

    private string? ClearTimers() { _timers.Clear(); return null; }


    private HostHello BuildHostHello(string transport) => new()
    {
        HostName = Environment.MachineName,
        Version = Program.Version,
        // Over the cable, and only there, the tablet is given the key that lets it use Wi-Fi later.
        Pair = transport == "usb" ? _pairing.KeyBase64 : null,
        Capabilities = new Capabilities
        {
            Elevated = Elevation.IsElevated,
            GpuTemp = _nvml.Available || _sensors.HasGpuTemp,
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
            CpuTemp = _lenovo.HasCpuTemp || _thermal.Available || _sensors.HasCpuTemp,
            Fans = _lenovo.HasFans || _sensors.HasFans,
            SensorsNote = _sensors.Available || _lenovo.HasCpuTemp ? null
                : _lenovo.NeedsElevation ? "needs the elevated agent" : _sensors.Problem,
            ThermalMode = _lenovo.HasThermalMode || _power.Available,
            // Lets the tablet title the card for what it is actually driving, rather than
            // calling a Windows power scheme a vendor thermal profile.
            ThermalModeKind = _lenovo.HasThermalMode ? "vendor" : _power.Available ? "powerplan" : null,
            ChargeMode = _lenovo.HasChargeMode,
            KeyboardLight = _lenovo.HasKeyboardLight,
            Fps = _fps.Available,
            FpsNote = _fps.Available ? null : _fps.Detail,
            GameShortcuts = _actions.AvailableGameShortcuts(_nvml.Available),
            VendorName = _lenovo.HasThermalMode ? _lenovo.Brand : null,
        },
    };

    public async ValueTask DisposeAsync()
    {
        _restoreTimer?.Dispose();
        _calTimer?.Dispose();
        _spectrum.Dispose();
        StopMonitor();
        _macroRec.Dispose();
        _runner.CancelAll();
        _timers.Dispose();
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
