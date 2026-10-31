using System.Diagnostics;
using System.Security.Principal;
using CompanionDeck.Host.Server;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace CompanionDeck.Host.Telemetry;

/// <summary>
/// Frame rate for a running game, read from Windows' own event tracing (ETW). The DirectX runtime
/// announces every Present call it receives, per process and per swap chain, so counting those gives
/// the frame rate of any DirectX 9/10/11/12 game on any GPU from any vendor, with nothing to install.
///
/// This replaces a design that launched PresentMon as a child process. That never produced a reading:
/// it picked whichever PresentMon-like executable it found first, and the one it found was NVIDIA
/// FrameView's private build, which exits at once with code 1 when driven the way a public PresentMon
/// is. Anything that depends on a third-party executable being present, being the right one, and
/// accepting these arguments, fails in exactly that way on somebody's machine.
///
/// OpenGL and Vulkan games do not go through DirectX's present path, so for them the same count is
/// taken from the graphics kernel's own present event, which every API ends in. DirectX swap chains are
/// preferred when there are any, because they separate a game's real swap chain from its overlays.
///
/// What this measures is the interval between the game's own Present calls, which is what the
/// "frame time" of nearly every overlay is. It is not the time a frame reached the screen, so frames
/// that a driver generates itself (frame generation) are not counted, and a frame the game presented
/// but the display dropped is.
///
/// Windows only lets an administrator, or a member of "Performance Log Users", open a real-time
/// trace. When neither is true <see cref="Available"/> is false and <see cref="Detail"/> says so,
/// rather than a number being invented.
/// </summary>
public sealed class FpsProvider : IDisposable
{
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");   // Microsoft-Windows-DXGI
    private static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");   // Microsoft-Windows-D3D9
    private static readonly Guid DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D"); // Microsoft-Windows-DxgKrnl

    private const int DxgiPresentStart = 42;
    private const int D3D9PresentStart = 1;
    private const int KrnlPresentInfo = 184;
    // The present events are the ones carrying bit 27 of the kernel provider's keywords. Asking for
    // every keyword delivered about 45,000 events a second on a machine doing nothing in particular.
    private const ulong KrnlPresentKeyword = 0x8000000;
    private const long KrnlKey = long.MinValue + 1;

    private const string SessionName = "CompanionDeckFps";
    private const int RecentFrames = 360;       // the live frame-time graph
    private const int WindowFrames = 600;       // the 1% low
    private const double LiveWindowMs = 1000;   // the number on the dial is the last second
    private const double StaleAfterMs = 2000;

    /// <summary>Raised for every frame, on the trace thread; handlers must be quick.</summary>
    public event Action<double>? Frame;

    private readonly object _gate = new();

    // ---- state shared between the trace thread and the readers, under _gate
    private readonly Queue<double> _frameTimes = new();   // ms between presents, for the 1% low
    private readonly Queue<double> _recent = new();       // the latest few hundred, for the graph
    private readonly Queue<double> _stamps = new();       // present timestamps in the last second
    private DateTime _lastFrameUtc = DateTime.MinValue;
    private long _framesSinceTrack;

    // ---- trace-thread-only state
    private sealed class Chain
    {
        public double LastMs = double.NaN;
        public double LastSeenMs;
        public readonly Queue<double> Recent = new();   // timestamps in the last second
    }
    private readonly Dictionary<long, Chain> _chains = new();
    private long _mainKey = long.MinValue;
    private double _nextPickMs;

    private TraceEventSession? _session;
    private Thread? _thread;
    private volatile int _pid;
    private DateTime _trackStartedUtc;
    private DateTime _idleSince = DateTime.MinValue;
    private int _failures;
    private DateTime _retryAt = DateTime.MinValue;
    private volatile bool _abandoned;

    private readonly bool _permitted;
    private string _problem = "";

    public FpsProvider()
    {
        _permitted = Elevation.IsElevated || InPerformanceLogUsers();
        if (!_permitted)
        {
            _problem = "needs the elevated agent (or your account in Performance Log Users)";
            Log.Info($"FPS unavailable: {_problem}");
        }
        else Log.Info("FPS: Windows event tracing (DirectX present events)");
    }

    /// <summary>True when this process may open a trace session, and it has not been given up on.</summary>
    public bool Available => _permitted && !_abandoned;

    /// <summary>The process being measured, or 0 when idle.</summary>
    public int TrackedPid => _pid;

    /// <summary>"off" (nothing asked), "waiting" (asked, no frames yet), "live", or "unavailable".</summary>
    public string State
    {
        get
        {
            if (!Available) return "unavailable";
            if (_pid == 0) return "off";
            lock (_gate)
                return (DateTime.UtcNow - _lastFrameUtc).TotalMilliseconds <= StaleAfterMs ? "live" : "waiting";
        }
    }

    /// <summary>Why FPS is unavailable, or what a "waiting" state most likely means. Empty when fine.</summary>
    public string Detail
    {
        get
        {
            if (!Available) return _problem;
            if (State == "waiting" && _pid != 0 &&
                (DateTime.UtcNow - _trackStartedUtc).TotalSeconds > 5 && Volatile.Read(ref _framesSinceTrack) == 0)
                return "no frames seen from this program; it may not draw through DirectX";
            return "";
        }
    }

    private static bool InPerformanceLogUsers()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(new SecurityIdentifier("S-1-5-32-559"));
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ control

    /// <summary>
    /// Ensures the given process is the one being measured, starting the trace when needed. Passing
    /// null or 0 begins a short grace period after which the trace is stopped, so an alt-tab out of a
    /// game and back does not tear a kernel session down and up again.
    /// </summary>
    public void Track(int? pid)
    {
        if (!Available) return;

        int want = pid.GetValueOrDefault();
        if (want == Environment.ProcessId) want = 0;   // never meaningful

        lock (_gate)
        {
            if (want == 0)
            {
                if (_pid != 0) { _pid = 0; ClearReadings(); _idleSince = DateTime.UtcNow; }
                if (_session is not null && _idleSince != DateTime.MinValue &&
                    (DateTime.UtcNow - _idleSince).TotalSeconds > 6)
                    StopSessionLocked();
                return;
            }

            _idleSince = DateTime.MinValue;
            if (want != _pid)
            {
                _pid = want;
                ClearReadings();
                _trackStartedUtc = DateTime.UtcNow;
                Log.Info($"FPS tracking started for pid {want}");
            }

            if (_session is null && DateTime.UtcNow >= _retryAt) StartSessionLocked();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _pid = 0;
            ClearReadings();
            StopSessionLocked();
        }
    }

    private void ClearReadings()
    {
        _frameTimes.Clear(); _recent.Clear(); _stamps.Clear();
        _lastFrameUtc = DateTime.MinValue;
        Volatile.Write(ref _framesSinceTrack, 0);
        _chainsReset = true;
    }

    private volatile bool _chainsReset;

    private void StartSessionLocked()
    {
        try
        {
            // Creating a session under a name that already exists stops the old one, which is what is
            // wanted when a previous run of this agent died without cleaning up.
            var session = new TraceEventSession(SessionName) { StopOnDispose = true };
            // Filtered by event id inside the kernel, so the agent is handed the present events and
            // nothing else rather than being asked to look at all of them.
            static TraceEventProviderOptions Only(int id) => new() { EventIDsToEnable = new List<int> { id } };
            session.EnableProvider(Dxgi, TraceEventLevel.Informational, ulong.MaxValue, Only(DxgiPresentStart));
            session.EnableProvider(D3D9, TraceEventLevel.Informational, ulong.MaxValue, Only(D3D9PresentStart));
            session.EnableProvider(DxgKrnl, TraceEventLevel.Informational, KrnlPresentKeyword, Only(KrnlPresentInfo));
            session.Source.AllEvents += OnEvent;
            _session = session;
            _chains.Clear();
            _mainKey = long.MinValue;

            _thread = new Thread(() =>
            {
                try { session.Source.Process(); }
                catch (Exception ex) { Log.Warn($"FPS trace ended with an error: {ex}"); }
                finally
                {
                    // However it ended, a session nobody is reading from must not keep Track() from
                    // starting a new one.
                    lock (_gate)
                    {
                        if (ReferenceEquals(_session, session))
                        {
                            _session = null;
                            _retryAt = DateTime.UtcNow.AddSeconds(2);
                            try { session.Dispose(); } catch { }
                        }
                    }
                }
            }) { IsBackground = true, Name = "fps-etw" };
            _thread.Start();
            _failures = 0;
            Log.Info("FPS trace session started");
        }
        catch (Exception ex)
        {
            _session = null;
            _failures++;
            _retryAt = DateTime.UtcNow.AddSeconds(Math.Min(60, 5 * _failures));
            _problem = ex is UnauthorizedAccessException
                ? "needs the elevated agent (or your account in Performance Log Users)"
                : $"the Windows trace could not start: {ex.Message}";
            Log.Warn($"FPS trace session failed (attempt {_failures}): {ex.Message}");
            if (_failures >= 5)
            {
                _abandoned = true;
                Log.Warn("FPS disabled for this run after repeated trace failures");
            }
        }
    }

    private void StopSessionLocked()
    {
        var s = _session;
        _session = null;
        if (s is null) return;
        try { s.Dispose(); } catch { }
        Log.Info("FPS trace session stopped");
    }

    // ------------------------------------------------------------------ the trace thread

    private void OnEvent(TraceEvent e)
    {
        int pid = _pid;
        if (pid == 0 || e.ProcessID != pid) return;

        long key;
        var g = e.ProviderGuid;
        int id = (int)e.ID;
        if (g == Dxgi && id == DxgiPresentStart) key = SwapChain(e);
        else if (g == Dxgi && id == DxgiPresentStart) key = SwapChain(e);
        else if (g == DxgKrnl && id == KrnlPresentInfo) key = KrnlKey;
        else return;

        double ts = e.TimeStampRelativeMSec;

        if (_chainsReset) { _chains.Clear(); _mainKey = long.MinValue; _chainsReset = false; }

        if (!_chains.TryGetValue(key, out var chain)) _chains[key] = chain = new Chain();
        double interval = double.IsNaN(chain.LastMs) ? double.NaN : ts - chain.LastMs;
        chain.LastMs = ts;
        chain.LastSeenMs = ts;
        chain.Recent.Enqueue(ts);
        while (chain.Recent.Count > 0 && ts - chain.Recent.Peek() > LiveWindowMs) chain.Recent.Dequeue();

        // A process may own several swap chains (an overlay, a launcher, a second window). The game's
        // is the one presenting the most, so choose by recent count, re-evaluated twice a second.
        if (ts >= _nextPickMs)
        {
            _nextPickMs = ts + 500;
            long best = _mainKey; double bestN = -1;
            foreach (var (k, c) in _chains)
            {
                if (ts - c.LastSeenMs > StaleAfterMs) continue;
                // The kernel's count is a fallback: any live DirectX swap chain outranks it.
                double score = k == KrnlKey ? -0.5 : c.Recent.Count;
                if (score > bestN) { best = k; bestN = score; }
            }
            _mainKey = best;
        }
        if (_mainKey == long.MinValue) _mainKey = key;
        if (key != _mainKey) return;

        Volatile.Write(ref _framesSinceTrack, 1);
        if (double.IsNaN(interval) || interval <= 0 || interval > 1000) return;   // first frame, or a stall

        lock (_gate)
        {
            if (_pid != pid) return;
            _frameTimes.Enqueue(interval);
            while (_frameTimes.Count > WindowFrames) _frameTimes.Dequeue();
            _recent.Enqueue(interval);
            while (_recent.Count > RecentFrames) _recent.Dequeue();
            _stamps.Enqueue(ts);
            while (_stamps.Count > 0 && ts - _stamps.Peek() > LiveWindowMs) _stamps.Dequeue();
            _lastFrameUtc = DateTime.UtcNow;
        }
        try { Frame?.Invoke(interval); } catch { /* a listener must not stop the trace */ }
    }

    /// <summary>
    /// The swap chain's address, which is the first field of both present events. Two swap chains in one
    /// process then count separately instead of interleaving into one impossible frame rate.
    /// </summary>
    private static long SwapChain(TraceEvent e)
    {
        try
        {
            var p = e.EventData();
            return p.Length >= 8 ? BitConverter.ToInt64(p, 0) : 0;
        }
        catch { return 0; }
    }

    // ------------------------------------------------------------------ readings

    /// <summary>The most recent frame times in milliseconds, oldest first; empty when nothing is being drawn.</summary>
    public double[] RecentFrameTimes(int count)
    {
        lock (_gate)
        {
            if ((DateTime.UtcNow - _lastFrameUtc).TotalMilliseconds > StaleAfterMs) return Array.Empty<double>();
            var all = _recent.ToArray();
            return all.Length <= count ? all : all[^count..];
        }
    }

    public sealed record Reading(double Fps, double OnePercentLow);

    /// <summary>Frames per second over the last second, and the 1% low over the last few hundred frames; null when nothing is being drawn.</summary>
    public Reading? Sample()
    {
        lock (_gate)
        {
            if (_pid == 0 || _frameTimes.Count < 5) return null;
            if ((DateTime.UtcNow - _lastFrameUtc).TotalMilliseconds > StaleAfterMs) return null;

            double fps;
            if (_stamps.Count >= 3)
            {
                double span = _stamps.Last() - _stamps.Peek();
                fps = span > 0 ? (_stamps.Count - 1) * 1000.0 / span : 0;
            }
            else
            {
                double mean = _frameTimes.TakeLast(Math.Min(_frameTimes.Count, 30)).Average();
                fps = mean > 0 ? 1000.0 / mean : 0;
            }
            if (fps <= 0) return null;

            var times = _frameTimes.ToArray();
            Array.Sort(times);
            int worstCount = Math.Max(1, times.Length / 100);
            double worstMean = times[^worstCount..].Average();

            return new Reading(Math.Round(fps, 1), Math.Round(1000.0 / worstMean, 1));
        }
    }

    public void Dispose() => Stop();
}
