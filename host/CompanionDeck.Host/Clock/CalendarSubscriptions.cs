using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CompanionDeck.Host.Protocol;

namespace CompanionDeck.Host.Clock;

/// <summary>
/// Calendars subscribed by link, so nobody has to export a file and carry it to a folder. Google, Outlook,
/// iCloud, Fastmail and Nextcloud all offer a private .ics address for a calendar; pasting it once is the
/// whole setup, and the agent fetches it again every half hour.
///
/// The address is a secret (whoever holds it can read the calendar), so it is kept only in this PC's
/// settings file and is never sent to the tablet - the tablet is shown the site's name and nothing more.
/// Fetched calendars are saved beside any hand-placed files, so the existing reader needs no changes and
/// the last copy survives being offline.
/// </summary>
public sealed class CalendarSubscriptions
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private const int MaxSubscriptions = 8;

    private sealed class Sub
    {
        public string Id { get; set; } = "";
        public string Url { get; set; } = "";
        public string Name { get; set; } = "";
        public long LastOkMs { get; set; }
        public string? Error { get; set; }
    }

    private readonly string _settingsPath;
    private readonly string _folder;
    private readonly object _gate = new();
    private List<Sub> _subs = new();
    private readonly HttpClient _http;

    public CalendarSubscriptions(string calendarFolder, string? settingsPath = null)
    {
        _folder = calendarFolder;
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CompanionDeck", "calendars.json");
        // Redirects are followed by hand, so every hop is checked like the first address.
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            // Calendar servers compress; without this a perfectly good calendar arrives as bytes that are not text.
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CompanionDeck-calendar");
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            _subs = JsonSerializer.Deserialize<List<Sub>>(File.ReadAllText(_settingsPath)) ?? new();
        }
        catch (Exception ex) { Log.Warn($"calendars.json unreadable: {ex.Message}"); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var tmp = _settingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_subs));
            File.Move(tmp, _settingsPath, overwrite: true);
        }
        catch (Exception ex) { Log.Warn($"could not save calendars.json: {ex.Message}"); }
    }

    /// <summary>What the tablet may know: a name, the site, and whether the last fetch worked. Never the address.</summary>
    public List<CalendarSubInfo> Info()
    {
        lock (_gate)
            return _subs.Select(s => new CalendarSubInfo
            {
                Id = s.Id, Name = s.Name, Host = HostOf(s.Url), LastOkMs = s.LastOkMs, Error = s.Error,
            }).ToList();
    }

    private static string HostOf(string url)
    {
        try { return new Uri(url).Host; } catch { return ""; }
    }

    /// <summary>Accepts https:// and webcal:// (which is https in a different coat). Anything else is refused.</summary>
    public static bool TryNormalise(string? raw, out Uri uri, out string? error)
    {
        uri = null!;
        error = null;
        raw = raw?.Trim();
        if (string.IsNullOrEmpty(raw)) { error = "no link"; return false; }
        if (raw.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) raw = "https://" + raw[9..];
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
        { error = "that is not an https calendar link"; return false; }
        if (u.HostNameType == UriHostNameType.Unknown || u.Host.Length == 0) { error = "that link has no site"; return false; }
        uri = u;
        return true;
    }

    /// <summary>
    /// True for addresses that are this PC or its own network. A calendar link has no business there, and
    /// a link that did point there would turn this agent into something that fetches pages from inside
    /// the user's network on a stranger's say-so.
    /// </summary>
    public static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || b[0] == 0
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] is >= 64 and <= 127);
    }

    private static string IdOf(string url) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..8].ToLowerInvariant();

    private string FileFor(string id) => Path.Combine(_folder, $"subscribed-{id}.ics");

    /// <summary>Adds a calendar by link and fetches it at once, so a wrong link is reported now, not half an hour later.</summary>
    public async Task<(bool ok, string? error)> AddAsync(string? raw)
    {
        if (!TryNormalise(raw, out var uri, out var err)) return (false, err);
        string url = uri.ToString();
        string id = IdOf(url);

        lock (_gate)
        {
            if (_subs.Any(s => s.Id == id)) return (false, "that calendar is already added");
            if (_subs.Count >= MaxSubscriptions) return (false, $"at most {MaxSubscriptions} calendars");
        }

        var (body, fetchError) = await FetchAsync(uri).ConfigureAwait(false);
        if (body is null) return (false, fetchError);

        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(FileFor(id), body).ConfigureAwait(false);
        lock (_gate)
        {
            _subs.Add(new Sub
            {
                Id = id, Url = url, Name = NameOf(body) ?? uri.Host,
                LastOkMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            Save();
        }
        Log.Info($"calendar subscribed: {uri.Host}");
        return (true, null);
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            if (_subs.RemoveAll(s => s.Id == id) == 0) return false;
            Save();
        }
        try { File.Delete(FileFor(id)); } catch { /* it will simply not be read next time */ }
        return true;
    }

    /// <summary>Fetches every subscription again. A failure keeps the previous copy and is shown, not hidden.</summary>
    public async Task RefreshAllAsync()
    {
        List<Sub> list;
        lock (_gate) list = _subs.ToList();
        foreach (var s in list)
        {
            if (!TryNormalise(s.Url, out var uri, out _)) continue;
            var (body, error) = await FetchAsync(uri).ConfigureAwait(false);
            lock (_gate)
            {
                if (body is null) s.Error = error;
                else { s.Error = null; s.LastOkMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); s.Name = NameOf(body) ?? s.Name; }
            }
            if (body is not null)
            {
                try { await File.WriteAllTextAsync(FileFor(s.Id), body).ConfigureAwait(false); }
                catch (Exception ex) { Log.Warn($"calendar copy not saved: {ex.Message}"); }
            }
        }
        lock (_gate) Save();
    }

    private static string? NameOf(string ics)
    {
        foreach (var line in ics.Split('\n'))
            if (line.StartsWith("X-WR-CALNAME", StringComparison.OrdinalIgnoreCase))
            {
                int colon = line.IndexOf(':');
                var name = colon >= 0 ? line[(colon + 1)..].Trim() : "";
                if (name.Length > 0) return name.Length > 40 ? name[..40] : name;
            }
        return null;
    }

    private async Task<(string? body, string? error)> FetchAsync(Uri uri)
    {
        try
        {
            for (int hop = 0; hop < 4; hop++)
            {
                var addresses = await Dns.GetHostAddressesAsync(uri.Host).ConfigureAwait(false);
                if (addresses.Length == 0) return (null, "that site could not be found");
                if (addresses.Any(IsPrivate)) return (null, "that link points inside your own network");

                using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } next)
                {
                    uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
                    if (uri.Scheme != Uri.UriSchemeHttps) return (null, "that link redirects to a page that is not secure");
                    continue;
                }
                if (!resp.IsSuccessStatusCode) return (null, $"the site answered {(int)resp.StatusCode}");
                if (resp.Content.Headers.ContentLength > MaxBytes) return (null, "that calendar is over 5 MB");

                await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var ms = new MemoryStream();
                var buf = new byte[81920];
                int n;
                while ((n = await stream.ReadAsync(buf).ConfigureAwait(false)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > MaxBytes) return (null, "that calendar is over 5 MB");
                }
                var text = Encoding.UTF8.GetString(ms.ToArray()).TrimStart('﻿', ' ', '\r', '\n');
                if (!text.StartsWith("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
                    return (null, "that link is not a calendar (.ics) file (the site sent " + (resp.Content.Headers.ContentType?.MediaType ?? "something else") + ")");
                return (text, null);
            }
            return (null, "too many redirects");
        }
        catch (TaskCanceledException) { return (null, "the site did not answer in time"); }
        catch (HttpRequestException ex) { return (null, "could not reach the site: " + ex.Message); }
        catch (Exception ex) { return (null, ex.Message); }
    }

    // ------------------------------------------------------------------ clipboard

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr h);

    /// <summary>
    /// The text on the PC's clipboard, read only because the user asked for it on the tablet. The copy that
    /// was taken is a calendar link they had just copied; nothing else about the clipboard is kept or sent.
    /// </summary>
    public static string? ClipboardText()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    var h = GetClipboardData(13 /* CF_UNICODETEXT */);
                    if (h == IntPtr.Zero) return null;
                    var p = GlobalLock(h);
                    if (p == IntPtr.Zero) return null;
                    try { return Marshal.PtrToStringUni(p); }
                    finally { GlobalUnlock(h); }
                }
                finally { CloseClipboard(); }
            }
            Thread.Sleep(40);   // another program has it for a moment
        }
        return null;
    }
}
