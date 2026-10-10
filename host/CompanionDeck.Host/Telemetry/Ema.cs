namespace CompanionDeck.Host.Telemetry;

/// <summary>
/// Exponential moving average, used to take the jitter out of CPU and GPU load before it ever
/// reaches the tablet. Sampling CPU over a sub-second window is genuinely noisy - consecutive
/// reads of an idle machine can swing between 9% and 38% purely from scheduling.
/// </summary>
public struct Ema
{
    private double _value;
    private bool _seeded;

    public double Alpha { get; init; }

    public Ema(double alpha = 0.4) { Alpha = alpha; _value = 0; _seeded = false; }

    public double Push(double sample)
    {
        if (double.IsNaN(sample) || double.IsInfinity(sample)) return _value;

        if (!_seeded) { _value = sample; _seeded = true; }
        else _value += (Alpha <= 0 ? 0.4 : Alpha) * (sample - _value);

        return _value;
    }

    public double? Push(double? sample) => sample is null ? null : Push(sample.Value);

    public void Reset() => _seeded = false;
}
