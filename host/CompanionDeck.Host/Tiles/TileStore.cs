using System.Text.Json;
using System.Text.RegularExpressions;
using CompanionDeck.Host.Actions;
using CompanionDeck.Host.Protocol;

namespace CompanionDeck.Host.Tiles;

/// <summary>
/// The tile layouts, kept on the PC. The tablet draws them and asks for one to run by id; it
/// never supplies a command line, and anything it sends to change a layout is checked here
/// against a closed vocabulary before it is kept.
/// </summary>
public sealed partial class TileStore
{
    public const string DefaultId = "default";
    public const int MaxProfiles = 16;
    public const int MaxTiles = 40;
    public const int MaxSteps = 12;
    public const int MaxTotalWaitMs = 30_000;

    /// <summary>What a "sys" step may name. The same list the Actions page uses.</summary>
    public static readonly string[] SysActions =
    {
        "start", "search", "desktop", "taskmgr", "screenshot", "explorer", "calc", "settings",
        "dismiss", "lock", "closewindow", "sleep", "restart", "shutdown",
    };

    /// <summary>Actions that discard work or end the session, so the tile must be held.</summary>
    private static readonly HashSet<string> Destructive = new() { "closewindow", "sleep", "restart", "shutdown" };

    private static readonly string[] AudioVerbs =
        { "masterMuteToggle", "micMuteToggle", "deafenToggle", "masterVolume", "setOutput" };
    private static readonly string[] MediaVerbs = { "playpause", "next", "previous" };
    private static readonly string[] SoundVerbs = { "play", "stop" };
    private static readonly string[] TimerVerbs = { "countdown", "stopwatch", "pomodoro", "clear" };

    private static readonly string[] Colours =
        { "accent", "pink", "violet", "amber", "green", "red", "blue" };

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex IdPattern();
    [GeneratedRegex("^i-[a-z0-9-]{1,24}$")]
    private static partial Regex IconPattern();
    [GeneratedRegex("^[A-Za-z0-9 ._@#:+()\\-]{1,64}$")]
    private static partial Regex MatchPattern();

    private readonly string _path;
    private readonly object _gate = new();
    private TileDocument _doc = new();

    public TileStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CompanionDeck", "tiles.json");
    }

    public string FilePath => _path;

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                {
                    var doc = JsonSerializer.Deserialize<TileDocument>(File.ReadAllBytes(_path), Json.Options);
                    if (doc is not null && Repair(doc)) { _doc = doc; return; }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"tiles.json unreadable ({ex.Message}); keeping a copy and starting again");
                try { File.Move(_path, _path + ".bad", overwrite: true); } catch { }
            }

            _doc = Seed();
            SaveLocked();
        }
    }

    /// <summary>A copy, so a caller serialising it cannot race an edit.</summary>
    public TileDocument Snapshot()
    {
        lock (_gate)
            return JsonSerializer.Deserialize<TileDocument>(JsonSerializer.SerializeToUtf8Bytes(_doc, Json.Options), Json.Options)!;
    }

    /// <summary>The profile to show: the pinned one, else the first whose match fits, else the default.</summary>
    public (TileProfile profile, bool auto) Resolve(string? foregroundProcess, string mode)
    {
        lock (_gate)
        {
            if (_doc.Pinned is { } pin && _doc.Profiles.FirstOrDefault(p => p.Id == pin) is { } pinned)
                return (pinned, false);

            string fg = Normalise(foregroundProcess);
            foreach (var p in _doc.Profiles)
            {
                if (p.Id == DefaultId || p.Match.Count == 0) continue;
                foreach (var m in p.Match)
                {
                    if (m.StartsWith('@') ? string.Equals(m[1..], mode, StringComparison.OrdinalIgnoreCase)
                                          : fg.Length > 0 && string.Equals(Normalise(m), fg, StringComparison.OrdinalIgnoreCase))
                        return (p, true);
                }
            }
            return (_doc.Profiles.First(p => p.Id == DefaultId), true);
        }
    }

    public Tile? FindTile(string? profileId, string tileId, string? foregroundProcess, string mode)
    {
        lock (_gate)
        {
            if (profileId is not null &&
                _doc.Profiles.FirstOrDefault(p => p.Id == profileId)?.Tiles.FirstOrDefault(t => t.Id == tileId) is { } direct)
                return direct;

            var (active, _) = Resolve(foregroundProcess, mode);
            return active.Tiles.FirstOrDefault(t => t.Id == tileId)
                   ?? _doc.Profiles.First(p => p.Id == DefaultId).Tiles.FirstOrDefault(t => t.Id == tileId);
        }
    }

    public bool Pin(string? profileId, out string? error)
    {
        error = null;
        lock (_gate)
        {
            if (profileId is not null && _doc.Profiles.All(p => p.Id != profileId))
            { error = "unknown profile"; return false; }
            _doc.Pinned = profileId;
            SaveLocked();
            return true;
        }
    }

    public bool DeleteProfile(string id, out string? error)
    {
        error = null;
        lock (_gate)
        {
            if (id == DefaultId) { error = "the default profile cannot be removed"; return false; }
            if (_doc.Profiles.RemoveAll(p => p.Id == id) == 0) { error = "unknown profile"; return false; }
            if (_doc.Pinned == id) _doc.Pinned = null;
            SaveLocked();
            return true;
        }
    }

    /// <summary>Validates and stores one whole profile, replacing any with the same id.</summary>
    public bool SetProfile(JsonElement raw, out string? error)
    {
        error = null;
        TileProfile? profile;
        try { profile = JsonSerializer.Deserialize<TileProfile>(raw.GetRawText(), Json.Options); }
        catch (JsonException) { error = "that profile is not valid"; return false; }

        if (profile is null || !Sanitise(profile, out error)) return false;

        lock (_gate)
        {
            int at = _doc.Profiles.FindIndex(p => p.Id == profile.Id);
            if (at < 0 && _doc.Profiles.Count >= MaxProfiles) { error = "too many profiles"; return false; }

            // The default is the fallback for every other profile and must keep matching
            // nothing, or it could shadow the rest.
            if (profile.Id == DefaultId) profile.Match.Clear();

            if (at >= 0) _doc.Profiles[at] = profile;
            else _doc.Profiles.Add(profile);
            SaveLocked();
            return true;
        }
    }

    public static bool Sanitise(TileProfile p, out string? error)
    {
        error = null;
        if (!IdPattern().IsMatch(p.Id ?? "")) { error = "bad profile id"; return false; }
        p.Name = Clean(p.Name, 24);
        if (p.Name.Length == 0) { error = "a profile needs a name"; return false; }

        p.Match ??= new();
        if (p.Match.Count > 8) { error = "too many apps in one profile"; return false; }
        for (int i = 0; i < p.Match.Count; i++)
        {
            p.Match[i] = (p.Match[i] ?? "").Trim();
            if (!MatchPattern().IsMatch(p.Match[i])) { error = "bad app name"; return false; }
        }

        p.Tiles ??= new();
        if (p.Tiles.Count > MaxTiles) { error = "too many tiles"; return false; }

        var seen = new HashSet<string>();
        foreach (var t in p.Tiles)
        {
            if (!SanitiseTile(t, out error)) return false;
            if (!seen.Add(t.Id)) { error = "duplicate tile id"; return false; }
        }
        return true;
    }

    private static bool SanitiseTile(Tile t, out string? error)
    {
        error = null;
        if (!IdPattern().IsMatch(t.Id ?? "")) { error = "bad tile id"; return false; }
        t.Name = Clean(t.Name, 24);
        if (t.Name.Length == 0) { error = "a tile needs a name"; return false; }
        if (!IconPattern().IsMatch(t.Icon ?? "")) t.Icon = "i-bolt";
        if (t.Color is not null && !Colours.Contains(t.Color)) t.Color = null;

        t.Steps ??= new();
        if (t.Steps.Count is 0 or > MaxSteps) { error = $"a tile needs 1 to {MaxSteps} steps"; return false; }

        int waited = 0;
        foreach (var s in t.Steps)
        {
            if (!SanitiseStep(s, out error)) return false;
            if (s.Do == "wait") waited += s.Ms!.Value;
            if (s is { Do: "sys", Id: { } id } && Destructive.Contains(id)) t.Confirm = true;
        }
        if (waited > MaxTotalWaitMs) { error = "the waits add up to more than 30 seconds"; return false; }
        return true;
    }

    private static bool SanitiseStep(TileStep s, out string? error)
    {
        error = null;
        switch (s.Do)
        {
            case "sys":
                if (s.Id is null || !SysActions.Contains(s.Id)) { error = "unknown system action"; return false; }
                s.Keys = null; s.Verb = null; s.Value = null; s.Ms = null;
                return true;

            case "key":
                if (!KeyNames.TryParse(s.Keys, out _)) { error = "unknown key in a shortcut"; return false; }
                s.Keys = s.Keys!.Trim().ToLowerInvariant();
                s.Id = null; s.Verb = null; s.Value = null; s.Ms = null;
                return true;

            case "launch":
            case "sound" when s.Verb != "stop":
                if (string.IsNullOrEmpty(s.Id) || s.Id.Length > 200) { error = "a step is missing its target"; return false; }
                if (s.Do == "sound") s.Verb = "play";
                s.Keys = null; s.Value = null; s.Ms = null;
                if (s.Do == "launch") s.Verb = null;
                return true;

            case "sound":
                s.Id = null; s.Keys = null; s.Value = null; s.Ms = null;
                return true;

            case "audio":
                if (s.Verb is null || !AudioVerbs.Contains(s.Verb)) { error = "unknown audio step"; return false; }
                if (s.Verb == "masterVolume")
                {
                    if (s.Value is null or < 0 or > 100) { error = "volume must be 0 to 100"; return false; }
                }
                else s.Value = null;
                if (s.Verb == "setOutput")
                {
                    if (string.IsNullOrEmpty(s.Id) || s.Id.Length > 200) { error = "pick an output device"; return false; }
                }
                else s.Id = null;
                s.Keys = null; s.Ms = null;
                return true;

            case "media":
                if (s.Verb is null || !MediaVerbs.Contains(s.Verb)) { error = "unknown media step"; return false; }
                s.Id = null; s.Keys = null; s.Value = null; s.Ms = null;
                return true;

            case "wait":
                if (s.Ms is null or < 50 or > 10_000) { error = "a wait is 50 ms to 10 seconds"; return false; }
                s.Id = null; s.Keys = null; s.Verb = null; s.Value = null;
                return true;

            case "macro":
                if (string.IsNullOrEmpty(s.Id) || s.Id.Length > 40) { error = "pick a macro"; return false; }
                s.Keys = null; s.Verb = null; s.Value = null; s.Ms = null;
                return true;

            case "timer":
                if (s.Verb is null || !TimerVerbs.Contains(s.Verb)) { error = "unknown timer step"; return false; }
                if (s.Verb == "countdown")
                {
                    if (s.Value is null or < 1 or > 86_400) { error = "a timer is 1 second to 24 hours"; return false; }
                }
                else s.Value = null;
                s.Id = null; s.Keys = null; s.Ms = null;
                return true;

            default:
                error = "unknown step kind";
                return false;
        }
    }

    private static string Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var chars = s.Where(c => !char.IsControl(c)).ToArray();
        var t = new string(chars).Trim();
        return t.Length > max ? t[..max].TrimEnd() : t;
    }

    private static string Normalise(string? process)
    {
        if (string.IsNullOrWhiteSpace(process)) return "";
        var p = process.Trim();
        return p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p;
    }

    /// <summary>
    /// Makes a document read from disk safe to use. Disk is not trusted either: a hand edit
    /// goes through the same rules as a tablet edit, and anything that fails is dropped.
    /// </summary>
    private static bool Repair(TileDocument doc)
    {
        doc.Profiles ??= new();
        doc.Profiles.RemoveAll(p => !Sanitise(p, out _));
        if (doc.Profiles.All(p => p.Id != DefaultId)) return false;
        doc.Profiles.First(p => p.Id == DefaultId).Match.Clear();
        if (doc.Pinned is not null && doc.Profiles.All(p => p.Id != doc.Pinned)) doc.Pinned = null;
        return true;
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            // Written beside and moved over, so a crash mid-write cannot leave half a layout.
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(_doc, new JsonSerializerOptions(Json.Options) { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) { Log.Warn($"could not save tiles.json: {ex.Message}"); }
    }

    private static Tile T(string id, string name, string icon, string? colour, params TileStep[] steps)
    {
        var t = new Tile { Id = id, Name = name, Icon = icon, Color = colour, Steps = steps.ToList() };
        SanitiseTile(t, out _);
        return t;
    }

    private static TileStep Sys(string id) => new() { Do = "sys", Id = id };
    private static TileStep Key(string keys) => new() { Do = "key", Keys = keys };
    private static TileStep Audio(string verb) => new() { Do = "audio", Verb = verb };
    private static TileStep Media(string verb) => new() { Do = "media", Verb = verb };

    /// <summary>
    /// What a new install starts with: a general set, and one profile that takes over when a
    /// game is in front, so the automatic switch is visible before anyone has configured it.
    /// </summary>
    public static TileDocument Seed() => new()
    {
        Profiles =
        {
            new TileProfile
            {
                Id = DefaultId, Name = "Desktop",
                Tiles =
                {
                    T("mic", "Mic mute", "i-mic-off", "red", Audio("micMuteToggle")),
                    T("deafen", "Deafen", "i-mute", "red", Audio("deafenToggle")),
                    T("playpause", "Play / pause", "i-play", "accent", Media("playpause")),
                    T("next", "Next track", "i-next", null, Media("next")),
                    T("shot", "Screenshot", "i-camera", null, Sys("screenshot")),
                    T("desktop", "Show desktop", "i-monitor", null, Sys("desktop")),
                    T("search", "Search", "i-search", null, Sys("search")),
                    T("taskmgr", "Task Manager", "i-chip", null, Sys("taskmgr")),
                    T("lock", "Lock", "i-lock", "amber", Sys("lock")),
                },
            },
            new TileProfile
            {
                Id = "gaming", Name = "Gaming", Match = { "@gaming" },
                Tiles =
                {
                    T("mic", "Mic mute", "i-mic-off", "red", Audio("micMuteToggle")),
                    T("deafen", "Deafen", "i-mute", "red", Audio("deafenToggle")),
                    T("gamebar", "Game Bar", "i-game", "violet", Key("win+g")),
                    T("record", "Record", "i-rec", "red", Key("win+alt+r")),
                    T("clip", "Screenshot", "i-camera", null, Key("win+alt+prtsc")),
                    T("overlay", "NVIDIA overlay", "i-chip", "green", Key("alt+z")),
                    T("steam", "Steam overlay", "i-apps", "blue", Key("shift+tab")),
                    T("playpause", "Play / pause", "i-play", "accent", Media("playpause")),
                },
            },
        },
    };
}
