using System.Diagnostics;
using System.Globalization;

namespace CompanionDeck.Host.Telemetry;

/// <summary>
/// Frame rate for the running game, read from PresentMon's CSV output. PresentMon is not bundled
/// - it is an external tool this looks for, and FPS is simply unavailable when none is installed.
/// </summary>
public sealed class FpsProvider : IDisposable
{
    /// <summary>
    /// Where to look, best first. Intel's build is preferred because it is the upstream
    /// project and is maintained; the NVIDIA FrameView copy is a fallback that happens to
    /// be present on many machines with an NVIDIA driver.
    /// </summary>
    private static IEnumerable<string> Candidates()
    {
        // Dropped next to the agent, which is the no-install route: unzip PresentMon here.
        var beside = AppContext.BaseDirectory;
        yield return Path.Combine(beside, "PresentMon.exe");
        yield return Path.Combine(beside, "PresentMon-x64.exe");

        var env = Environment.GetEnvironmentVariable("COMPANIONDECK_PRESENTMON");
        if (!string.IsNullOrWhiteSpace(env)) yield return env;

        foreach (var dir in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                 })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            yield return Path.Combine(dir, "PresentMon", "PresentMon.exe");
            yield return Path.Combine(dir, "Intel", "PresentMon", "PresentMon.exe");
        }

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(pf))
            yield return Path.Combine(pf, "NVIDIA Corporation", "FrameViewSDK", "bin", "PresentMon_x64.exe");

        // Anything on PATH, which covers a user who installed it their own way.
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string candidate;
            try { candidate = Path.Combine(dir.Trim(), "PresentMon.exe"); } catch { continue; }
            yield return candidate;
        }
    }

    private readonly object _gate = new();
    private readonly Queue<double> _frameTimes = new();   // ms between presents
    private const int WindowFrames = 600;                 // a few seconds at typical rates

    private Process? _proc;
    private int _trackedPid;
    private DateTime _lastFrameUtc = DateTime.MinValue;

    // A target with no swapchain - a background service, or our own process - makes
    // PresentMon exit at once. Without a cooldown the publish loop simply respawns it on
    // the next tick, which measured 80 process launches in under a minute.
    private int _lastAttemptPid;
    private DateTime _attemptStartedUtc;
    private DateTime _cooldownUntil = DateTime.MinValue;
    private int _consecutiveFailures;

    /// <summary>
    /// Set once this provider has been given up on for the life of the process. Every start
    /// attempt spawns a process which asks the kernel to create - and, with
    /// --stop_existing_session, to tear down - a real-time ETW trace session.
    /// </summary>
    private volatile bool _abandoned;
    private bool _everRead;

    /// <summary>Total attempts, so the give-up decision is about this run, not this target.</summary>
    private int _totalAttempts;
    private const int AbandonAfter = 6;

    public string? ExePath { get; }
    public bool Available => ExePath is not null && !_abandoned;

    public FpsProvider()
    {
        ExePath = Candidates().FirstOrDefault(p =>
        {
            try { return File.Exists(p); } catch { return false; }
        });
        Log.Info(ExePath is null
            ? "PresentMon not found - FPS unavailable. Put PresentMon.exe beside the agent, " +
              "or set COMPANIONDECK_PRESENTMON, to enable it."
            : $"PresentMon: {ExePath}");
    }

    /// <summary>Currently tracked process, or 0 when idle.</summary>
    public int TrackedPid => _trackedPid;

    /// <summary>
    /// Ensures we are tracking the given process, starting, retargeting or stopping as
    /// needed. Passing null or 0 stops tracking.
    /// </summary>
    public void Track(int? pid)
    {
        if (!Available) return;
        if (_abandoned) return;

        int want = pid.GetValueOrDefault();

        // Tracking ourselves is never meaningful and always fails.
        if (want == Environment.ProcessId) want = 0;

        lock (_gate)
        {
            if (want == _trackedPid && _proc is { HasExited: false }) return;

            // Did the previous attempt at this target die more or less immediately?
            if (want != 0 && want == _lastAttemptPid && _proc is { HasExited: true })
            {
                if ((DateTime.UtcNow - _attemptStartedUtc).TotalSeconds < 3)
                {
                    _consecutiveFailures++;
                    int backoff = Math.Min(60, 1 << Math.Min(6, _consecutiveFailures));
                    _cooldownUntil = DateTime.UtcNow.AddSeconds(backoff);
                    // Warn, not Debug. This was Debug, and the agent runs at Info, so a
                    // provider that has never once produced a reading failed in complete
                    // silence for the life of the project - the log showed only a cheerful
                    // "FPS tracking started" every time the backoff expired. A capability
                    // that is advertised to the tablet and then never works has to say so.
                    int code = -1;
                    try { code = _proc.ExitCode; } catch { }
                    if (_consecutiveFailures == 1 || _consecutiveFailures % 5 == 0)
                        Log.Warn($"PresentMon exited immediately (code {code}) for pid {want} " +
                                 $"(attempt {_consecutiveFailures}); backing off {backoff}s");

                    // Give up for good once it is clear this will never work here. Each retry
                    // costs a process spawn and a kernel ETW session, and that price is only
                    // worth paying while there is some prospect of a reading.
                    if (!_everRead && ++_totalAttempts >= AbandonAfter)
                    {
                        _abandoned = true;
                        Log.Warn($"PresentMon has failed {_totalAttempts} times without ever producing a " +
                                 "reading; disabling FPS for this session. Each attempt starts an ETW " +
                                 "trace session, which is far too expensive to keep retrying.");
                        StopLocked();
                        return;
                    }
                }
            }

            StopLocked();
            if (want <= 0) { _consecutiveFailures = 0; _cooldownUntil = DateTime.MinValue; return; }

            if (want == _lastAttemptPid && DateTime.UtcNow < _cooldownUntil) return;
            if (want != _lastAttemptPid) { _consecutiveFailures = 0; _cooldownUntil = DateTime.MinValue; }

            try
            {
                var psi = new ProcessStartInfo(ExePath!)
                {
                    // v1 metrics keep the CSV schema stable and simple.
                    Arguments = $"--process_id {want} --output_stdout --no_console_stats " +
                                "--v1_metrics --stop_existing_session --terminate_on_proc_exit " +
                                "--session_name CompanionDeck",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                _proc = Process.Start(psi);
                if (_proc is null) { Log.Warn("could not start PresentMon"); return; }

                _trackedPid = want;
                _lastAttemptPid = want;
                _attemptStartedUtc = DateTime.UtcNow;
                _frameTimes.Clear();
                _lastFrameUtc = DateTime.MinValue;

                _ = Task.Run(() => ReadLoop(_proc));
                _ = Task.Run(async () =>
                {
                    var err = await _proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(err)) Log.Warn($"PresentMon: {err.Trim()}");
                });

                Log.Info($"FPS tracking started for pid {want}");
            }
            catch (Exception ex)
            {
                Log.Warn($"PresentMon start failed: {ex.Message}");
                _proc = null;
                _trackedPid = 0;
            }
        }
    }

    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    private void StopLocked()
    {
        var p = _proc;
        _proc = null;
        _trackedPid = 0;
        _frameTimes.Clear();

        if (p is null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        try { p.Dispose(); } catch { }
        Log.Debug("FPS tracking stopped");
    }

    private void ReadLoop(Process p)
    {
        try
        {
            int col = -1;
            string? line;
            while ((line = p.StandardOutput.ReadLine()) is not null)
            {
                if (col < 0)
                {
                    // Locate the frame-interval column from the header rather than
                    // assuming a position; the schema differs between builds.
                    var headers = line.Split(',');
                    for (int i = 0; i < headers.Length; i++)
                    {
                        if (headers[i].Trim().Equals("msBetweenPresents", StringComparison.OrdinalIgnoreCase))
                        { col = i; break; }
                    }
                    if (col < 0) Log.Debug($"PresentMon header without msBetweenPresents: {line}");
                    continue;
                }

                var parts = line.Split(',');
                if (parts.Length <= col) continue;
                if (!double.TryParse(parts[col], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)) continue;
                if (ms <= 0 || ms > 1000) continue;   // ignore nonsense and long stalls

                lock (_gate)
                {
                    _everRead = true;
                    _frameTimes.Enqueue(ms);
                    while (_frameTimes.Count > WindowFrames) _frameTimes.Dequeue();
                    _lastFrameUtc = DateTime.UtcNow;
                }
            }
        }
        catch (Exception ex) { Log.Debug($"PresentMon read loop: {ex.Message}"); }
    }

    public sealed record Reading(double Fps, double OnePercentLow);

    /// <summary>Average FPS and 1% low over the rolling window, or null when idle.</summary>
    public Reading? Sample()
    {
        lock (_gate)
        {
            if (_frameTimes.Count < 20) return null;

            // Nothing presented recently: the game is minimised, loading, or gone.
            if ((DateTime.UtcNow - _lastFrameUtc).TotalSeconds > 2) return null;

            var times = _frameTimes.ToArray();
            double mean = times.Average();
            if (mean <= 0) return null;

            // 1% low is conventionally the mean of the worst 1% of frames.
            Array.Sort(times);
            int worstCount = Math.Max(1, times.Length / 100);
            double worstMean = times[^worstCount..].Average();

            return new Reading(
                Math.Round(1000.0 / mean, 1),
                Math.Round(1000.0 / worstMean, 1));
        }
    }

    public void Dispose() => Stop();
}
