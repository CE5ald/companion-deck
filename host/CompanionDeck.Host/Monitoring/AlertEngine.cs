namespace CompanionDeck.Host.Monitoring;

public sealed class AlertConfig
{
    /// <summary>Off until someone turns it on: a buzzing tablet nobody asked for is worse than none.</summary>
    public bool Enabled { get; set; }
    public int CpuTemp { get; set; } = 90;
    public int GpuTemp { get; set; } = 85;
    public int Ram { get; set; } = 92;
    /// <summary>Fan speed in RPM; 0 leaves it unwatched. Only machines that report fans have one.</summary>
    public int Fan { get; set; }

    public AlertConfig Clamped() => new()
    {
        Enabled = Enabled,
        CpuTemp = Math.Clamp(CpuTemp, 40, 110),
        GpuTemp = Math.Clamp(GpuTemp, 40, 110),
        Ram = Math.Clamp(Ram, 50, 99),
        Fan = Math.Clamp(Fan, 0, 12000),
    };
}

public sealed record AlertEvent(string Metric, string Label, double Value, double Limit, long AtMs);

/// <summary>
/// Decides when a reading has really crossed a limit. A single hot sample is not an alert, so a
/// value has to stay over for a few readings; and once it has fired it stays quiet until the value
/// has come back down a little, so a temperature sitting right on the line does not buzz the
/// tablet every second. A condition that persists reminds once a minute, not continuously.
/// </summary>
public sealed class AlertEngine
{
    public const int SustainReadings = 3;
    public const long ReminderMs = 60_000;

    private sealed class State { public int Over; public bool Active; public long LastFiredMs; }

    private readonly Dictionary<string, State> _state = new();

    public IReadOnlyCollection<string> Active
    {
        get { lock (_state) return _state.Where(kv => kv.Value.Active).Select(kv => kv.Key).ToList(); }
    }

    public List<AlertEvent> Evaluate(AlertConfig cfg, long nowMs, double? cpuTemp, double? gpuTemp, double? ramPercent, double? fanRpm)
    {
        var fired = new List<AlertEvent>();
        lock (_state)
        {
            if (!cfg.Enabled) { _state.Clear(); return fired; }

            Check(fired, "cpuTemp", "CPU temperature", cpuTemp, cfg.CpuTemp, 3, nowMs);
            Check(fired, "gpuTemp", "GPU temperature", gpuTemp, cfg.GpuTemp, 3, nowMs);
            Check(fired, "ram", "Memory use", ramPercent, cfg.Ram, 3, nowMs);
            if (cfg.Fan > 0) Check(fired, "fan", "Fan speed", fanRpm, cfg.Fan, 200, nowMs);
        }
        return fired;
    }

    private void Check(List<AlertEvent> fired, string metric, string label, double? value, double limit, double hysteresis, long nowMs)
    {
        if (!_state.TryGetValue(metric, out var s)) _state[metric] = s = new State();
        if (value is not { } v) { s.Over = 0; return; }

        if (v >= limit)
        {
            if (++s.Over < SustainReadings) return;
            if (!s.Active || nowMs - s.LastFiredMs >= ReminderMs)
            {
                s.Active = true;
                s.LastFiredMs = nowMs;
                fired.Add(new AlertEvent(metric, label, Math.Round(v, 1), limit, nowMs));
            }
        }
        else
        {
            s.Over = 0;
            if (s.Active && v <= limit - hysteresis) s.Active = false;
        }
    }
}
