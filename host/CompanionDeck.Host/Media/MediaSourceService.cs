using System.Diagnostics;
using System.Runtime.Versioning;
using CompanionDeck.Host.Assets;
using CompanionDeck.Host.Launcher;
using CompanionDeck.Host.Protocol;
using Microsoft.Win32;

namespace CompanionDeck.Host.Media;

/// <summary>
/// The short list of places music and video actually get started from, so the Media tab is useful
/// before anything is playing rather than only after. This deliberately mirrors how the launcher
/// already works instead of inventing a second mechanism: the tablet names an <see
/// cref="MediaSource.Id"/> from a catalogue we built, and the executable path or URL behind it
/// never leaves this process.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MediaSourceService
{
    private readonly AssetCache _assets;
    private List<Resolved> _sources = new();

    public MediaSourceService(AssetCache assets) => _assets = assets;

    /// <summary>What a resolved source actually does when launched. Never sent to the tablet.</summary>
    private sealed class Resolved
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        /// <summary>app | web | folder</summary>
        public required string Kind { get; init; }
        public string? IconKey { get; set; }

        /// <summary>Program to run, or null when <see cref="Url"/> goes to the default handler.</summary>
        public string? Exe { get; init; }
        /// <summary>Address or folder passed to <see cref="Exe"/>, or opened directly.</summary>
        public string? Url { get; init; }
        /// <summary>Process name used to show a "running" pip, when we know it.</summary>
        public string? ExeName { get; init; }
        /// <summary>For a web service: which service it is, so the tablet can draw that identity.</summary>
        public string? Service { get; init; }
        /// <summary>For a web service: the browser that will open it, shown as a badge and never as the identity.</summary>
        public string? Via { get; init; }
        public string? ViaIconKey { get; init; }
    }

    public bool Any => _sources.Count > 0;


    /// <summary>
    /// Resolves the catalogue against this machine. Cheap enough to run beside the launcher
    /// scan - it is a handful of registry reads and file probes, and no disk walking.
    /// </summary>
    public void Scan(LauncherService launcher)
    {
        var list = new List<Resolved>();

        // --- desktop players -------------------------------------------------
        // Spotify installs per-user and is not always on the Start Menu in a form the
        // launcher scan keeps, so App Paths is checked first and the catalogue second.
        if (ResolveApp("Spotify.exe", launcher, "spotify") is { } spotify)
        {
            list.Add(new Resolved
            {
                Id = "src:spotify", Name = "Spotify", Kind = "app",
                Exe = spotify.Path, ExeName = "Spotify", IconKey = spotify.IconKey,
            });
        }

        // "Windows Media Player" is the classic desktop player; the modern Store app is a
        // different product with a different launch surface, so both are probed.
        if (ResolveApp("wmplayer.exe", launcher, "windows media player") is { } wmp)
        {
            list.Add(new Resolved
            {
                Id = "src:wmp", Name = "Windows Media Player", Kind = "app",
                Exe = wmp.Path, ExeName = "wmplayer", IconKey = wmp.IconKey,
            });
        }

        foreach (var (exe, name, proc) in new[]
                 {
                     ("vlc.exe", "VLC", "vlc"),
                     ("foobar2000.exe", "foobar2000", "foobar2000"),
                     ("iTunes.exe", "iTunes", "iTunes"),
                     ("Audacious.exe", "Audacious", "audacious"),
                 })
        {
            if (ResolveApp(exe, launcher, name) is { } hit)
            {
                list.Add(new Resolved
                {
                    Id = "src:" + proc.ToLowerInvariant(), Name = name, Kind = "app",
                    Exe = hit.Path, ExeName = proc, IconKey = hit.IconKey,
                });
            }
        }

        // --- web players -----------------------------------------------------
        // Opening Spotify in a browser is a genuinely different thing from opening the
        // desktop client - different account session, different audio session, and it is
        // what someone reaches for when the desktop client is signed into another account.
        // So the browser-backed entries are listed separately rather than collapsed.
        //
        // The browser is whatever Windows says is the default, and it is only ever the vehicle:
        // the entry stays "Spotify Web", and the browser rides along as a badge.
        var browser = ResolveBrowser();

        foreach (var (id, name, url, service) in new[]
                 {
                     ("src:spotify-web", "Spotify Web", "https://open.spotify.com", "spotify"),
                     ("src:ytmusic", "YouTube Music", "https://music.youtube.com", "ytmusic"),
                 })
        {
            list.Add(new Resolved
            {
                Id = id, Name = name, Kind = "web", Service = service,
                // Opened through the shell, which hands the address to the default browser exactly as
                // clicking a link would, profile and all.
                Url = url,

            });
        }

        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (Directory.Exists(music))
        {
            list.Add(new Resolved
            {
                Id = "src:music-folder", Name = "Music", Kind = "folder", Url = music,
            });
        }

        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (Directory.Exists(videos))
        {
            list.Add(new Resolved
            {
                Id = "src:video-folder", Name = "Videos", Kind = "folder", Url = videos,
            });
        }

        _sources = list;
        Log.Info($"media sources: {list.Count} resolved ({string.Join(", ", list.Select(s => s.Name))})");
    }

    private sealed record AppHit(string Path, string? IconKey);
    private sealed record BrowserHit(string Path, string DisplayName, string? IconKey);

    /// <summary>
    /// Finds a program by its registered App Paths entry, falling back to a launcher
    /// catalogue entry with a matching name. Both sources are ones we produced ourselves.
    /// </summary>
    private AppHit? ResolveApp(string exe, LauncherService launcher, string catalogueName)
    {
        if (AppPath(exe) is { } path)
            return new AppHit(path, IconExtractor.GetPng(path) is { } png ? _assets.Put(png) : null);

        // The launcher's own entry already carries an extracted icon, so reuse it rather
        // than rasterising the same image a second time.
        var entry = launcher.Entries.FirstOrDefault(e =>
            e.Name.Equals(catalogueName, StringComparison.OrdinalIgnoreCase) ||
            e.Name.StartsWith(catalogueName, StringComparison.OrdinalIgnoreCase));

        return entry is null ? null : new AppHit(entry.Target, entry.IconKey);
    }

    private readonly Dictionary<string, string?> _browserIcons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which browser is the default right now. Cheap: Windows is asked at most every 30 seconds.</summary>
    public string BrowserKey => DefaultBrowser.Resolve()?.Path ?? "";

    private BrowserHit? ResolveBrowser()
    {
        if (DefaultBrowser.Resolve() is not { } b) return null;
        // The icon is extracted once per browser, not once per call.
        if (!_browserIcons.TryGetValue(b.Path, out var icon))
            _browserIcons[b.Path] = icon = IconExtractor.GetPng(b.Path) is { } png ? _assets.Put(png) : null;
        return new BrowserHit(b.Path, b.DisplayName, icon);
    }

    /// <summary>
    /// The canonical Windows registration for "where is this program installed". Reading it
    /// avoids both guessing at Program Files layouts and resolving .lnk files through COM.
    /// </summary>
    private static string? AppPath(string exe)
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                if (key?.GetValue(null) is string raw)
                {
                    var path = raw.Trim('"');
                    if (File.Exists(path)) return path;
                }
            }
            catch (Exception ex) { Log.Debug($"app path {exe}: {ex.Message}"); }
        }
        return null;
    }


    public MediaSourcesState Snapshot()
    {
        var running = RunningProcessNames();
        // Asked every time, not remembered from the scan: the user can change their default browser while
        // this runs, and the badge should follow.
        var browser = ResolveBrowser();

        return new MediaSourcesState
        {
            Items = _sources.Select(s => new MediaSourceInfo
            {
                Id = s.Id,
                Name = s.Name,
                Kind = s.Kind,
                IconKey = s.IconKey,
                Service = s.Service,
                Via = s.Kind == "web" ? browser?.DisplayName : null,
                ViaIconKey = s.Kind == "web" ? browser?.IconKey : null,
                // A web service is "running" only if a browser happens to be open, which says nothing
                // about it, so it never claims to be.
                Running = s.Kind != "web" && s.ExeName is { Length: > 0 } && running.Contains(s.ExeName),
            }).ToList(),
        };
    }

    public IEnumerable<string?> IconKeys => _sources.Select(s => s.IconKey);

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


    /// <summary>
    /// Opens a source the tablet named. Returns false for an id we did not publish, so an
    /// unknown or crafted id can only ever be a no-op.
    /// </summary>
    public bool Launch(string id)
    {
        var src = _sources.FirstOrDefault(s => s.Id == id);
        if (src is null) return false;

        try
        {
            ProcessStartInfo psi;

            if (src.Kind == "web" || (src.Url is not null && src.Exe is null))
            {
                // An address (ours, not the tablet's) or a folder: the shell picks the handler, which
                // for a web address is the user's own default browser.
                psi = new ProcessStartInfo(src.Url!) { UseShellExecute = true };
            }
            else
            {
                psi = new ProcessStartInfo(src.Exe!) { UseShellExecute = true };
            }

            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"media source '{id}' failed to open: {ex.Message}");
            return false;
        }
    }
}
