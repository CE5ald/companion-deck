using System.Text.Json;
using CompanionDeck.Host.Monitoring;
using CompanionDeck.Host.Protocol;

namespace CompanionDeck.Host.Server;

public sealed class SettingsData
{
    /// <summary>Show Windows notifications on the tablet. Off until someone switches it on.</summary>
    public bool Notifications { get; set; }
    public AlertConfig Alerts { get; set; } = new();
    /// <summary>Keep a record of each gaming session. On, because it costs nothing between games.</summary>
    public bool Sessions { get; set; } = true;
    /// <summary>Raise the performance mode while a game runs on AC power, and put it back after.</summary>
    public bool AutoPower { get; set; }
}

/// <summary>Choices that belong to the PC and outlive the tablet, kept in one small file.</summary>
public sealed class HostSettings
{
    private readonly string _path;
    private readonly object _gate = new();
    private SettingsData _data = new();

    public bool Notifications { get { lock (_gate) return _data.Notifications; } }
    public bool Sessions { get { lock (_gate) return _data.Sessions; } }
    public bool AutoPower { get { lock (_gate) return _data.AutoPower; } }
    public AlertConfig Alerts { get { lock (_gate) return _data.Alerts.Clamped(); } }

    public HostSettings(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CompanionDeck", "settings.json");
    }

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return;
                var loaded = JsonSerializer.Deserialize<SettingsData>(File.ReadAllBytes(_path), Json.Options);
                if (loaded is not null) _data = loaded;
                _data.Alerts = (_data.Alerts ?? new AlertConfig()).Clamped();
            }
            catch (Exception ex) { Log.Warn($"settings.json unreadable: {ex.Message}"); }
        }
    }

    public void SetNotifications(bool on) => Update(d => d.Notifications = on);
    public void SetSessions(bool on) => Update(d => d.Sessions = on);
    public void SetAutoPower(bool on) => Update(d => d.AutoPower = on);
    public void SetAlerts(AlertConfig cfg) => Update(d => d.Alerts = cfg.Clamped());

    private void Update(Action<SettingsData> change)
    {
        lock (_gate)
        {
            change(_data);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(_data, Json.Options));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex) { Log.Warn($"could not save settings.json: {ex.Message}"); }
        }
    }
}
