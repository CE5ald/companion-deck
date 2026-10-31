namespace CompanionDeck.Host.Monitoring;

/// <summary>One second of the machine. A missing reading is NaN, never zero: zero is a real value.</summary>
public readonly record struct Reading(long AtMs, double Cpu, double Gpu, double Ram, double CpuTemp, double GpuTemp, double Fps);

/// <summary>What the history graphs draw: one value per bucket, null where nothing was measured.</summary>
public class HistoryData
{
    public long StartMs { get; set; }
    public int StepMs { get; set; }
    public double?[] Cpu { get; set; } = Array.Empty<double?>();
    public double?[] Gpu { get; set; } = Array.Empty<double?>();
    public double?[] Ram { get; set; } = Array.Empty<double?>();
    public double?[] CpuTemp { get; set; } = Array.Empty<double?>();
    public double?[] GpuTemp { get; set; } = Array.Empty<double?>();
    public double?[] Fps { get; set; } = Array.Empty<double?>();
}

/// <summary>
/// The last half hour at one reading a second, in a ring, so a spike can be seen after the fact.
/// About 1800 readings of six numbers: a few tens of kilobytes, whatever the tablet is doing.
/// </summary>
public sealed class HistoryBuffer
{
    public const int Capacity = 1800;

    private readonly Reading[] _ring = new Reading[Capacity];
    private readonly object _gate = new();
    private int _next;
    private int _count;

    public void Add(Reading r)
    {
        lock (_gate)
        {
            _ring[_next] = r;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    public int Count { get { lock (_gate) return _count; } }

    /// <summary>
    /// Buckets the last <paramref name="seconds"/> into <paramref name="points"/> values. Load and
    /// temperature keep the highest reading in each bucket, because the point of a history is the
    /// spike and an average would sand it off; frame rate keeps the mean.
    /// </summary>
    public HistoryData Query(int seconds, int points, long nowMs)
    {
        seconds = Math.Clamp(seconds, 10, Capacity);
        // Readings arrive once a second, so a bucket narrower than that is empty every other time and the
        // graph turns into unconnected dots. Never ask for more points than there are seconds.
        points = Math.Min(Math.Clamp(points, 10, 400), seconds);
        long windowMs = seconds * 1000L;
        long start = nowMs - windowMs;
        double bucketMs = (double)windowMs / points;

        var cpu = new double?[points]; var gpu = new double?[points]; var ram = new double?[points];
        var ct = new double?[points]; var gt = new double?[points]; var fps = new double?[points];
        var fpsSum = new double[points]; var fpsN = new int[points];

        Reading[] snapshot;
        lock (_gate)
        {
            snapshot = new Reading[_count];
            for (int i = 0; i < _count; i++)
                snapshot[i] = _ring[(_next - _count + i + Capacity * 2) % Capacity];
        }

        foreach (var r in snapshot)
        {
            if (r.AtMs < start || r.AtMs > nowMs) continue;
            int b = Math.Min(points - 1, (int)((r.AtMs - start) / bucketMs));
            Peak(cpu, b, r.Cpu); Peak(gpu, b, r.Gpu); Peak(ram, b, r.Ram);
            Peak(ct, b, r.CpuTemp); Peak(gt, b, r.GpuTemp);
            if (!double.IsNaN(r.Fps)) { fpsSum[b] += r.Fps; fpsN[b]++; }
        }
        for (int b = 0; b < points; b++)
            if (fpsN[b] > 0) fps[b] = Math.Round(fpsSum[b] / fpsN[b], 1);

        return new HistoryData
        {
            StartMs = start, StepMs = (int)bucketMs,
            Cpu = cpu, Gpu = gpu, Ram = ram, CpuTemp = ct, GpuTemp = gt, Fps = fps,
        };
    }

    private static void Peak(double?[] series, int bucket, double value)
    {
        if (double.IsNaN(value)) return;
        value = Math.Round(value, 1);
        if (series[bucket] is not { } current || value > current) series[bucket] = value;
    }
}
