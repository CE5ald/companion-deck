using System.Text;

namespace CompanionDeck.Host;

public enum LogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3, None = 4 }

/// <summary>
/// Deliberately tiny logger. Writes are lock-guarded and the file sink is opt-in, because
/// an agent that runs all day during gaming should not be doing disk I/O for chatter.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _file;

    public static LogLevel Level { get; set; } = LogLevel.Info;
    public static bool ToConsole { get; set; } = true;

    public static void OpenFile(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Keep one previous run around; never let the log grow without bound.
            if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                File.Move(path, path + ".old", overwrite: true);

            lock (Gate)
            {
                _file?.Dispose();
                // ReadWrite, not Read: --selftest and --probe-* are routinely run while the
                // agent is already running, and denying the second writer made every one of
                // them open with a file-locking error that has nothing to do with the
                // question being asked. Interleaved lines are a far smaller problem.
                _file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write,
                                                        FileShare.ReadWrite),
                                         Encoding.UTF8) { AutoFlush = true };
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"log file unavailable: {ex.Message}");
        }
    }

    public static void Debug(string m) => Write(LogLevel.Debug, "DBG", m);
    public static void Info(string m) => Write(LogLevel.Info, "INF", m);
    public static void Warn(string m) => Write(LogLevel.Warn, "WRN", m);
    public static void Error(string m) => Write(LogLevel.Error, "ERR", m);

    public static void Error(string m, Exception ex) =>
        Write(LogLevel.Error, "ERR", $"{m}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(LogLevel level, string tag, string message)
    {
        if (level < Level) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff} {tag} {message}";
        lock (Gate)
        {
            if (ToConsole) Console.WriteLine(line);
            _file?.WriteLine(line);
        }
    }

    public static void Close()
    {
        lock (Gate) { _file?.Dispose(); _file = null; }
    }
}
