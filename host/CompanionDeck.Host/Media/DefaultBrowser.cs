using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace CompanionDeck.Host.Media;

/// <summary>
/// The browser Windows itself would open a web address in. Read from the same place Settings writes
/// it: the user's chosen handler for https, then the program that handler's registration launches.
/// Nothing here names a browser, so Chrome, Edge, Firefox, Brave, Vivaldi, Opera, Arc and whatever is
/// released next year are all found the same way, and the answer follows the user when they change it.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DefaultBrowser
{
    public sealed record Info(string Path, string ProcessName, string DisplayName);

    private static Info? _cached;
    private static DateTime _cachedAt;

    /// <summary>The default browser, or null when none is registered (a fresh install, or a policy-locked PC).</summary>
    public static Info? Resolve()
    {
        // The user can change it any time, but not often enough to read the registry on every call.
        if ((DateTime.UtcNow - _cachedAt).TotalSeconds < 30) return _cached;
        _cachedAt = DateTime.UtcNow;
        _cached = ReadFromRegistry();
        return _cached;
    }

    private static Info? ReadFromRegistry()
    {
        foreach (var scheme in new[] { "https", "http" })
        {
            try
            {
                using var choice = Registry.CurrentUser.OpenSubKey(
                    $@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\{scheme}\UserChoice");
                if (choice?.GetValue("ProgId") is not string progId || progId.Length == 0) continue;
                if (FromProgId(progId) is { } info) return info;
            }
            catch (Exception ex) { Log.Debug($"default browser ({scheme}): {ex.Message}"); }
        }
        return null;
    }

    /// <summary>
    /// What a registered handler (a "ProgId" such as BraveHTML, ChromeHTML, MSEdgeHTM or FirefoxURL-...)
    /// actually runs. This is the whole of the lookup once Windows has said which handler the user chose,
    /// so it is also what the checks exercise with a browser that is installed but not the default.
    /// </summary>
    public static Info? FromProgId(string progId)
    {
        var path = ExeFromCommand(OpenCommand(progId));
        if (path is null || !File.Exists(path)) return null;
        return new Info(path, System.IO.Path.GetFileNameWithoutExtension(path), NameOf(path));
    }

    private static string? OpenCommand(string progId)
    {
        // HKCR is the merged view of the user and machine registrations; reading both explicitly
        // keeps a per-user browser install (Brave, Chrome and Edge all offer one) in view.
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var k = root.OpenSubKey($@"Software\Classes\{progId}\shell\open\command");
            if (k?.GetValue(null) is string cmd && cmd.Length > 0) return cmd;
        }
        return null;
    }

    /// <summary>
    /// The program out of a shell command line such as <c>"C:\Program Files\X\x.exe" --flag "%1"</c>.
    /// Public so the logic checks can pin it down: registrations quote and environment-expand in
    /// several ways, and a wrong answer here would show the wrong browser's icon.
    /// </summary>
    public static string? ExeFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var cmd = Environment.ExpandEnvironmentVariables(command.Trim());

        string exe;
        if (cmd[0] == '"')
        {
            int end = cmd.IndexOf('"', 1);
            if (end < 0) return null;
            exe = cmd[1..end];
        }
        else
        {
            int i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            exe = cmd[..(i + 4)];
        }
        return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : null;
    }

    private static string NameOf(string path)
    {
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            // ProductName is the short brand ("Brave", "Firefox"); FileDescription is often a sentence
            // for the executable ("Google Chrome", "Microsoft Edge"). Prefer the shorter, non-empty one.
            var candidates = new[] { vi.ProductName, vi.FileDescription }
                .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToList();
            if (candidates.Count > 0) return candidates.OrderBy(s => s.Length).First();
        }
        catch { /* fall through to the file name */ }
        return System.IO.Path.GetFileNameWithoutExtension(path);
    }
}
