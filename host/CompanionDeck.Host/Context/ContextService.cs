using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CompanionDeck.Host.Protocol;

namespace CompanionDeck.Host.Context;

/// <summary>
/// Works out what the laptop is being used for, so the dashboard can lead with whatever matters
/// right now. Detection is deliberately conservative.
/// </summary>
public sealed class ContextService
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr h, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFO mi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
    }

    private const uint MonitorDefaultToNearest = 2;

    /// <summary>Shell surfaces that should never be mistaken for an application in use.</summary>
    private static readonly HashSet<string> Shell = new(StringComparer.OrdinalIgnoreCase)
    { "explorer", "ShellExperienceHost", "SearchHost", "StartMenuExperienceHost", "TextInputHost", "LockApp", "Idle" };

    /// <summary>Executables known to be games, supplied by the launcher catalogue.</summary>
    private volatile HashSet<string> _knownGames = new(StringComparer.OrdinalIgnoreCase);

    private string _pending = "desktop";
    private int _pendingCount;
    private string _mode = "desktop";

    /// <summary>Settled mode, and the process behind the foreground window.</summary>
    public string Mode => _mode;
    public int ForegroundPid { get; private set; }

    public void SetKnownGames(IEnumerable<string> exeNames) =>
        _knownGames = new HashSet<string>(exeNames, StringComparer.OrdinalIgnoreCase);

    /// <param name="gpuPercent">Used as corroboration; a fullscreen window that isn't
    /// working the GPU is more likely a video than a game.</param>
    /// <param name="mediaPlaying">Whether any media session is currently playing.</param>
    public ContextState Sample(double? gpuPercent, bool mediaPlaying, string? mediaApp)
    {
        var hwnd = GetForegroundWindow();
        string proc = "";
        string title = "";
        bool fullscreen = false;

        if (hwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(hwnd, out int pid);
            if (pid > 0)
            {
                ForegroundPid = pid;
                try { using var p = Process.GetProcessById(pid); proc = p.ProcessName; }
                catch { /* process exited between the two calls */ }
            }

            var sb = new StringBuilder(512);
            if (GetWindowTextW(hwnd, sb, sb.Capacity) > 0) title = sb.ToString();

            fullscreen = IsFullscreen(hwnd);
        }

        string observed = Classify(proc, fullscreen, gpuPercent, mediaPlaying, mediaApp);

        // Require two consecutive agreeing samples before switching modes.
        if (observed == _mode) { _pendingCount = 0; }
        else if (observed == _pending)
        {
            if (++_pendingCount >= 2) { _mode = observed; _pendingCount = 0; }
        }
        else { _pending = observed; _pendingCount = 1; }

        return new ContextState
        {
            Mode = _mode,
            ForegroundApp = proc.Length > 0 ? proc : null,
            WindowTitle = title.Length > 0 ? Truncate(title, 90) : null,
            Fullscreen = fullscreen,
        };
    }

    private string Classify(string proc, bool fullscreen, double? gpu, bool mediaPlaying, string? mediaApp)
    {
        if (proc.Length > 0 && _knownGames.Contains(proc)) return "gaming";

        // A fullscreen window driving the GPU hard is a game even if we don't know its name.
        if (fullscreen && !Shell.Contains(proc) && gpu is > 45) return "gaming";

        if (mediaPlaying)
        {
            // Browsers and players in the foreground with playback running: treat a
            // fullscreen or maximised one as video, otherwise it's background music.
            bool foregroundIsPlayer = mediaApp is not null &&
                                      proc.Contains(mediaApp, StringComparison.OrdinalIgnoreCase);
            if (fullscreen && foregroundIsPlayer) return "video";
            if (IsVideoApp(proc) && foregroundIsPlayer) return "video";
            return "music";
        }

        return "desktop";
    }

    private static bool IsVideoApp(string proc) => proc switch
    {
        "vlc" or "mpc-hc64" or "PotPlayerMini64" or "Movies & TV" or "VideoPlayer" => true,
        _ => false,
    };

    private static bool IsFullscreen(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var wr)) return false;

        var mon = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (mon == IntPtr.Zero) return false;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(mon, ref mi)) return false;

        var m = mi.rcMonitor;
        // Allow a pixel of slop; borderless-fullscreen windows are occasionally off by one.
        return wr.Left <= m.Left + 1 && wr.Top <= m.Top + 1 &&
               wr.Right >= m.Right - 1 && wr.Bottom >= m.Bottom - 1;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
