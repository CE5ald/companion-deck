using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CompanionDeck.Host.Protocol;
using CompanionDeck.Host.Server;

namespace CompanionDeck.Host.Context;

/// <summary>
/// What is actually running on the laptop right now, and the ability to stop it. Two populations,
/// because they answer different questions.
/// </summary>
public sealed class RunningAppsService
{
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr h, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr h, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr h);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    private const uint GwOwner = 4;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int DwmaCloaked = 14;
    private const uint WmClose = 0x0010;

    /// <summary>
    /// Processes that must never be closed or killed from a tablet. The first group would take
    /// Windows down with them.
    /// </summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        // Critical system
        "System", "Idle", "Registry", "Memory Compression",
        "csrss", "wininit", "winlogon", "services", "lsass", "smss", "svchost",
        "dwm", "fontdrvhost", "sihost", "ctfmon", "audiodg", "conhost", "WUDFHost",
        "LsaIso", "SecurityHealthService", "SecurityHealthSystray",
        // Security software
        "MsMpEng", "NisSrv", "MpDefenderCoreService", "MpCmdRun", "SgrmBroker",
    };

    /// <summary>A background process below this is noise, not a resource problem.</summary>
    private const long BackgroundFloorMb = 120;
    private const int MaxBackground = 12;

    private readonly HashSet<int> _offered = new();
    private readonly HashSet<string> _offeredNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public static bool IsProtectedProcess(Process p) => IsProtected(p);

    /// <summary>Names shown in the top-processes list. Only these may be acted on by name.</summary>
    public void OfferNames(IEnumerable<string> names)
    {
        lock (_gate)
        {
            _offeredNames.Clear();
            foreach (var n in names) _offeredNames.Add(n);
        }
    }

    private List<Process> MatchingOffered(string name, string what)
    {
        bool offered;
        lock (_gate) offered = _offeredNames.Contains(name);
        var result = new List<Process>();
        if (!offered) { Log.Warn($"refused to {what} '{name}': not in the published list"); return result; }

        foreach (var p in Process.GetProcessesByName(name))
        {
            // Re-checked against the live process, as for a single pid.
            if (IsProtected(p)) { Log.Warn($"refused to {what} {p.ProcessName} ({p.Id}): protected"); p.Dispose(); continue; }
            result.Add(p);
        }
        return result;
    }

    /// <summary>Asks every window of every process with this name to close, as its X button would.</summary>
    public int CloseByName(string name)
    {
        var procs = MatchingOffered(name, "close");
        var pids = procs.Select(p => p.Id).ToHashSet();
        foreach (var p in procs) p.Dispose();
        return pids.Count == 0 ? 0 : PostCloseToWindows(pids);
    }

    public int KillByName(string name) => KillByName(name, out _);

    public int KillByName(string name, out string? reason)
    {
        reason = null;
        int ended = 0;
        var matches = MatchingOffered(name, "kill");
        if (matches.Count == 0) reason = "Windows needs this process, or it has already closed";
        foreach (var p in matches)
        {
            using (p)
            {
                try { p.Kill(entireProcessTree: true); ended++; }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
                {
                    reason = Elevation.IsElevated
                        ? "Windows refused: a protection driver is guarding it"
                        : "Windows refused: it runs with higher rights. Run the agent as administrator";
                    Log.Warn($"kill {p.ProcessName} ({p.Id}) denied: {ex.Message}");
                }
                catch (InvalidOperationException) { ended++; }   // already gone
                catch (Exception ex) { reason = ex.Message; Log.Warn($"kill {p.ProcessName} ({p.Id}) failed: {ex.Message}"); }
            }
        }
        if (ended > 0) { Log.Warn($"killed {ended} process(es) named {name}"); reason = null; }
        return ended;
    }

    /// <summary>Lowers (or restores) the priority of every process with this name.</summary>
    public int SetPriorityByName(string name, bool low)
    {
        int changed = 0;
        foreach (var p in MatchingOffered(name, "change the priority of"))
        {
            using (p)
            {
                try
                {
                    p.PriorityClass = low ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
                    changed++;
                }
                catch (Exception ex) { Log.Debug($"priority of {p.ProcessName} ({p.Id}): {ex.Message}"); }
            }
        }
        if (changed > 0) Log.Info($"{name}: priority {(low ? "lowered" : "restored")} on {changed} process(es)");
        return changed;
    }

    private static int PostCloseToWindows(HashSet<int> pids)
    {
        int posted = 0;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (GetWindow(hwnd, GwOwner) != IntPtr.Zero) return true;
            GetWindowThreadProcessId(hwnd, out int wpid);
            if (!pids.Contains(wpid)) return true;
            if (PostMessageW(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero)) posted++;
            return true;
        }, IntPtr.Zero);
        return posted;
    }

    /// <summary>Protection is by name because pids are recycled; also refuses this agent.</summary>
    private static bool IsProtected(Process p)
    {
        if (p.Id == Environment.ProcessId) return true;
        if (p.Id <= 4) return true;
        if (Protected.Contains(p.ProcessName)) return true;
        // Anything else shipped by the antivirus vendors this machine uses.
        if (p.ProcessName.StartsWith("rs", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public RunningAppsState Snapshot()
    {
        var windowed = new Dictionary<int, string>();

        // One title per process: the first real top-level window wins. A process with six
        // windows should be one row you can close, not six rows that each half-close it.
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (GetWindow(hwnd, GwOwner) != IntPtr.Zero) return true;
            if ((GetWindowLongW(hwnd, GwlExStyle) & WsExToolWindow) != 0) return true;
            if (GetWindowTextLengthW(hwnd) == 0) return true;

            // A suspended UWP app keeps a visible window that is not on screen. Without this
            // the list fills up with Store apps the user closed long ago.
            if (DwmGetWindowAttribute(hwnd, DwmaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            GetWindowThreadProcessId(hwnd, out int pid);
            if (pid <= 0 || windowed.ContainsKey(pid)) return true;

            var sb = new StringBuilder(512);
            GetWindowTextW(hwnd, sb, sb.Capacity);
            windowed[pid] = sb.ToString();
            return true;
        }, IntPtr.Zero);

        var items = new List<RunningAppInfo>();
        var seen = new HashSet<int>();

        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (Exception ex) { Log.Warn($"process enumeration failed: {ex.Message}"); return new RunningAppsState(); }

        try
        {
            foreach (var p in all)
            {
                try
                {
                    if (IsProtected(p)) continue;

                    long mb = p.WorkingSet64 / (1024 * 1024);
                    bool hasWindow = windowed.TryGetValue(p.Id, out var title);

                    if (!hasWindow && mb < BackgroundFloorMb) continue;

                    items.Add(new RunningAppInfo
                    {
                        Pid = p.Id,
                        Name = p.ProcessName,
                        Title = hasWindow && !string.IsNullOrWhiteSpace(title) ? Truncate(title!, 70) : null,
                        MemoryMb = mb,
                        Windowed = hasWindow,
                    });
                    seen.Add(p.Id);
                }
                catch { /* exited, or a process this agent may not inspect */ }
            }
        }
        finally
        {
            foreach (var p in all) { try { p.Dispose(); } catch { } }
        }

        // Windowed first and alphabetical, because that list is scanned by name. Background
        // by memory descending, because that one is scanned for the biggest number.
        var windows = items.Where(i => i.Windowed)
                           .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                           .ToList();
        var background = items.Where(i => !i.Windowed)
                              .OrderByDescending(i => i.MemoryMb)
                              .Take(MaxBackground)
                              .ToList();

        var result = windows.Concat(background).ToList();

        // Only what was actually offered may later be closed. This is the same rule the
        // media source catalogue uses: the tablet names an id, and an id it was never given
        // is not a request, it is a bug or an attack.
        lock (_gate)
        {
            _offered.Clear();
            foreach (var i in result) _offered.Add(i.Pid);
        }

        return new RunningAppsState { Items = result };
    }

    private bool WasOffered(int pid)
    {
        lock (_gate) return _offered.Contains(pid);
    }

    /// <summary>
    /// Asks the application to close, exactly as its own X button would: it may prompt to
    /// save, and it may refuse. This is the default because the alternative silently
    /// discards unsaved work.
    /// </summary>
    public bool Close(int pid)
    {
        if (!Allowed(pid, "close", out var p) || p is null) return false;

        using (p)
        {
            int posted = 0;
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                if (GetWindow(hwnd, GwOwner) != IntPtr.Zero) return true;
                GetWindowThreadProcessId(hwnd, out int wpid);
                if (wpid != pid) return true;
                if (PostMessageW(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero)) posted++;
                return true;
            }, IntPtr.Zero);

            if (posted == 0)
            {
                Log.Info($"close {p.ProcessName} ({pid}): no window to ask");
                return false;
            }

            Log.Info($"close requested: {p.ProcessName} ({pid}), {posted} window(s)");
            return true;
        }
    }

    /// <summary>Terminates immediately. Nothing is saved; this is for an app that is hung.</summary>
    public bool Kill(int pid) => Kill(pid, out _);

    /// <summary>
    /// As above, and says in words the user can act on why it did not work. "Could not be ended" was
    /// all the tablet ever learned, which made a protected process, a refused termination and a
    /// process that had already closed look identical.
    /// </summary>
    public bool Kill(int pid, out string? reason)
    {
        reason = null;
        if (!Allowed(pid, "kill", out var p, out reason) || p is null)
            return reason == "already closed";   // gone before we got there is the outcome that was wanted

        using (p)
        {
            string name = p.ProcessName;
            try
            {
                p.Kill(entireProcessTree: true);
                Log.Warn($"killed {name} ({pid})");
                return true;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
            {
                // Access denied: the process runs with more rights than this agent, or a protection
                // driver (anti-cheat, antivirus) refuses termination even to an administrator.
                reason = Elevation.IsElevated
                    ? "Windows refused: a protection driver is guarding it"
                    : "Windows refused: it runs with higher rights. Run the agent as administrator";
                Log.Warn($"kill {name} ({pid}) denied: {ex.Message}");
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;   // exited between the check and the call
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                Log.Warn($"kill {name} ({pid}) failed: {ex.Message}");
                return false;
            }
        }
    }

    private bool Allowed(int pid, string what, out Process? proc, out string? reason)
    {
        proc = null;
        reason = null;

        if (!WasOffered(pid))
        {
            Log.Warn($"refused to {what} pid {pid}: not in the published list");
            reason = "that app is no longer in the list";
            return false;
        }

        try { proc = Process.GetProcessById(pid); }
        catch { Log.Info($"{what} pid {pid}: already gone"); reason = "already closed"; return false; }

        // Re-checked against the live process rather than trusting the snapshot: pids are
        // recycled, so the pid that was safe when it was published may be something else
        // entirely by the time the tap arrives.
        if (IsProtected(proc))
        {
            Log.Warn($"refused to {what} {proc.ProcessName} ({pid}): protected");
            reason = "Windows needs this process";
            proc.Dispose();
            proc = null;
            return false;
        }

        return true;
    }

    private bool Allowed(int pid, string what, out Process? proc) => Allowed(pid, what, out proc, out _);

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";
}
