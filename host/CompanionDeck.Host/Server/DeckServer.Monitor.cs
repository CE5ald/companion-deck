using System.Text.Json;
using CompanionDeck.Host.Context;
using CompanionDeck.Host.Monitoring;
using CompanionDeck.Host.Protocol;
using CompanionDeck.Host.Telemetry;
using CompanionDeck.Host.Transport;

namespace CompanionDeck.Host.Server;

/// <summary>
/// The part of the agent that keeps watching when nobody is looking: a once-a-second reading that
/// feeds the history graphs, the alerts, the gaming-session record, the playtime counter and the
/// automatic performance mode. It is also the single reader of the counters that work by difference
/// (CPU time, GPU engine time), so two callers can never steal each other's interval.
/// </summary>
public sealed partial class DeckServer
{
    private readonly HistoryBuffer _history = new();
    private readonly AlertEngine _alertEngine = new();
    private readonly SessionRecorder _sessions = new();
    private readonly Playtime _playtime = new();
    private readonly AutoPower _autoPower = new();
    // Its own copy, so what the monitor samples never disturbs the dashboard's debounce.
    private readonly ContextService _gameWatch = new();
    private readonly TopProcesses _top = new(RunningAppsService.IsProtectedProcess);

    private string? _lastBrowserKey;
    private Timer? _monitorTimer;
    private int _monitorBusy;
    private volatile MonitorLatest _latest = new();
    private volatile bool _tabletWantsFps;
    private string? _lastAlertsJson, _lastSessionsJson;
    private long _lastTopAt;

    /// <summary>
    /// Process names to treat as games for the monitor only. This exists so sessions, playtime and the
    /// automatic power rule can be tested on a real machine without starting a real game:
    /// COMPANIONDECK_TEST_GAME=notepad makes Notepad a "game".
    /// </summary>
    private static readonly string[] TestGames =
        (Environment.GetEnvironmentVariable("COMPANIONDECK_TEST_GAME") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static readonly string TestGamesFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CompanionDeck", "test-games.txt");

    private IEnumerable<string> _launcherGames = Array.Empty<string>();
    private DateTime _testGamesCheckedAt;
    private string _testGamesSeen = "";

    private void SetGameWatchGames(IEnumerable<string> known)
    {
        _launcherGames = known.ToList();
        _gameWatch.SetKnownGames(_launcherGames.Concat(TestGames).Concat(ReadTestGames()));
    }

    /// <summary>
    /// A file named test-games.txt beside the log, one process name per line, makes those processes count
    /// as games for the monitor. Like the fps-debug flag, it lets the whole path be exercised on a real
    /// machine, including by the elevated agent, without starting a real game. Delete it to stop.
    /// </summary>
    private IEnumerable<string> ReadTestGames()
    {
        try
        {
            return File.Exists(TestGamesFile)
                ? File.ReadAllLines(TestGamesFile).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList()
                : Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    private void RefreshTestGames()
    {
        if ((DateTime.UtcNow - _testGamesCheckedAt).TotalSeconds < 3) return;
        _testGamesCheckedAt = DateTime.UtcNow;
        var names = ReadTestGames().ToList();
        string seen = string.Join("|", names);
        if (seen == _testGamesSeen) return;
        _testGamesSeen = seen;
        _gameWatch.SetKnownGames(_launcherGames.Concat(TestGames).Concat(names));
        if (names.Count > 0) Log.Info($"test games in effect: {string.Join(", ", names)}");
    }

    /// <summary>The newest reading, for the dashboard to show instead of reading the counters a second time.</summary>
    private sealed class MonitorLatest
    {
        public double Cpu;
        public GpuSample? Gpu;
        public string? GpuThrottle, CpuThrottle;
        public LenovoEc.Reading? Ec;
        public double? CpuTemp, GpuTemp;
        public List<FanReading> Fans = new();
    }

    private sealed record FanReading(string Name, int Rpm, int? Max);

    /// <summary>
    /// Every fan from whichever sources this PC has. The vendor interface wins where it exists, because it
    /// knows the rated speed; the sensor chips fill in everything else, so a desktop's six headers or a
    /// laptop's single fan appear without anyone telling the app how many there are.
    /// </summary>
    private List<FanReading> MergeFans(LenovoEc.Reading? ec, HardwareSensors.Reading? hw)
    {
        var list = new List<FanReading>();
        if (ec?.Fan1Rpm is { } f1) list.Add(new FanReading("Fan 1", f1, _lenovo.FanMaxRpm));
        if (ec?.Fan2Rpm is { } f2) list.Add(new FanReading("Fan 2", f2, _lenovo.FanMaxRpm));
        if (list.Count == 0 && hw is not null)
            foreach (var f in hw.Fans.Take(8)) list.Add(new FanReading(f.Name, f.Rpm, null));
        return list;
    }

    private void StartMonitor()
    {
        if (Environment.GetEnvironmentVariable("COMPANIONDECK_NO_MONITOR") == "1")
        {
            Log.Info("background monitoring disabled by COMPANIONDECK_NO_MONITOR");
            return;
        }

        _sessions.Load();
        _playtime.Load();
        SetGameWatchGames(Array.Empty<string>());
        _fps.Frame += ms => _sessions.AddFrame(ms);
        _sessions.Changed += () => { _lastSessionsJson = null; if (_sections.Contains("sessions")) PublishSessions(); };
        _monitorTimer = new Timer(_ => MonitorTick(), null, 1500, 1000);
    }

    private void StopMonitor()
    {
        _monitorTimer?.Dispose();
        _monitorTimer = null;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try { _sessions.Flush(now); _playtime.Flush(); } catch { }
        _top.Dispose();
    }

    private void MonitorTick()
    {
        if (Interlocked.Exchange(ref _monitorBusy, 1) == 1) return;
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // A changed default browser shows up on the media sources within the lookup's 30 s cache.
            string browserNow = _mediaSources.BrowserKey;
            if (browserNow != _lastBrowserKey)
            {
                bool first = _lastBrowserKey is null;
                _lastBrowserKey = browserNow;
                if (!first)
                {
                    Log.Info($"default browser is now {(browserNow.Length > 0 ? Path.GetFileNameWithoutExtension(browserNow) : "none")}");
                    _lastSourcesJson = null;
                    if (_sections.Contains("mediaSources")) PublishMediaSources();
                }
            }

            double cpu = _counters.ReadCpuPercent();
            var (ramPct, _, _) = _counters.ReadMemory();
            var gpu = _nvml.Sample() ?? _gpuCounters.Sample();
            double? gpuUtil = gpu?.UtilPercent, gpuTemp = gpu?.TempC;
            string? gpuThrottle = _nvml.Available ? _nvml.ThrottleReason() : null;

            // Each source is read once here and shared; the dashboard publishes this reading rather than
            // asking the hardware a second time. Order: the vendor's own, then the sensor chips, then ACPI.
            var ec = _lenovo.Sample();
            var hw = _sensors.Available ? _sensors.Read() : null;
            double? cpuTemp = ec?.CpuTempC ?? hw?.CpuTempC;
            cpuTemp ??= _thermal.Available ? _thermal.Read() : null;
            gpuTemp ??= hw?.GpuTempC;
            var fans = MergeFans(ec, hw);
            double? fanRpm = fans.Count > 0 ? fans.Max(f => f.Rpm) : null;

            bool onAc = _counters.ReadBattery().charging ?? true;

            // A CPU asked for a lot that is running well below its rated clock is being held back. Windows
            // cannot say why, so the reason is a judgement from what else is true, and worded as one.
            string? cpuThrottle = null;
            if (cpu >= 70 && _counters.ReadCpuClockPercent() is < 60)
                cpuThrottle = !onAc ? "battery" : cpuTemp >= 88 ? "heat" : "power";

            // What is in front, and whether it is a game.
            RefreshTestGames();
            var ctx = _gameWatch.Sample(gpuUtil, false, null);
            bool gaming = ctx.Mode == "gaming";
            var entry = gaming && ctx.ForegroundApp is { } fg
                ? _launcher.GameForProcess(_gameWatch.ForegroundPid, fg)
                : null;
            string? game = gaming ? (entry?.Name ?? ctx.ForegroundApp) : null;

            // Frame rate costs a trace session, so it runs only for a game in front, and only if a record
            // is being kept or the tablet is showing it. Nothing else may start or stop it.
            bool wantFps = (gaming || FpsDebugRequested()) && (_settings.Sessions || _tabletWantsFps || FpsDebugRequested());
            _fps.Track(wantFps ? (FpsDebugRequested() && FpsDebugPid > 0 ? FpsDebugPid
                                : _gameWatch.GamePid > 0 ? _gameWatch.GamePid : _gameWatch.ForegroundPid) : 0);
            var fpsReading = _fps.Sample();
            double? fps = fpsReading?.Fps;
            if (FpsDebugRequested())
                Log.Info($"fps-debug: pid {_fps.TrackedPid} state {_fps.State} fps {fpsReading?.Fps.ToString("F1") ?? "-"} low {fpsReading?.OnePercentLow.ToString("F1") ?? "-"} {_fps.Detail}");

            _latest = new MonitorLatest
            {
                Cpu = cpu, Gpu = gpu, GpuThrottle = gpuThrottle, CpuThrottle = cpuThrottle,
                Ec = ec, CpuTemp = cpuTemp, GpuTemp = gpuTemp, Fans = fans,
            };

            _history.Add(new Reading(now, cpu, gpuUtil ?? double.NaN, ramPct,
                                     cpuTemp ?? double.NaN, gpuTemp ?? double.NaN, fps ?? double.NaN));

            // Alerts.
            var fired = _alertEngine.Evaluate(_settings.Alerts, now, cpuTemp, gpuTemp, ramPct, fanRpm);
            foreach (var a in fired)
            {
                Log.Info($"alert: {a.Label} {a.Value} (limit {a.Limit})");
                var link = _link;
                if (link is not null)
                    lock (_sendLock)
                        link.SendJson(new AlertMessage { Metric = a.Metric, Label = a.Label, Value = a.Value, Limit = a.Limit, AtMs = a.AtMs });
            }
            if (fired.Count > 0) { _lastAlertsJson = null; if (_sections.Contains("alerts")) PublishAlerts(); }

            // Sessions and playtime.
            if (_settings.Sessions) _sessions.Tick(now, game, fps, cpu, gpuUtil, gpuTemp);
            else _sessions.Flush(now);
            if (entry is not null) _playtime.Add(entry.Id, 1, now);

            // Performance mode follows the game, if asked to.
            var (step, mode) = _autoPower.Tick(_settings.AutoPower, "performance", gaming, onAc, CurrentPerformanceMode);
            if (step != PowerStep.None && mode is not null)
            {
                bool done = TrySetPerformanceMode(mode, out var why);
                if (!done && step == PowerStep.Apply) _autoPower.Refused();
                Log.Info($"auto power: {(step == PowerStep.Apply ? "game started" : "game over")}, mode {mode} -> {(done ? "ok" : why)}");
                if (done) { _lastSessionsJson = null; if (_sections.Contains("sessions")) PublishSessions(); }
            }
        }
        catch (Exception ex) { Log.Debug($"monitor tick: {ex.Message}"); }
        finally { Interlocked.Exchange(ref _monitorBusy, 0); }
    }

    /// <summary>The one place a performance mode is set, for the tablet and for the automatic rule alike.</summary>
    private bool TrySetPerformanceMode(string mode, out string? error)
    {
        error = null;
        if (_lenovo.HasThermalMode)
        {
            if (_lenovo.SetThermalMode(mode)) return true;
            error = "the firmware refused that mode";
            return false;
        }
        if (_power.Available)
        {
            if (_power.SetMode(mode)) { _lastPowerRead = DateTime.MinValue; return true; }
            error = "Windows refused that power scheme";
            return false;
        }
        error = "no performance control on this machine";
        return false;
    }

    private string? CurrentPerformanceMode()
    {
        if (_lenovo.HasThermalMode) return _lenovo.CurrentThermalMode();
        return _power.Available ? _power.CurrentMode() : null;
    }

    // ------------------------------------------------------------------ publishing

    private void PublishAlerts()
    {
        var link = _link;
        if (link is null) return;

        var cfg = _settings.Alerts;
        var state = new AlertsState
        {
            Enabled = cfg.Enabled, CpuTemp = cfg.CpuTemp, GpuTemp = cfg.GpuTemp, Ram = cfg.Ram, Fan = cfg.Fan,
            CpuTempAvailable = _thermal.Available || _lenovo.HasCpuTemp || _sensors.HasCpuTemp,
            GpuTempAvailable = _nvml.Available || _sensors.HasGpuTemp,
            FanAvailable = _lenovo.HasFans || _sensors.HasFans,
            Active = _alertEngine.Active.ToList(),
        };
        var json = System.Text.Encoding.UTF8.GetString(Json.Serialize(state));
        if (json == _lastAlertsJson) return;
        _lastAlertsJson = json;
        lock (_sendLock) link.SendJson(state);
    }

    private void PublishSessions()
    {
        var link = _link;
        if (link is null) return;

        var state = new SessionsState
        {
            Enabled = _settings.Sessions,
            FpsAvailable = _fps.Available,
            Items = _sessions.Items,
            AutoPower = _settings.AutoPower,
            AutoPowerApplied = _autoPower.Applied,
            AutoPowerAvailable = CanSetPerformanceMode(),
            CanEdit = link.Transport == "usb",
        };
        var json = System.Text.Encoding.UTF8.GetString(Json.Serialize(state));
        if (json == _lastSessionsJson) return;
        _lastSessionsJson = json;
        lock (_sendLock) link.SendJson(state);
    }

    private bool CanSetPerformanceMode() =>
        _power.Available
        || _lenovo.HasThermalMode
        ;

    private void PublishFrames()
    {
        var link = _link;
        if (link is null) return;
        var times = _fps.RecentFrameTimes(120);
        var state = new FramesState
        {
            Times = times.Select(t => Math.Round(t, 1)).ToArray(),
            Fps = _fps.Sample()?.Fps,
            Live = _sessions.Live,
        };
        // A page with no game on it sends one empty frame and then nothing.
        string json = System.Text.Encoding.UTF8.GetString(Json.Serialize(state));
        if (times.Length == 0 && state.Live is null && json == _lastFramesJson) return;
        _lastFramesJson = json;
        lock (_sendLock) link.SendJson(state);
    }

    private string? _lastFramesJson;

    private void PublishTop()
    {
        var link = _link;
        if (link is null) return;

        var state = _top.Snapshot(8);
        // Only what is shown here may be acted on from the tablet.
        _running.OfferNames(state.Cpu.Concat(state.Ram).Concat(state.Gpu).Where(p => !p.Protected).Select(p => p.Name));
        lock (_sendLock) link.SendJson(state);
    }

    // ------------------------------------------------------------------ commands

    private bool HandleMonitorCommand(DeckLink link, string action, JsonElement args, out string? error)
    {
        error = null;
        switch (action)
        {
            case "history.get":
            {
                int seconds = (int)Math.Clamp(Num(args, "seconds", 60), 10, HistoryBuffer.Capacity);
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var data = _history.Query(seconds, 120, now);
                var state = new HistoryState
                {
                    Seconds = seconds, NowMs = now, StartMs = data.StartMs, StepMs = data.StepMs,
                    Cpu = data.Cpu, Gpu = data.Gpu, Ram = data.Ram, CpuTemp = data.CpuTemp, GpuTemp = data.GpuTemp, Fps = data.Fps,
                };
                lock (_sendLock) link.SendJson(state);
                return true;
            }

            case "alerts.set":
            {
                var cfg = _settings.Alerts;
                if (args.ValueKind == JsonValueKind.Object)
                {
                    if (args.TryGetProperty("enabled", out var e)) cfg.Enabled = e.ValueKind == JsonValueKind.True || (e.TryGetInt32(out var ei) && ei != 0);
                    if (args.TryGetProperty("cpuTemp", out var a) && a.TryGetInt32(out var av)) cfg.CpuTemp = av;
                    if (args.TryGetProperty("gpuTemp", out var b) && b.TryGetInt32(out var bv)) cfg.GpuTemp = bv;
                    if (args.TryGetProperty("ram", out var c) && c.TryGetInt32(out var cv)) cfg.Ram = cv;
                    if (args.TryGetProperty("fan", out var d) && d.TryGetInt32(out var dv)) cfg.Fan = dv;
                }
                _settings.SetAlerts(cfg);
                _lastAlertsJson = null;
                PublishAlerts();
                return true;
            }

            case "sessions.enable":
                // What it records is only the name of a game and a few numbers about it, but it does start
                // a trace session while a game is in front, so it is switched from the cable.
                if (link.Transport != "usb") { error = "this can only be changed over the cable"; return false; }
                _settings.SetSessions(Num(args, "value", 0) != 0);
                _lastSessionsJson = null; PublishSessions();
                return true;

            case "sessions.delete":
                if (link.Transport != "usb") { error = "this can only be changed over the cable"; return false; }
                if (!_sessions.Delete(Str(args, "id"))) { error = "unknown session"; return false; }
                return true;

            case "sessions.clear":
                if (link.Transport != "usb") { error = "this can only be changed over the cable"; return false; }
                _sessions.Clear();
                return true;

            case "autopower.set":
                // A rule that changes how the PC behaves by itself belongs to the cable.
                if (link.Transport != "usb") { error = "this can only be changed over the cable"; return false; }
                if (Num(args, "value", 0) != 0 && !CanSetPerformanceMode()) { error = "this PC has no performance control"; return false; }
                _settings.SetAutoPower(Num(args, "value", 0) != 0);
                _lastSessionsJson = null; PublishSessions();
                return true;

            case "top.close":
            {
                int n = _running.CloseByName(Str(args, "name"));
                if (n == 0) { error = "that application has no window to ask"; return false; }
                return true;
            }
            case "top.kill":
            {
                int n = _running.KillByName(Str(args, "name"), out var why);
                if (n == 0) { error = why ?? "that application could not be ended"; return false; }
                return true;
            }
            case "top.priority":
            {
                int n = _running.SetPriorityByName(Str(args, "name"), Num(args, "low", 1) != 0);
                if (n == 0) { error = "its priority could not be changed"; return false; }
                _lastTopAt = 0;
                return true;
            }

            default:
                error = $"unknown command '{action}'";
                return false;
        }
    }
}
