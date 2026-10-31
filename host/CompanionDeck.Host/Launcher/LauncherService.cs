using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using CompanionDeck.Host.Assets;
using CompanionDeck.Host.Protocol;
using Microsoft.Win32;

namespace CompanionDeck.Host.Launcher;

/// <summary>
/// Builds the catalogue of things worth launching from the tablet: Steam and Epic games with
/// their real artwork, plus applications from the Start Menu with their shell icons. The scan is
/// expensive (disk walking and icon rasterising) and the results barely change, so it runs once
/// at startup and then only on demand.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class LauncherService
{
    private readonly AssetCache _assets;
    private List<Entry> _entries = new();

    public LauncherPrefs Prefs { get; }

    public sealed class Entry
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Kind { get; init; }     // game | app
        public required string Source { get; init; }   // steam | epic | shortcut
        public required string Target { get; init; }   // URI or path handed to ShellExecute
        public string? IconKey { get; set; }
        public string? ExeName { get; init; }          // for "is it running" and game detection
        /// <summary>The folder the game is installed in. Recognises the game's real process whatever it is called.</summary>
        public string? InstallDir { get; init; }
    }

    public LauncherService(AssetCache assets, string? prefsPath = null)
    {
        _assets = assets;
        Prefs = new LauncherPrefs(prefsPath);
    }

    /// <summary>For the logic checks: a catalogue without scanning the machine.</summary>
    internal void SetEntries(IEnumerable<Entry> entries) => _entries = entries.ToList();

    public IReadOnlyList<Entry> Entries => _entries;

    public IEnumerable<string> KnownGameExecutables =>
        _entries.Where(e => e.Kind == "game" && e.ExeName is { Length: > 0 })
                .Select(e => e.ExeName!);

    public Entry? Find(string id) => _entries.FirstOrDefault(e => e.Id == id);

    /// <summary>
    /// The game a running process belongs to: by program name where the store recorded one, otherwise by
    /// whether the program lives inside the game's install folder.
    /// </summary>
    public Entry? GameForProcess(int pid, string? processName)
    {
        var games = _entries.Where(e => e.Kind == "game").ToList();
        if (processName is { Length: > 0 })
            foreach (var g in games)
                if (g.ExeName is { Length: > 0 } && string.Equals(g.ExeName, processName, StringComparison.OrdinalIgnoreCase))
                    return g;

        if (!games.Any(g => g.InstallDir is not null)) return null;
        var path = Context.ProcessPaths.PathOf(pid);
        if (path is null) return null;
        return games.FirstOrDefault(g => g.InstallDir is not null && Context.ProcessPaths.IsInside(path, g.InstallDir));
    }


    public void Scan()
    {
        var sw = Stopwatch.StartNew();
        var list = new List<Entry>();

        try { list.AddRange(ScanSteam()); } catch (Exception ex) { Log.Warn($"steam scan: {ex.Message}"); }
        try { list.AddRange(ScanEpic()); } catch (Exception ex) { Log.Warn($"epic scan: {ex.Message}"); }
        try { list.AddRange(ScanStartMenu()); } catch (Exception ex) { Log.Warn($"start menu scan: {ex.Message}"); }

        // Same app reached two ways (a Start Menu shortcut to a Steam game) - prefer the
        // launcher entry, which has real cover art and a proper protocol handler.
        var deduped = list
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(e => e.Source == "shortcut" ? 1 : 0).First())
            .OrderBy(e => e.Kind == "game" ? 0 : 1)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _entries = deduped;
        Log.Info($"launcher: {deduped.Count} entries " +
                 $"({deduped.Count(e => e.Kind == "game")} games) in {sw.ElapsedMilliseconds}ms");
    }


    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathRegex();

    [GeneratedRegex("\"appid\"\\s+\"(\\d+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex AppIdRegex();

    [GeneratedRegex("\"name\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex NameRegex();

    [GeneratedRegex("\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex InstallDirRegex();

    private IEnumerable<Entry> ScanSteam()
    {
        string? steam = SteamPath();
        if (steam is null || !Directory.Exists(steam)) yield break;

        foreach (var library in SteamLibraries(steam))
        {
            var apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;

            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                Entry? entry = null;
                try
                {
                    var text = File.ReadAllText(acf);
                    var id = AppIdRegex().Match(text);
                    var name = NameRegex().Match(text);
                    if (!id.Success || !name.Success) continue;

                    string appId = id.Groups[1].Value;
                    // Redistributables and runtimes are installed as "games"; nobody launches them.
                    if (name.Groups[1].Value.Contains("Redistributable", StringComparison.OrdinalIgnoreCase) ||
                        name.Groups[1].Value.Contains("Steamworks", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var installDir = InstallDirRegex().Match(text);
                    entry = new Entry
                    {
                        Id = "steam:" + appId,
                        Name = name.Groups[1].Value,
                        Kind = "game",
                        Source = "steam",
                        Target = $"steam://rungameid/{appId}",
                        InstallDir = installDir.Success
                            ? Path.Combine(library, "steamapps", "common", installDir.Groups[1].Value) : null,
                    };
                    entry.IconKey = SteamArtwork(steam, appId);
                }
                catch (Exception ex) { Log.Debug($"acf {acf}: {ex.Message}"); }

                if (entry is not null) yield return entry;
            }
        }
    }

    private static string? SteamPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") as string;
        }
        catch { return null; }
    }

    private static IEnumerable<string> SteamLibraries(string steam)
    {
        yield return steam;

        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string text;
        try { text = File.ReadAllText(vdf); } catch { yield break; }

        foreach (Match m in LibraryPathRegex().Matches(text))
        {
            var p = m.Groups[1].Value.Replace(@"\\", @"\");
            if (Directory.Exists(p)) yield return p;
        }
    }

    /// <summary>
    /// Steam caches library artwork locally. The layout changed between client versions,
    /// so several known locations are tried before giving up and letting the tile fall
    /// back to a generic icon.
    /// </summary>
    private string? SteamArtwork(string steam, string appId)
    {
        var cache = Path.Combine(steam, "appcache", "librarycache");
        var candidates = new[]
        {
            Path.Combine(cache, appId, "library_600x900.jpg"),
            Path.Combine(cache, $"{appId}_library_600x900.jpg"),
            Path.Combine(cache, appId, "header.jpg"),
            Path.Combine(cache, $"{appId}_header.jpg"),
        };

        foreach (var c in candidates)
        {
            try
            {
                if (!File.Exists(c)) continue;
                var bytes = File.ReadAllBytes(c);
                if (bytes.Length is 0 or > 4_000_000) continue;
                return _assets.Put(bytes);
            }
            catch (Exception ex) { Log.Debug($"steam art {c}: {ex.Message}"); }
        }
        return null;
    }


    private IEnumerable<Entry> ScanEpic()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");

        if (!Directory.Exists(dir)) yield break;

        foreach (var file in Directory.EnumerateFiles(dir, "*.item"))
        {
            Entry? entry = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;

                string? name = Str(root, "DisplayName");
                string? appName = Str(root, "AppName");
                if (name is null || appName is null) continue;

                // Plugins and engine components share the manifest folder with games.
                string? category = Str(root, "AppCategories");
                if (Str(root, "bIsIncompleteInstall") == "true") continue;

                string? exe = Str(root, "LaunchExecutable");

                entry = new Entry
                {
                    Id = "epic:" + appName,
                    Name = name,
                    Kind = "game",
                    Source = "epic",
                    Target = $"com.epicgames.launcher://apps/{appName}?action=launch&silent=true",
                    ExeName = exe is null ? null : Path.GetFileNameWithoutExtension(exe),
                    InstallDir = Str(root, "InstallLocation"),
                };

                string? install = Str(root, "InstallLocation");
                if (install is not null && exe is not null)
                {
                    var full = Path.Combine(install, exe);
                    if (File.Exists(full) && IconExtractor.GetPng(full) is { } png)
                        entry.IconKey = _assets.Put(png);
                }
            }
            catch (Exception ex) { Log.Debug($"epic manifest {file}: {ex.Message}"); }

            if (entry is not null) yield return entry;
        }
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            }
            : null;


    private static readonly string[] NoiseWords =
    {
        "uninstall", "readme", "help", "documentation", "website", "manual",
        "release notes", "support", "eula", "license", "changelog", "report a",
        "command prompt", "powershell", "registry editor", "odbc", "character map",
        "system information", "resource monitor", "component services", "event viewer",
        "disk cleanup", "defragment", "iscsi", "recovery drive", "steps recorder",
        "windows memory diagnostic", "performance monitor", "print management",
        "services", "task scheduler", "wordpad", "xps viewer", "math input",
        "quick assist", "remote desktop connection", "windows fax",
    };

    /// <summary>
    /// Start Menu folders that are essentially never what someone reaches for from a
    /// tablet beside the keyboard. Filtering by folder catches far more noise than name
    /// matching alone, and it does it without guessing at individual products.
    /// </summary>
    private static readonly string[] NoiseFolders =
    {
        "administrative tools", "windows administrative tools", "windows tools",
        "windows accessories", "windows system", "windows powershell",
        "windows ease of access", "accessibility", "maintenance", "startup",
        "windows kits", "microsoft office tools", "java development kit",
        "visual studio tools", "accessories", "system tools", "windows security",
    };

    private IEnumerable<Entry> ScanStartMenu()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories); }
            catch (Exception ex) { Log.Debug($"start menu {root}: {ex.Message}"); continue; }

            foreach (var lnk in files)
            {
                Entry? entry = null;
                try
                {
                    var name = Path.GetFileNameWithoutExtension(lnk);
                    if (name.Length == 0) continue;
                    if (NoiseWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;

                    // Compare only the part of the path below the Programs root, so a user
                    // folder that happens to sit under a matching name is not caught.
                    var relative = Path.GetRelativePath(root, lnk).Replace('\\', '/');
                    var folder = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? "";
                    if (folder.Length > 0 && NoiseFolders.Any(f =>
                            folder.Contains(f, StringComparison.OrdinalIgnoreCase))) continue;

                    if (!seen.Add(name)) continue;

                    entry = new Entry
                    {
                        Id = "lnk:" + name.ToLowerInvariant().Replace(' ', '-'),
                        Name = name,
                        Kind = "app",
                        Source = "shortcut",
                        Target = lnk,
                    };

                    if (IconExtractor.GetPng(lnk) is { } png) entry.IconKey = _assets.Put(png);
                }
                catch (Exception ex) { Log.Debug($"shortcut {lnk}: {ex.Message}"); }

                if (entry is not null) yield return entry;
            }
        }
    }


    /// <param name="playedAtMs">When a game was last in front, from the playtime record, or null.
    /// Counted alongside launches, because a game started from Steam never passed through here.</param>
    public AppsState Snapshot(Func<string, long?>? playedAtMs = null)
    {
        var running = RunningProcessNames();
        var runningPaths = _entries.Any(e => e.InstallDir is not null) ? RunningImagePaths() : new List<string>();

        // Pinned entries first in the order they were pinned. Then whatever this person actually
        // uses, most recent first; then games; then the rest alphabetically. A PC with no history
        // therefore falls back to the plain order, and one with history leads with its own habits.
        // Hidden entries stay in the payload so the tablet's edit mode can offer to restore them;
        // it filters them out of the normal view.
        var items = _entries.Select(e => new LaunchTarget
        {
            Id = e.Id,
            Name = e.Name,
            Kind = e.Kind,
            Source = e.Source,
            IconKey = e.IconKey,
            Running = (e.ExeName is { Length: > 0 } && running.Contains(e.ExeName))
                      || (e.InstallDir is not null && runningPaths.Any(p => Context.ProcessPaths.IsInside(p, e.InstallDir))),
            Fav = Prefs.IsFavourite(e.Id),
            Hidden = Prefs.IsHidden(e.Id),
        })
        .OrderBy(t => t.Fav ? 0 : 1)
        .ThenBy(t => t.Fav ? Prefs.FavouriteRank(t.Id) : 0)
        .ThenByDescending(t => Math.Max(Prefs.LastUsedMs(t.Id), playedAtMs?.Invoke(t.Id) ?? 0))
        .ThenBy(t => t.Kind == "game" ? 0 : 1)
        .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

        return new AppsState { Items = items };
    }

    private static List<string> RunningImagePaths()
    {
        var list = new List<string>();
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try { if (Context.ProcessPaths.PathOf(p.Id) is { } path) list.Add(path); }
                finally { p.Dispose(); }
            }
        }
        catch (Exception ex) { Log.Debug($"process paths: {ex.Message}"); }
        return list;
    }

    private static HashSet<string> RunningProcessNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try { set.Add(p.ProcessName); } catch { }
                finally { p.Dispose(); }
            }
        }
        catch (Exception ex) { Log.Debug($"process enumeration: {ex.Message}"); }
        return set;
    }
}
