using NAudio.Wave;

namespace CompanionDeck.Host.Audio;

/// <summary>
/// A gain that travels to its target instead of jumping to it. NAudio's own VolumeSampleProvider
/// applies a new value to the very next sample, which is a step discontinuity in the waveform and
/// is audible as a click.
/// </summary>
public sealed class SmoothVolume : ISampleProvider
{
    private readonly ISampleProvider _src;
    private readonly float _step;

    private float _current;
    private float _target;

    public SmoothVolume(ISampleProvider src, float initial = 1f, int rampMs = 150)
    {
        _src = src;
        _current = _target = initial;
        WaveFormat = src.WaveFormat;

        // Per-sample increment that crosses the full 0..1 range in rampMs.
        int samplesPerRamp = Math.Max(1, WaveFormat.SampleRate * WaveFormat.Channels * rampMs / 1000);
        _step = 1f / samplesPerRamp;
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Where the gain should end up. Reached over the ramp, not immediately.</summary>
    public float Target
    {
        get => _target;
        set => _target = Math.Clamp(value, 0f, 4f);
    }

    /// <summary>Jumps without ramping. Only safe when nothing is flowing yet.</summary>
    public void SetImmediate(float v) => _current = _target = Math.Clamp(v, 0f, 4f);

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _src.Read(buffer, offset, count);

        // Nothing to interpolate: skip the per-sample branch entirely on the common path.
        if (_current == _target)
        {
            if (_current != 1f)
                for (int i = 0; i < read; i++) buffer[offset + i] *= _current;
            return read;
        }

        for (int i = 0; i < read; i++)
        {
            if (_current < _target) _current = Math.Min(_target, _current + _step);
            else if (_current > _target) _current = Math.Max(_target, _current - _step);
            buffer[offset + i] *= _current;
        }
        return read;
    }
}
