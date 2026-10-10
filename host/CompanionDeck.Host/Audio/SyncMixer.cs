using NAudio.Wave;

namespace CompanionDeck.Host.Audio;

/// <summary>
/// A mixer whose source list is safe to change while it is playing. This exists because NAudio's
/// MixingSampleProvider is not.
/// </summary>
public sealed class SyncMixer : ISampleProvider
{
    private readonly object _lock = new();
    private readonly List<ISampleProvider> _sources = new();
    private readonly List<ISampleProvider> _snapshot = new();
    private float[] _scratch = Array.Empty<float>();

    public SyncMixer(WaveFormat format) => WaveFormat = format;

    public WaveFormat WaveFormat { get; }

    public int SourceCount { get { lock (_lock) return _sources.Count; } }

    public void Add(ISampleProvider source)
    {
        if (!source.WaveFormat.Equals(WaveFormat))
            throw new ArgumentException("source format does not match the mixer");
        lock (_lock) _sources.Add(source);
    }

    public void Remove(ISampleProvider source)
    {
        lock (_lock) _sources.Remove(source);
    }

    /// <summary>
    /// Always fills the buffer, padding with silence. A mixer feeding a live output must never
    /// report a short read: the device would treat it as the end of the stream and stop, which
    /// for the cast means the call goes silent the moment nothing happens to be playing.
    /// </summary>
    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);

        lock (_lock)
        {
            if (_scratch.Length < count) _scratch = new float[count];

            // Iterate a snapshot and remove by reference, never by index. Reading a source can
            // re-enter this class: a clip that reaches its end notifies its owner, which calls
            // Remove, which takes this same lock on this same thread and shortens the list
            // underneath the loop.
            _snapshot.Clear();
            _snapshot.AddRange(_sources);

            foreach (var src in _snapshot)
            {
                int n;
                try { n = src.Read(_scratch, 0, count); }
                catch (Exception ex)
                {
                    // One bad source must not take the output down with it.
                    Log.Warn($"mixer source failed: {ex.Message}");
                    _sources.Remove(src);
                    continue;
                }

                for (int j = 0; j < n; j++) buffer[offset + j] += _scratch[j];
                if (n < count) _sources.Remove(src);
            }
        }

        return count;
    }
}
