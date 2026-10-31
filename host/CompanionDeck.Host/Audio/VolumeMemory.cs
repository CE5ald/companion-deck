using System.Text.Json;

namespace CompanionDeck.Host.Audio;

/// <summary>
/// Levels the user asked to keep for particular applications, put back each time the
/// application starts. Opt-in per application: an app only appears here after it has been
/// pinned, so nothing is ever restored that the user did not choose.
/// </summary>
public sealed class VolumeMemory
{
    public sealed class Saved
    {
        public int Volume { get; set; }
        public bool Muted { get; set; }
    }

    public const int MaxApps = 64;

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, Saved> _apps = new(StringComparer.OrdinalIgnoreCase);

    public VolumeMemory(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CompanionDeck", "volumes.json");
    }

    public int Count { get { lock (_gate) return _apps.Count; } }

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return;
                var loaded = JsonSerializer.Deserialize<Dictionary<string, Saved>>(File.ReadAllBytes(_path));
                if (loaded is null) return;
                _apps = new Dictionary<string, Saved>(StringComparer.OrdinalIgnoreCase);
                foreach (var (k, v) in loaded)
                    if (k.Length is > 0 and <= 64 && _apps.Count < MaxApps)
                        _apps[k] = new Saved { Volume = Math.Clamp(v.Volume, 0, 100), Muted = v.Muted };
            }
            catch (Exception ex) { Log.Warn($"volumes.json unreadable: {ex.Message}"); }
        }
    }

    public bool Has(string id) { lock (_gate) return _apps.ContainsKey(id); }

    public Saved? Get(string id)
    {
        lock (_gate) return _apps.TryGetValue(id, out var s) ? new Saved { Volume = s.Volume, Muted = s.Muted } : null;
    }

    public bool Set(string id, int volume, bool muted)
    {
        lock (_gate)
        {
            if (!_apps.ContainsKey(id) && _apps.Count >= MaxApps) return false;
            _apps[id] = new Saved { Volume = Math.Clamp(volume, 0, 100), Muted = muted };
            Save();
            return true;
        }
    }

    public void Forget(string id)
    {
        lock (_gate) { if (_apps.Remove(id)) Save(); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(_apps));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) { Log.Warn($"could not save volumes.json: {ex.Message}"); }
    }
}
