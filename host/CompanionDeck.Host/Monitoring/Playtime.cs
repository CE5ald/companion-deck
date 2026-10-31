using System.Text.Json;

namespace CompanionDeck.Host.Monitoring;

/// <summary>
/// Time spent in each game and when it was last played, counted in seconds with the game in front.
/// Written out at most once a minute, so a long evening is not a thousand disk writes.
/// </summary>
public sealed class Playtime
{
    public sealed class Entry
    {
        public long Seconds { get; set; }
        public long LastMs { get; set; }
    }

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, Entry> _items = new();
    private bool _dirty;
    private long _lastSaveMs;

    public Playtime(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CompanionDeck", "playtime.json");
    }

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return;
                _items = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllBytes(_path)) ?? new();
            }
            catch (Exception ex) { Log.Warn($"playtime.json unreadable: {ex.Message}"); }
        }
    }

    public Entry? Get(string id)
    {
        lock (_gate)
            return _items.TryGetValue(id, out var e) ? new Entry { Seconds = e.Seconds, LastMs = e.LastMs } : null;
    }

    public void Add(string id, int seconds, long nowMs)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var e)) _items[id] = e = new Entry();
            e.Seconds += seconds;
            e.LastMs = nowMs;
            _dirty = true;
            if (nowMs - _lastSaveMs >= 60_000) SaveLocked(nowMs);
        }
    }

    public void Flush()
    {
        lock (_gate) { if (_dirty) SaveLocked(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); }
    }

    private void SaveLocked(long nowMs)
    {
        _lastSaveMs = nowMs;
        _dirty = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(_items));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) { Log.Warn($"could not save playtime.json: {ex.Message}"); }
    }
}
