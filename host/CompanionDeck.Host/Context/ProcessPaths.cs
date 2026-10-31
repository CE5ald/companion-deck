using System.Runtime.InteropServices;
using System.Text;

namespace CompanionDeck.Host.Context;

/// <summary>
/// Where a running program's executable lives. This is how a game is recognised without knowing its
/// name: Steam records no executable at all, and Epic's "launch executable" is often a launcher stub
/// (often a Launcher.exe, while the game itself runs as a different program). The folder it was installed
/// into is the one fact every store agrees on.
/// </summary>
public static class ProcessPaths
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder name, ref int size);

    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>The full path of the process's program, or null if it has gone or may not be inspected.</summary>
    public static string? PathOf(int pid)
    {
        if (pid <= 4) return null;
        IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>True when the path is inside the folder, compared whole-segment so "Game2" is not inside "Game".</summary>
    public static bool IsInside(string path, string folder)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder)) return false;
        folder = folder.Replace('/', '\\').TrimEnd('\\') + "\\";
        return path.Replace('/', '\\').StartsWith(folder, StringComparison.OrdinalIgnoreCase);
    }
}
