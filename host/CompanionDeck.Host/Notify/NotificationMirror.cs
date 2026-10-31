using System.Text;
using System.Xml.Linq;
using CompanionDeck.Host.Protocol;

namespace CompanionDeck.Host.Notify;

/// <summary>
/// Windows toasts, read from the notification database Windows keeps for the Action Center.
///
/// The documented way to listen for notifications is only open to apps with a package identity,
/// which a plain desktop agent does not have. The database is readable by the signed-in user, so
/// this reads it, read-only, through the SQLite library that ships with Windows. The schema is
/// Microsoft's to change: if a future Windows build moves it, <see cref="Problem"/> says so and the
/// feature switches itself off rather than guessing.
///
/// It can only read. Dismissing a notification on the tablet hides it there; Windows keeps its own.
/// </summary>
public sealed class NotificationMirror
{
    public const int MaxItems = 20;

    private readonly string _path;

    public string? Problem { get; private set; }
    public bool Available => Problem is null;

    public NotificationMirror(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Notifications", "wpndatabase.db");
    }

    /// <summary>Opens the database once to see whether this Windows build looks the way we expect.</summary>
    public void Probe()
    {
        Problem = null;
        if (!File.Exists(_path)) { Problem = "this Windows keeps no notification database for this user"; return; }

        using var db = MiniSqlite.OpenReadOnly(_path, out var err);
        if (db is null) { Problem = err; return; }

        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        db.Query("PRAGMA table_info(Notification)", r => cols.Add(r.Text(1) ?? ""));
        var handlerCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        db.Query("PRAGMA table_info(NotificationHandler)", r => handlerCols.Add(r.Text(1) ?? ""));

        foreach (var need in new[] { "Id", "HandlerId", "Type", "Payload", "ArrivalTime", "ExpiryTime" })
            if (!cols.Contains(need)) { Problem = $"the notification database has changed ('{need}' is gone)"; return; }
        foreach (var need in new[] { "RecordId", "PrimaryId" })
            if (!handlerCols.Contains(need)) { Problem = $"the notification database has changed ('{need}' is gone)"; return; }
    }

    /// <summary>The notifications currently in the Action Center, newest first, minus any hidden by the tablet.</summary>
    public List<NotificationItem> Read(ISet<long> hidden)
    {
        var items = new List<NotificationItem>();
        if (!Available) return items;

        try
        {
            using var db = MiniSqlite.OpenReadOnly(_path, out var err);
            if (db is null) { Log.Debug($"notifications: {err}"); return items; }

            long now = DateTime.UtcNow.ToFileTimeUtc();
            db.Query(
                "SELECT n.Id, n.ArrivalTime, n.ExpiryTime, n.Payload, h.PrimaryId " +
                "FROM Notification n LEFT JOIN NotificationHandler h ON h.RecordId = n.HandlerId " +
                "WHERE n.Type = 'toast' ORDER BY n.ArrivalTime DESC LIMIT 60", r =>
                {
                    long id = r.Int(0), arrived = r.Int(1), expires = r.Int(2);
                    if (hidden.Contains(id)) return;
                    if (expires > 0 && expires < now) return;
                    if (items.Count >= MaxItems) return;

                    var (title, body) = ParseToast(r.Blob(3));
                    if (title.Length == 0 && body.Length == 0) return;

                    items.Add(new NotificationItem
                    {
                        Id = id,
                        App = FriendlyApp(r.Text(4)),
                        Title = Truncate(title, 90),
                        Body = Truncate(body, 200),
                        AtMs = arrived > 0 ? new DateTimeOffset(DateTime.FromFileTimeUtc(arrived), TimeSpan.Zero).ToUnixTimeMilliseconds() : 0,
                    });
                });

            if (db.Error is not null) Log.Debug($"notifications: {db.Error}");
        }
        catch (Exception ex) { Log.Debug($"notifications: {ex.Message}"); }
        return items;
    }

    /// <summary>First text node is the title, the rest the body, as Windows lays a generic toast out.</summary>
    public static (string title, string body) ParseToast(byte[]? payload)
    {
        if (payload is null || payload.Length == 0) return ("", "");
        try
        {
            // Stored as UTF-8 on current builds; tolerate UTF-16 in case it is not.
            string xml = payload.Length > 1 && payload[1] == 0 ? Encoding.Unicode.GetString(payload) : Encoding.UTF8.GetString(payload);
            xml = xml.Trim('﻿', '\0', ' ', '\r', '\n');

            var doc = XDocument.Parse(xml);
            var texts = doc.Descendants()
                .Where(e => e.Name.LocalName == "text")
                .Select(e => e.Value.Trim())
                .Where(t => t.Length > 0)
                .ToList();
            if (texts.Count == 0) return ("", "");
            return (texts[0], string.Join(" - ", texts.Skip(1)));
        }
        catch { return ("", ""); }
    }

    /// <summary>"5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App" becomes "WhatsAppDesktop".</summary>
    public static string FriendlyApp(string? primaryId)
    {
        if (string.IsNullOrWhiteSpace(primaryId)) return "";
        string id = primaryId;
        int bang = id.IndexOf('!');
        if (bang > 0) id = id[..bang];
        int back = id.LastIndexOf('\\');
        if (back >= 0 && back < id.Length - 1) id = id[(back + 1)..];
        int us = id.IndexOf('_');
        if (us > 0) id = id[..us];
        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) id = id[..^4];
        int dot = id.LastIndexOf('.');
        if (dot >= 0 && dot < id.Length - 1) id = id[(dot + 1)..];
        return id.Length > 40 ? id[..40] : id;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "...";
}
