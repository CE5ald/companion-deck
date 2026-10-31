using System.Text.Json;
using CompanionDeck.Host.Protocol;

namespace CompanionDeck.Host.Actions;

/// <summary>
/// One recorded input event. Mouse positions are stored as 0..65535 across the whole virtual
/// desktop, so a macro replays at the same place on the same screen layout whatever the
/// resolution scaling was when it was made.
/// </summary>
public sealed class MacroEvent
{
    /// <summary>Milliseconds since the recording started.</summary>
    public int T { get; set; }
    /// <summary>kd and ku (key), md and mu (mouse button), mm (mouse move), mw (wheel).</summary>
    public string K { get; set; } = "";
    /// <summary>A virtual-key code, a button (1 left, 2 right, 3 middle), or a wheel delta.</summary>
    public int V { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
}

public sealed class Macro
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public long Created { get; set; }
    public List<MacroEvent> Events { get; set; } = new();
}

/// <summary>Where a macro's events go. The real one sends input; a test records what it was given.</summary>
public interface IInputSink
{
    bool Key(ushort vk, bool up);
    bool MouseMove(int x, int y);
    bool MouseButton(int button, bool up);
    bool Wheel(int delta);
}

public static class MacroPlayer
{
    public const int MaxEvents = 400;
    public const int MaxMs = 60_000;

    /// <summary>
    /// Plays events in order, with their original gaps. However it ends - finished, cancelled,
    /// or refused by Windows - every key and button it pressed is released, so a stopped macro
    /// can never leave a modifier held down on the PC.
    /// </summary>
    public static async Task<string?> PlayAsync(IReadOnlyList<MacroEvent> events, IInputSink sink, CancellationToken ct)
    {
        var keys = new HashSet<ushort>();
        var buttons = new HashSet<int>();
        int previous = 0;

        try
        {
            foreach (var e in events)
            {
                int wait = Math.Clamp(e.T - previous, 0, 5000);
                previous = e.T;
                if (wait > 0) await Task.Delay(wait, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                bool ok = true;
                switch (e.K)
                {
                    case "kd": ok = sink.Key((ushort)e.V, false); keys.Add((ushort)e.V); break;
                    case "ku": ok = sink.Key((ushort)e.V, true); keys.Remove((ushort)e.V); break;
                    case "mm": ok = sink.MouseMove(e.X, e.Y); break;
                    case "md": ok = sink.MouseMove(e.X, e.Y) && sink.MouseButton(e.V, false); buttons.Add(e.V); break;
                    case "mu": ok = sink.MouseMove(e.X, e.Y) && sink.MouseButton(e.V, true); buttons.Remove(e.V); break;
                    case "mw": ok = sink.Wheel(e.V); break;
                }
                if (!ok) return "Windows refused the input; an elevated window may have focus";
            }
            return null;
        }
        finally
        {
            foreach (var vk in keys) sink.Key(vk, true);
            foreach (var b in buttons) sink.MouseButton(b, true);
        }
    }

    /// <summary>
    /// Keeps only what a macro is allowed to contain. Used on everything that is stored or read
    /// back, so a hand-edited file goes through the same rules as a recording.
    /// </summary>
    public static bool Sanitise(List<MacroEvent> events, out string? error)
    {
        error = null;
        if (events.Count == 0) { error = "nothing was recorded"; return false; }
        if (events.Count > MaxEvents) { error = "that macro is too long"; return false; }

        int last = 0;
        foreach (var e in events)
        {
            if (e.T < last || e.T > MaxMs + 1000) { error = "bad timing"; return false; }
            last = e.T;
            switch (e.K)
            {
                case "kd": case "ku":
                    if (!KeyNames.IsRecordable(e.V)) { error = "a key a macro may not contain"; return false; }
                    e.X = e.Y = 0;
                    break;
                case "md": case "mu":
                    if (e.V is < 1 or > 3) { error = "bad mouse button"; return false; }
                    if (e.X is < 0 or > 65535 || e.Y is < 0 or > 65535) { error = "bad mouse position"; return false; }
                    break;
                case "mm":
                    if (e.X is < 0 or > 65535 || e.Y is < 0 or > 65535) { error = "bad mouse position"; return false; }
                    e.V = 0;
                    break;
                case "mw":
                    if (e.V is < -1200 or > 1200 || e.V == 0) { error = "bad wheel step"; return false; }
                    e.X = e.Y = 0;
                    break;
                default:
                    error = "unknown event"; return false;
            }
        }
        return true;
    }
}

/// <summary>Saved macros. Steps in a tile name one by id; the events themselves never travel.</summary>
public sealed class MacroStore
{
    public const int MaxMacros = 24;

    private readonly string _path;
    private readonly object _gate = new();
    private List<Macro> _items = new();

    public MacroStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CompanionDeck", "macros.json");
    }

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return;
                var items = JsonSerializer.Deserialize<List<Macro>>(File.ReadAllBytes(_path), Json.Options) ?? new();
                items.RemoveAll(m => m.Id.Length == 0 || !MacroPlayer.Sanitise(m.Events, out _));
                _items = items.Take(MaxMacros).ToList();
            }
            catch (Exception ex) { Log.Warn($"macros.json unreadable: {ex.Message}"); }
        }
    }

    public Macro? Find(string id)
    {
        lock (_gate) return _items.FirstOrDefault(m => m.Id == id);
    }

    public bool Add(string? name, List<MacroEvent> events, out string? error)
    {
        error = null;
        if (!MacroPlayer.Sanitise(events, out error)) return false;
        lock (_gate)
        {
            if (_items.Count >= MaxMacros) { error = "too many macros; delete one first"; return false; }
            string clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (clean.Length > 24) clean = clean[..24].TrimEnd();
            if (clean.Length == 0) clean = $"Macro {_items.Count + 1}";
            _items.Add(new Macro
            {
                Id = "m" + Guid.NewGuid().ToString("N")[..8],
                Name = clean,
                Created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Events = events,
            });
            Save();
            return true;
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            if (_items.RemoveAll(m => m.Id == id) == 0) return false;
            Save();
            return true;
        }
    }

    public List<MacroInfo> Summary()
    {
        lock (_gate)
            return _items.Select(m => new MacroInfo
            {
                Id = m.Id, Name = m.Name, Created = m.Created, Events = m.Events.Count,
                Seconds = Math.Round(m.Events[^1].T / 1000.0, 1),
            }).ToList();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(_items, Json.Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) { Log.Warn($"could not save macros.json: {ex.Message}"); }
    }
}
