using System.Text.Json;
using CompanionDeck.Host.Protocol;

namespace CompanionDeck.Host.Monitoring;

public sealed class SessionRecord
{
    public string Id { get; set; } = "";
    public string Game { get; set; } = "";
    public long StartMs { get; set; }
    public int Seconds { get; set; }
    public double? AvgFps { get; set; }
    public double? Low1 { get; set; }
    public double? Low01 { get; set; }
    public double? AvgCpu { get; set; }
    public double? AvgGpu { get; set; }
    public double? MaxGpuTemp { get; set; }
    /// <summary>Frame rate over the session, at most 90 points, for the shape of it.</summary>
    public List<double> Series { get; set; } = new();
}

public sealed class LiveSession
{
    public string Game { get; set; } = "";
    public int Seconds { get; set; }
    public double? Fps { get; set; }
}

/// <summary>
/// Notices a game starting and ending and keeps what happened in between. Called once a second with
/// what is in front. A game has to be in front for a few seconds before it counts, and out of front
/// for a while before it is over, so alt-tabbing to read something does not split one evening of play
/// into six sessions, and a launcher that flashes up does not make one.
/// </summary>
public sealed class SessionRecorder
{
    public const int StartAfter = 8;
    public const int EndAfter = 25;
    public const int MinSeconds = 30;
    public const int MaxKept = 50;
    private const int MaxSeriesPoints = 90;

    private readonly string _path;
    private readonly object _gate = new();
    private List<SessionRecord> _items = new();

    private string? _game;
    private int _present;      // consecutive ticks the current game was in front
    private int _absent;       // consecutive ticks it was not
    private long _startMs;
    private int _ticks;
    private FrameStats _frames = new();
    private readonly List<double> _fps = new();
    private double _cpuSum, _gpuSum, _maxGpuTemp;
    private int _cpuN, _gpuN;
    private double? _lastFps;

    public event Action? Changed;

    public SessionRecorder(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CompanionDeck", "sessions.json");
    }

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return;
                _items = (JsonSerializer.Deserialize<List<SessionRecord>>(File.ReadAllBytes(_path), Json.Options) ?? new())
                    .Where(s => s.Id.Length > 0 && s.Seconds > 0).Take(MaxKept).ToList();
            }
            catch (Exception ex) { Log.Warn($"sessions.json unreadable: {ex.Message}"); }
        }
    }

    public List<SessionRecord> Items { get { lock (_gate) return _items.ToList(); } }

    /// <summary>Set while a game is being recorded, so the tablet can say so.</summary>
    public LiveSession? Live
    {
        get
        {
            lock (_gate)
                return _game is not null && _present >= StartAfter
                    ? new LiveSession { Game = _game, Seconds = _ticks, Fps = _lastFps }
                    : null;
        }
    }

    public void AddFrame(double ms)
    {
        lock (_gate) if (_game is not null) _frames.Add(ms);
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            if (_items.RemoveAll(s => s.Id == id) == 0) return false;
            Save();
        }
        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        lock (_gate) { _items.Clear(); Save(); }
        Changed?.Invoke();
    }

    /// <param name="game">What is in front and counts as a game, or null.</param>
    public void Tick(long nowMs, string? game, double? fps, double? cpu, double? gpu, double? gpuTemp)
    {
        bool changed = false;
        lock (_gate)
        {
            if (_game is not null && game != _game)
            {
                // Something else is in front, or nothing is.
                if (++_absent >= EndAfter || (game is not null && _absent >= 3))
                {
                    changed = Finish(nowMs);
                }
            }
            else if (_game is not null)
            {
                _absent = 0;
            }

            if (_game is null && game is not null)
            {
                _game = game; _present = 0; _absent = 0; _ticks = 0;
                _startMs = nowMs; _frames = new FrameStats(); _fps.Clear();
                _cpuSum = _gpuSum = _maxGpuTemp = 0; _cpuN = _gpuN = 0;
            }

            if (_game is not null && game == _game)
            {
                _present++;
                _ticks++;
                _lastFps = fps;
                if (fps is { } f) _fps.Add(f);
                if (cpu is { } c) { _cpuSum += c; _cpuN++; }
                if (gpu is { } g) { _gpuSum += g; _gpuN++; }
                if (gpuTemp is { } t) _maxGpuTemp = Math.Max(_maxGpuTemp, t);
            }
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Ends whatever is in progress, for when the agent itself is stopping.</summary>
    public void Flush(long nowMs)
    {
        bool changed;
        lock (_gate) changed = _game is not null && Finish(nowMs);
        if (changed) Changed?.Invoke();
    }

    private bool Finish(long nowMs)
    {
        // Only seconds with the game in front are counted, so the wait to be sure it had ended is not.
        int seconds = _ticks;
        string game = _game!;
        bool counted = _present >= StartAfter && seconds >= MinSeconds;
        var frames = _frames;
        var fps = _fps.ToList();
        double cpu = _cpuN > 0 ? _cpuSum / _cpuN : double.NaN, gpu = _gpuN > 0 ? _gpuSum / _gpuN : double.NaN;
        double maxT = _maxGpuTemp;
        long start = _startMs;

        _game = null; _present = 0; _absent = 0; _ticks = 0; _lastFps = null;

        if (!counted) return false;

        _items.Insert(0, new SessionRecord
        {
            Id = "s" + Guid.NewGuid().ToString("N")[..8],
            Game = game,
            StartMs = start,
            Seconds = seconds,
            AvgFps = frames.AverageFps,
            Low1 = frames.LowFps(0.01),
            Low01 = frames.LowFps(0.001),
            AvgCpu = double.IsNaN(cpu) ? null : Math.Round(cpu, 0),
            AvgGpu = double.IsNaN(gpu) ? null : Math.Round(gpu, 0),
            MaxGpuTemp = maxT > 0 ? Math.Round(maxT, 0) : null,
            Series = Downsample(fps, MaxSeriesPoints),
        });
        if (_items.Count > MaxKept) _items.RemoveRange(MaxKept, _items.Count - MaxKept);
        Save();
        Log.Info($"session recorded: {game}, {seconds}s");
        return true;
    }

    public static List<double> Downsample(List<double> values, int max)
    {
        if (values.Count <= max) return values.Select(v => Math.Round(v, 0)).ToList();
        var result = new List<double>(max);
        double step = (double)values.Count / max;
        for (int i = 0; i < max; i++)
        {
            int a = (int)(i * step), b = Math.Max(a + 1, (int)((i + 1) * step));
            result.Add(Math.Round(values.Skip(a).Take(b - a).Average(), 0));
        }
        return result;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(_items, Json.Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) { Log.Warn($"could not save sessions.json: {ex.Message}"); }
    }
}
