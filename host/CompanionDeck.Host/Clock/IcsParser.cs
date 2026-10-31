using System.Globalization;
using System.Text;

namespace CompanionDeck.Host.Clock;

/// <summary>
/// Reads the part of iCalendar that real calendar exports use: events with a start and end,
/// all-day events, and the common recurrence rules. Anything it does not understand is left
/// out rather than guessed at, so an unfamiliar file shows fewer events, not wrong ones.
/// </summary>
public static class IcsParser
{
    public sealed class Event
    {
        public string Title = "";
        public string? Place;
        public bool AllDay;
        /// <summary>Wall-clock start in <see cref="Zone"/>.</summary>
        public DateTime Start;
        public TimeSpan Length;
        public TimeZoneInfo Zone = TimeZoneInfo.Local;
        public Rule? Repeat;
        public List<DateTime> Excluded = new();
    }

    public sealed class Rule
    {
        public string Freq = "";
        public int Interval = 1;
        public int? Count;
        public DateTime? Until;
        public List<DayOfWeek> Days = new();
    }

    public readonly record struct Occurrence(string Title, string? Place, bool AllDay, DateTimeOffset Start, DateTimeOffset End);

    // IANA names are what Google and Apple write; Windows only knows its own. With invariant
    // globalisation the runtime cannot convert between them, so the common ones are listed.
    private static readonly Dictionary<string, string> Iana = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UTC"] = "UTC", ["Etc/UTC"] = "UTC", ["GMT"] = "UTC",
        ["Europe/London"] = "GMT Standard Time", ["Europe/Dublin"] = "GMT Standard Time",
        ["Europe/Paris"] = "Romance Standard Time", ["Europe/Madrid"] = "Romance Standard Time",
        ["Europe/Berlin"] = "W. Europe Standard Time", ["Europe/Rome"] = "W. Europe Standard Time",
        ["Europe/Amsterdam"] = "W. Europe Standard Time", ["Europe/Stockholm"] = "W. Europe Standard Time",
        ["Europe/Athens"] = "GTB Standard Time", ["Europe/Helsinki"] = "FLE Standard Time",
        ["Europe/Istanbul"] = "Turkey Standard Time", ["Europe/Moscow"] = "Russian Standard Time",
        ["Africa/Cairo"] = "Egypt Standard Time", ["Africa/Johannesburg"] = "South Africa Standard Time",
        ["Asia/Riyadh"] = "Arab Standard Time", ["Asia/Dubai"] = "Arabian Standard Time",
        ["Asia/Kolkata"] = "India Standard Time", ["Asia/Karachi"] = "Pakistan Standard Time",
        ["Asia/Bangkok"] = "SE Asia Standard Time", ["Asia/Shanghai"] = "China Standard Time",
        ["Asia/Singapore"] = "Singapore Standard Time", ["Asia/Tokyo"] = "Tokyo Standard Time",
        ["Asia/Seoul"] = "Korea Standard Time", ["Australia/Sydney"] = "AUS Eastern Standard Time",
        ["Pacific/Auckland"] = "New Zealand Standard Time",
        ["America/New_York"] = "Eastern Standard Time", ["America/Chicago"] = "Central Standard Time",
        ["America/Denver"] = "Mountain Standard Time", ["America/Los_Angeles"] = "Pacific Standard Time",
        ["America/Toronto"] = "Eastern Standard Time", ["America/Sao_Paulo"] = "E. South America Standard Time",
    };

    public static List<Event> Parse(string text)
    {
        var events = new List<Event>();
        Dictionary<string, (Dictionary<string, string> par, string value)>? cur = null;

        foreach (var line in Unfold(text))
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                cur = new(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (cur is not null && Build(cur) is { } e) events.Add(e);
                cur = null;
                continue;
            }
            if (cur is null) continue;

            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var head = line[..colon].Split(';');
            var par = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in head.Skip(1))
            {
                int eq = p.IndexOf('=');
                if (eq > 0) par[p[..eq]] = p[(eq + 1)..].Trim('"');
            }

            // EXDATE can repeat, so its values are joined and split again when built.
            string name = head[0].ToUpperInvariant();
            string value = line[(colon + 1)..];
            if (name == "EXDATE" && cur.TryGetValue("EXDATE", out var prev))
                cur["EXDATE"] = (prev.par, prev.value + "," + value);
            else cur[name] = (par, value);
        }
        return events;
    }

    private static IEnumerable<string> Unfold(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t')) { sb.Append(line, 1, line.Length - 1); continue; }
            if (sb.Length > 0) yield return sb.ToString();
            sb.Clear();
            sb.Append(line);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static Event? Build(Dictionary<string, (Dictionary<string, string> par, string value)> p)
    {
        if (p.ContainsKey("RECURRENCE-ID")) return null;
        if (p.TryGetValue("STATUS", out var st) && st.value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase)) return null;
        if (!p.TryGetValue("DTSTART", out var ds)) return null;
        if (!ParseTime(ds.value, ds.par, out var start, out var zone, out bool allDay)) return null;

        var e = new Event
        {
            Title = p.TryGetValue("SUMMARY", out var s) ? Unescape(s.value) : "(no title)",
            Place = p.TryGetValue("LOCATION", out var l) && l.value.Length > 0 ? Unescape(l.value) : null,
            AllDay = allDay, Start = start, Zone = zone,
            Length = allDay ? TimeSpan.FromDays(1) : TimeSpan.Zero,
        };

        if (p.TryGetValue("DTEND", out var de) && ParseTime(de.value, de.par, out var end, out var endZone, out _))
        {
            // Both ends are read in their own zone and compared in absolute time.
            var a = new DateTimeOffset(start, zone.GetUtcOffset(start));
            var b = new DateTimeOffset(end, endZone.GetUtcOffset(end));
            if (b > a) e.Length = b - a;
        }

        if (p.TryGetValue("RRULE", out var rr)) e.Repeat = ParseRule(rr.value, zone);
        if (p.TryGetValue("EXDATE", out var ex))
            foreach (var part in ex.value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (ParseTime(part.Trim(), ex.par, out var x, out _, out _)) e.Excluded.Add(x);

        return e;
    }

    private static bool ParseTime(string v, Dictionary<string, string> par, out DateTime wall, out TimeZoneInfo zone, out bool allDay)
    {
        wall = default; zone = TimeZoneInfo.Local; allDay = false;
        v = v.Trim();

        if (par.TryGetValue("VALUE", out var kind) && kind.Equals("DATE", StringComparison.OrdinalIgnoreCase) || v.Length == 8)
        {
            allDay = true;
            return DateTime.TryParseExact(v, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out wall);
        }

        bool utc = v.EndsWith('Z');
        if (!DateTime.TryParseExact(v.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out wall))
            return false;

        if (utc) { zone = TimeZoneInfo.Utc; return true; }
        if (par.TryGetValue("TZID", out var tzid) && FindZone(tzid) is { } z) zone = z;
        return true;
    }

    private static TimeZoneInfo? FindZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        if (Iana.TryGetValue(id, out var win))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(win); } catch { }
        }
        return null;
    }

    private static Rule? ParseRule(string text, TimeZoneInfo zone)
    {
        var r = new Rule();
        foreach (var part in text.Split(';'))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string k = part[..eq].ToUpperInvariant(), val = part[(eq + 1)..];
            switch (k)
            {
                case "FREQ": r.Freq = val.ToUpperInvariant(); break;
                case "INTERVAL": if (int.TryParse(val, out int i) && i is >= 1 and <= 100) r.Interval = i; break;
                case "COUNT": if (int.TryParse(val, out int c) && c >= 1) r.Count = c; break;
                case "UNTIL":
                    if (ParseTime(val, new(), out var u, out var uz, out _))
                        r.Until = TimeZoneInfo.ConvertTime(new DateTimeOffset(u, uz.GetUtcOffset(u)), zone).DateTime;
                    break;
                case "BYDAY":
                    foreach (var d in val.Split(','))
                    {
                        // Ordinals ("2MO") are monthly and not supported; a bare weekday is.
                        var code = d.Length >= 2 ? d[^2..].ToUpperInvariant() : "";
                        if (d.Length == 2 && DayOf(code) is { } dw) r.Days.Add(dw);
                    }
                    break;
            }
        }
        return r.Freq is "DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY" ? r : null;
    }

    private static DayOfWeek? DayOf(string c) => c switch
    {
        "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday,
        "TH" => DayOfWeek.Thursday, "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday,
        "SU" => DayOfWeek.Sunday, _ => null,
    };

    private static string Unescape(string s) =>
        s.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");

    /// <summary>Occurrences that end after <paramref name="from"/> and start before <paramref name="to"/>.</summary>
    public static List<Occurrence> Expand(IEnumerable<Event> events, DateTimeOffset from, DateTimeOffset to, int cap = 500)
    {
        var result = new List<Occurrence>();
        foreach (var e in events)
        {
            foreach (var wall in Walls(e, to))
            {
                if (e.Excluded.Contains(wall)) continue;
                var start = ToOffset(wall, e);
                var end = start + e.Length;
                if (start >= to) break;
                if (end <= from && !(e.Length == TimeSpan.Zero && start >= from)) continue;
                result.Add(new Occurrence(e.Title, e.Place, e.AllDay, start, end));
                if (result.Count >= cap) return result;
            }
        }
        return result.OrderBy(o => o.Start).ToList();
    }

    private static DateTimeOffset ToOffset(DateTime wall, Event e)
    {
        // A wall time that does not exist (spring-forward gap) is moved to the next valid one.
        if (e.Zone.IsInvalidTime(wall)) wall = wall.AddHours(1);
        return new DateTimeOffset(wall, e.Zone.GetUtcOffset(wall));
    }

    private static IEnumerable<DateTime> Walls(Event e, DateTimeOffset horizon)
    {
        if (e.Repeat is not { } r) { yield return e.Start; yield break; }

        int emitted = 0;
        DateTime limit = TimeZoneInfo.ConvertTime(horizon, e.Zone).DateTime.AddDays(1);

        if (r.Freq == "WEEKLY" && r.Days.Count > 0)
        {
            // Monday-based weeks, stepping by the interval and visiting the listed days.
            var weekStart = e.Start.Date.AddDays(-(((int)e.Start.DayOfWeek + 6) % 7));
            for (int guard = 0; guard < 3000; guard++, weekStart = weekStart.AddDays(7 * r.Interval))
            {
                if (weekStart > limit) yield break;
                foreach (var day in r.Days.OrderBy(d => ((int)d + 6) % 7))
                {
                    var cand = weekStart.AddDays(((int)day + 6) % 7) + e.Start.TimeOfDay;
                    if (cand < e.Start) continue;
                    if (r.Until is { } u && cand > u) yield break;
                    if (r.Count is { } n && emitted >= n) yield break;
                    emitted++;
                    yield return cand;
                }
            }
            yield break;
        }

        var at = e.Start;
        for (int guard = 0; guard < 5000; guard++)
        {
            if (at > limit) yield break;
            if (r.Until is { } u && at > u) yield break;
            if (r.Count is { } n && emitted >= n) yield break;
            emitted++;
            yield return at;

            // Monthly and yearly steps are taken from the original start, so the 31st does not
            // slide to the 28th and stay there.
            at = r.Freq switch
            {
                "DAILY" => e.Start.AddDays((double)r.Interval * emitted),
                "WEEKLY" => e.Start.AddDays(7.0 * r.Interval * emitted),
                "MONTHLY" => e.Start.AddMonths(r.Interval * emitted),
                _ => e.Start.AddYears(r.Interval * emitted),
            };
        }
    }
}
