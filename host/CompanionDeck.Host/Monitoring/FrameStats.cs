namespace CompanionDeck.Host.Monitoring;

/// <summary>
/// Frame-time statistics for a whole session in constant memory. Every frame goes into a histogram
/// with 0.1 ms resolution up to 100 ms, and the rare slower ones are kept exactly; the "1% low" and
/// "0.1% low" figures are then read off the slow end. A three hour session at 144 fps is 1.5 million
/// frames, which is not something to keep a list of.
/// </summary>
public sealed class FrameStats
{
    private const double BinMs = 0.1;
    private const int Bins = 1000;
    private const int MaxOverflow = 20_000;

    private readonly long[] _bins = new long[Bins];
    private readonly List<double> _slow = new();   // frames of 100 ms or more, kept exactly
    private long _count;
    private double _sumMs;

    public long Count => _count;

    public void Add(double ms)
    {
        if (ms <= 0 || double.IsNaN(ms) || ms > 1000) return;
        _count++;
        _sumMs += ms;
        if (ms >= Bins * BinMs)
        {
            if (_slow.Count < MaxOverflow) _slow.Add(ms);
            else _bins[Bins - 1]++;
        }
        else _bins[(int)(ms / BinMs)]++;
    }

    public double? AverageFps => _count == 0 ? null : Math.Round(1000.0 * _count / _sumMs, 1);

    /// <summary>
    /// Frame rate of the slowest <paramref name="fraction"/> of frames: 0.01 is the "1% low". It is
    /// the mean of those frames, expressed as a rate, which is the convention capture tools use.
    /// </summary>
    public double? LowFps(double fraction)
    {
        if (_count < 20) return null;
        long wanted = Math.Max(1, (long)(_count * fraction));
        long taken = 0;
        double sum = 0;

        foreach (var ms in _slow.OrderByDescending(x => x))
        {
            if (taken >= wanted) break;
            taken++; sum += ms;
        }
        for (int i = Bins - 1; i >= 0 && taken < wanted; i--)
        {
            long here = Math.Min(_bins[i], wanted - taken);
            if (here <= 0) continue;
            sum += here * ((i + 0.5) * BinMs);
            taken += here;
        }
        return taken == 0 || sum <= 0 ? null : Math.Round(1000.0 * taken / sum, 1);
    }
}
