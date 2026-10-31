using System.Diagnostics;
using CompanionDeck.Host.Protocol;

namespace CompanionDeck.Host.Monitoring;

/// <summary>
/// The processes using the most CPU, memory and GPU right now. CPU is a rate, so it needs two looks
/// a little apart: the first snapshot after a quiet spell has no CPU figures, and says so.
/// </summary>
public sealed class TopProcesses : IDisposable
{
    private readonly Func<Process, bool> _isProtected;
    private readonly Dictionary<int, (TimeSpan cpu, long at)> _previous = new();
    private GpuProcessCounters? _gpu;
    private bool _gpuTried;
    private long _lastAt;

    public bool GpuAvailable => _gpu?.Available == true;

    public TopProcesses(Func<Process, bool> isProtected) => _isProtected = isProtected;

    public TopState Snapshot(int take = 8)
    {
        if (!_gpuTried)
        {
            _gpuTried = true;
            _gpu = new GpuProcessCounters();
            if (!_gpu.TryOpen()) { _gpu.Dispose(); _gpu = null; }
        }

        long now = Environment.TickCount64;
        // Left alone for a while, the previous reading describes a different moment altogether.
        if (now - _lastAt > 15_000) _previous.Clear();
        _lastAt = now;

        var gpu = _gpu?.Read() ?? new Dictionary<int, double>();
        var rows = new List<TopProcessInfo>();
        var next = new Dictionary<int, (TimeSpan, long)>();
        int cores = Math.Max(1, Environment.ProcessorCount);
        bool haveRates = _previous.Count > 0;

        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (Exception ex) { Log.Debug($"top processes: {ex.Message}"); return new TopState(); }

        try
        {
            foreach (var p in all)
            {
                try
                {
                    if (p.Id <= 4) continue;
                    double? cpu = null;
                    try
                    {
                        var total = p.TotalProcessorTime;
                        next[p.Id] = (total, now);
                        if (_previous.TryGetValue(p.Id, out var prev) && now > prev.at)
                            cpu = Math.Round(Math.Clamp((total - prev.cpu).TotalMilliseconds / ((now - prev.at) * cores) * 100, 0, 100), 1);
                    }
                    catch { /* a process this agent may not inspect */ }

                    bool low = false;
                    try { low = p.PriorityClass is ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Idle; } catch { }

                    rows.Add(new TopProcessInfo
                    {
                        Pid = p.Id,
                        Name = p.ProcessName,
                        Cpu = cpu,
                        RamMb = p.WorkingSet64 / (1024 * 1024),
                        Gpu = gpu.TryGetValue(p.Id, out var g) ? Math.Round(g, 1) : null,
                        Protected = _isProtected(p),
                        Low = low,
                    });
                }
                catch { }
            }
        }
        finally { foreach (var p in all) { try { p.Dispose(); } catch { } } }

        _previous.Clear();
        foreach (var (pid, v) in next) _previous[pid] = v;

        // One name per row: a browser is forty processes and one thing to the person looking.
        rows = rows.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            var lead = g.OrderByDescending(r => r.RamMb).First();
            return new TopProcessInfo
            {
                Pid = lead.Pid, Name = lead.Name,
                Cpu = g.Any(r => r.Cpu is not null) ? Math.Round(g.Sum(r => r.Cpu ?? 0), 1) : null,
                RamMb = g.Sum(r => r.RamMb),
                Gpu = g.Any(r => r.Gpu is not null) ? Math.Round(g.Sum(r => r.Gpu ?? 0), 1) : null,
                Protected = g.Any(r => r.Protected),
                Low = lead.Low,
                Count = g.Count(),
            };
        }).ToList();

        return new TopState
        {
            HaveCpu = haveRates,
            GpuAvailable = _gpu?.Available == true,
            Cpu = rows.Where(r => r.Cpu > 0).OrderByDescending(r => r.Cpu).Take(take).ToList(),
            Ram = rows.OrderByDescending(r => r.RamMb).Take(take).ToList(),
            Gpu = rows.Where(r => r.Gpu > 0).OrderByDescending(r => r.Gpu).Take(take).ToList(),
        };
    }

    public void Dispose() { _gpu?.Dispose(); _gpu = null; }
}
