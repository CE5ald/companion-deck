using CompanionDeck.Host.Protocol;

namespace CompanionDeck.Host.Clock;

/// <summary>
/// Upcoming events from calendar files dropped into one folder. There is no sign-in and no
/// network access: exporting a calendar to an .ics file and putting it in the folder is the whole
/// setup, the same way the soundboard takes its clips.
/// </summary>
public sealed class CalendarService
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const int MaxFiles = 20;

    private readonly object _gate = new();
    private List<IcsParser.Event> _events = new();
    private string _signature = "";
    private int _files;

    public string Folder { get; }
    public int Files { get { lock (_gate) return _files; } }

    public CalendarService(string? folder = null)
    {
        Folder = folder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CompanionDeck", "calendar");
    }

    /// <summary>Re-reads the folder only if a file was added, removed or changed.</summary>
    public void Refresh()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var files = new DirectoryInfo(Folder).GetFiles("*.ics").Take(MaxFiles).ToArray();
            string sig = string.Join("|", files.Select(f => $"{f.Name}:{f.Length}:{f.LastWriteTimeUtc.Ticks}"));

            lock (_gate)
            {
                if (sig == _signature) return;
                _signature = sig;
                _files = files.Length;
            }

            var all = new List<IcsParser.Event>();
            foreach (var f in files)
            {
                if (f.Length > MaxFileBytes) { Log.Warn($"calendar '{f.Name}' is over 5 MB; skipped"); continue; }
                try { all.AddRange(IcsParser.Parse(File.ReadAllText(f.FullName))); }
                catch (Exception ex) { Log.Warn($"calendar '{f.Name}' could not be read: {ex.Message}"); }
            }
            lock (_gate) _events = all;
            Log.Info($"calendar: {all.Count} events from {files.Length} file(s)");
        }
        catch (Exception ex) { Log.Debug($"calendar refresh: {ex.Message}"); }
    }

    public List<CalendarEvent> Upcoming(DateTimeOffset now, int max = 8, int days = 14)
    {
        List<IcsParser.Event> snapshot;
        lock (_gate) snapshot = _events;

        // Today's all-day events are included from midnight, and a running event until it ends.
        var from = new DateTimeOffset(now.Date, now.Offset);
        return IcsParser.Expand(snapshot, from, now.AddDays(days))
            .Where(o => o.End > now)
            .Take(max)
            .Select(o => new CalendarEvent
            {
                Title = o.Title, Place = o.Place, AllDay = o.AllDay,
                StartMs = o.Start.ToUnixTimeMilliseconds(), EndMs = o.End.ToUnixTimeMilliseconds(),
            })
            .ToList();
    }
}
