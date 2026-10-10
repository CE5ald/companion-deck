using System.Text.Json;
using System.Text.Json.Serialization;

namespace CompanionDeck.Host.Launcher;

/// <summary>
/// Which launcher entries are pinned and which are hidden, persisted between runs. Scanning the
/// Start Menu finds a hundred-odd entries and sorts them alphabetically, which puts "Access"
/// above a game you launch every day.
/// </summary>
public sealed class LauncherPrefs
{
    private sealed class Model
    {
        [JsonPropertyName("favourites")] public List<string> Favourites { get; set; } = new();
        [JsonPropertyName("hidden")] public List<string> Hidden { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private Model _model = new();

    public LauncherPrefs()
    {
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CompanionDeck", "launcher.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<Model>(File.ReadAllText(_path));
            if (loaded is not null) _model = loaded;
            Log.Info($"launcher prefs: {_model.Favourites.Count} pinned, {_model.Hidden.Count} hidden");
        }
        catch (Exception ex) { Log.Warn($"launcher prefs unreadable, starting fresh: {ex.Message}"); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Write then move, so an interrupted save cannot leave a truncated file behind.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_model, JsonOpts));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) { Log.Warn($"could not save launcher prefs: {ex.Message}"); }
    }

    public bool IsFavourite(string id)
    {
        lock (_gate) return _model.Favourites.Contains(id);
    }

    public bool IsHidden(string id)
    {
        lock (_gate) return _model.Hidden.Contains(id);
    }

    /// <summary>Pinned order is the order they were pinned in, which is predictable.</summary>
    public int FavouriteRank(string id)
    {
        lock (_gate)
        {
            int i = _model.Favourites.IndexOf(id);
            return i < 0 ? int.MaxValue : i;
        }
    }

    public void SetFavourite(string id, bool value)
    {
        lock (_gate)
        {
            if (value)
            {
                if (_model.Favourites.Contains(id)) return;
                _model.Favourites.Add(id);
                _model.Hidden.Remove(id);   // pinning something hidden implies unhiding it
            }
            else if (!_model.Favourites.Remove(id)) return;
            Save();
        }
    }

    public void SetHidden(string id, bool value)
    {
        lock (_gate)
        {
            if (value)
            {
                if (_model.Hidden.Contains(id)) return;
                _model.Hidden.Add(id);
                _model.Favourites.Remove(id);
            }
            else if (!_model.Hidden.Remove(id)) return;
            Save();
        }
    }
}
