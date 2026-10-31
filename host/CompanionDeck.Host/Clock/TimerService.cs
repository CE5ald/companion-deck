using System.Runtime.InteropServices;
using System.Text.Json;
using CompanionDeck.Host.Protocol;

namespace CompanionDeck.Host.Clock;

/// <summary>
/// Countdowns, stopwatches and pomodoro cycles. They live here, on the PC, as absolute
/// wall-clock times that are saved to disk, so a tablet that sleeps, reboots or is unplugged
/// loses nothing: it asks for the state again and draws it from the same moments.
/// </summary>
public sealed class TimerService : IDisposable
{
    public const int MaxTimers = 8;
    public const long MaxDurationMs = 24L * 3600 * 1000;

    private sealed class Entry
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "countdown";
        public string Label { get; set; } = "";
        public string State { get; set; } = "running";
        public long DurationMs { get; set; }
        public long? EndsAtMs { get; set; }
        public long? RemainingMs { get; set; }
        public long? StartedAtMs { get; set; }
        public long? ElapsedMs { get; set; }
        public string? Phase { get; set; }
        public int? Round { get; set; }
        public int? Rounds { get; set; }
        public long WorkMs { get; set; }
        public long ShortMs { get; set; }
        public long LongMs { get; set; }
        public long RingingSinceMs { get; set; }
    }

    [DllImport("user32.dll")] private static extern bool MessageBeep(uint type);

    private readonly string _path;
    private readonly Func<long> _now;
    private readonly Action _alarm;
    private readonly object _gate = new();
    private List<Entry> _items = new();
    private Timer? _tick;
    private long _lastBeepMs;

    /// <summary>Raised after any change a tablet would want to see.</summary>
    public event Action? Changed;

    public TimerService(string? path = null, Func<long>? now = null, Action? alarm = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CompanionDeck", "timers.json");
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _alarm = alarm ?? (() => MessageBeep(0x40));
    }

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                    _items = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllBytes(_path)) ?? new();
            }
            catch (Exception ex) { Log.Warn($"timers.json unreadable: {ex.Message}"); _items = new(); }

            // Anything that ran out while the agent was down is shown as finished, silently:
            // ringing for something that ended an hour ago helps nobody.
            long now = _now();
            foreach (var e in _items)
            {
                if (e.State != "running" || e.EndsAtMs is null || e.EndsAtMs > now) continue;
                if (e.Kind == "pomodoro") { e.State = "paused"; e.RemainingMs = PhaseMs(e); e.EndsAtMs = null; }
                else { e.State = "ringing"; e.RingingSinceMs = now - 3_600_000; }
            }
            _items.RemoveAll(e => e.Id.Length == 0);
            StartTickIfNeeded();
        }
    }

    public TimersState Snapshot()
    {
        lock (_gate)
        {
            return new TimersState
            {
                NowMs = _now(),
                Items = _items.Select(e => new TimerInfo
                {
                    Id = e.Id, Kind = e.Kind, Label = e.Label, State = e.State, DurationMs = e.DurationMs,
                    EndsAtMs = e.EndsAtMs, RemainingMs = e.RemainingMs, StartedAtMs = e.StartedAtMs,
                    ElapsedMs = e.ElapsedMs, Phase = e.Phase, Round = e.Round, Rounds = e.Rounds,
                }).ToList(),
            };
        }
    }

    public bool Any { get { lock (_gate) return _items.Count > 0; } }

    public bool StartCountdown(long seconds, string? label)
    {
        if (seconds < 1 || seconds * 1000 > MaxDurationMs) return false;
        lock (_gate)
        {
            if (_items.Count >= MaxTimers) return false;
            long ms = seconds * 1000;
            _items.Add(new Entry
            {
                Id = NewId(), Kind = "countdown", Label = Clean(label), State = "running",
                DurationMs = ms, EndsAtMs = _now() + ms,
            });
            Commit();
            return true;
        }
    }

    public bool StartStopwatch(string? label)
    {
        lock (_gate)
        {
            if (_items.Count >= MaxTimers) return false;
            _items.Add(new Entry
            {
                Id = NewId(), Kind = "stopwatch", Label = Clean(label), State = "running", StartedAtMs = _now(),
            });
            Commit();
            return true;
        }
    }

    public bool StartPomodoro(int workMin, int shortMin, int longMin, int rounds, string? label)
    {
        if (workMin is < 1 or > 180 || shortMin is < 1 or > 60 || longMin is < 1 or > 120 || rounds is < 1 or > 12) return false;
        lock (_gate)
        {
            if (_items.Count >= MaxTimers) return false;
            var e = new Entry
            {
                Id = NewId(), Kind = "pomodoro", Label = Clean(label), State = "running",
                WorkMs = workMin * 60_000L, ShortMs = shortMin * 60_000L, LongMs = longMin * 60_000L,
                Phase = "work", Round = 1, Rounds = rounds,
            };
            e.DurationMs = e.WorkMs;
            e.EndsAtMs = _now() + e.WorkMs;
            _items.Add(e);
            Commit();
            return true;
        }
    }

    public bool Pause(string id)
    {
        lock (_gate)
        {
            if (Find(id) is not { State: "running" } e) return false;
            long now = _now();
            if (e.Kind == "stopwatch") { e.ElapsedMs = now - e.StartedAtMs; e.StartedAtMs = null; }
            else { e.RemainingMs = Math.Max(0, (e.EndsAtMs ?? now) - now); e.EndsAtMs = null; }
            e.State = "paused";
            Commit();
            return true;
        }
    }

    public bool Resume(string id)
    {
        lock (_gate)
        {
            if (Find(id) is not { State: "paused" } e) return false;
            long now = _now();
            if (e.Kind == "stopwatch") { e.StartedAtMs = now - (e.ElapsedMs ?? 0); e.ElapsedMs = null; }
            else { e.EndsAtMs = now + (e.RemainingMs ?? 0); e.RemainingMs = null; }
            e.State = "running";
            Commit();
            return true;
        }
    }

    /// <summary>Stops ringing. A countdown goes away; a finished pomodoro goes away too.</summary>
    public bool Remove(string id)
    {
        lock (_gate)
        {
            if (_items.RemoveAll(e => e.Id == id) == 0) return false;
            Commit();
            return true;
        }
    }

    /// <summary>Moves a pomodoro on to its next phase now.</summary>
    public bool Skip(string id)
    {
        lock (_gate)
        {
            if (Find(id) is not { Kind: "pomodoro" } e) return false;
            Advance(e, _now(), beep: false);
            Commit();
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_items.Count == 0) return;
            _items.Clear();
            Commit();
        }
    }

    private Entry? Find(string id) => _items.FirstOrDefault(e => e.Id == id);

    private static long PhaseMs(Entry e) => e.Phase switch { "short" => e.ShortMs, "long" => e.LongMs, _ => e.WorkMs };

    /// <summary>Starts the phase after the current one: work, then a break, and a long break after the last round.</summary>
    private void Advance(Entry e, long now, bool beep)
    {
        if (e.Phase == "work")
            e.Phase = e.Round >= e.Rounds ? "long" : "short";
        else if (e.Phase == "short") { e.Phase = "work"; e.Round++; }
        else
        {
            // The long break ends the cycle.
            e.State = "ringing";
            e.EndsAtMs = null;
            e.RingingSinceMs = now;
            if (beep) _alarm();
            return;
        }

        e.DurationMs = PhaseMs(e);
        e.State = "running";
        e.EndsAtMs = now + e.DurationMs;
        e.RemainingMs = null;
        if (beep) _alarm();
    }

    /// <summary>One pass of the timer loop. Public so the self-test can drive it with a fake clock.</summary>
    public void Tick() => OnTick(null);

    private void OnTick(object? _)
    {
        bool changed = false;
        lock (_gate)
        {
            long now = _now();
            foreach (var e in _items.ToList())
            {
                if (e.State == "running" && e.EndsAtMs is { } end && end <= now)
                {
                    if (e.Kind == "pomodoro") Advance(e, now, beep: true);
                    else { e.State = "ringing"; e.RingingSinceMs = now; e.EndsAtMs = null; _alarm(); _lastBeepMs = now; }
                    changed = true;
                }
                // Repeats for twenty seconds, then goes quiet but stays on screen until dismissed.
                else if (e.State == "ringing" && now - e.RingingSinceMs < 20_000 && now - _lastBeepMs >= 2_000)
                {
                    _alarm();
                    _lastBeepMs = now;
                }
            }
            if (changed) Save();
            StartTickIfNeeded();
        }
        if (changed) Changed?.Invoke();
    }

    private void StartTickIfNeeded()
    {
        bool need = _items.Any(e => e.State == "ringing" || (e.State == "running" && e.Kind != "stopwatch"));
        if (need && _tick is null) _tick = new Timer(OnTick, null, 500, 500);
        else if (!need && _tick is not null) { _tick.Dispose(); _tick = null; }
    }

    private void Commit()
    {
        Save();
        StartTickIfNeeded();
        Changed?.Invoke();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(_items));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) { Log.Warn($"could not save timers.json: {ex.Message}"); }
    }

    private static string NewId() => "t" + Guid.NewGuid().ToString("N")[..8];

    private static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return t.Length > 24 ? t[..24].TrimEnd() : t;
    }

    public void Dispose()
    {
        lock (_gate) { _tick?.Dispose(); _tick = null; }
    }
}
