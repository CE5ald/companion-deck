using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CompanionDeck.Host.Transport;

public sealed record AdbDevice(string Serial, string State)
{
    public bool IsReady => State == "device";
}

/// <summary>
/// Minimal client for the adb server's wire protocol (127.0.0.1:5037). We speak the protocol
/// directly instead of shelling out to adb.exe because device presence is tracked with a single
/// long-lived socket ("host:track-devices") that the server pushes to.
/// </summary>
public sealed class AdbClient
{
    private const string Host = "127.0.0.1";
    private const int Port = 5037;

    private readonly string _adbPath;

    public AdbClient(string? adbPath = null)
    {
        _adbPath = adbPath ?? LocateAdb() ?? "adb";
    }

    public static string? LocateAdb()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Android", "Sdk", "platform-tools", "adb.exe"),
        };

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim(), "adb.exe")); } catch { /* malformed PATH entry */ }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Ensures the adb server is up. Only ever spawns a process on a cold start.</summary>
    public async Task EnsureServerAsync(CancellationToken ct)
    {
        if (await CanConnectAsync(ct).ConfigureAwait(false)) return;

        try
        {
            var psi = new ProcessStartInfo(_adbPath, "start-server")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is not null) await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"adb start-server failed: {ex.Message}");
        }
    }

    private static async Task<bool> CanConnectAsync(CancellationToken ct)
    {
        try
        {
            using var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, Port, ct).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    private static async Task<NetworkStream> OpenAsync(CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, Port, ct).ConfigureAwait(false);
        return client.GetStream();
    }

    private static async Task SendAsync(Stream s, string request, CancellationToken ct)
    {
        var body = Encoding.ASCII.GetBytes(request);
        var msg = new byte[4 + body.Length];
        Encoding.ASCII.GetBytes(body.Length.ToString("x4")).CopyTo(msg, 0);
        body.CopyTo(msg, 4);
        await s.WriteAsync(msg, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task ExpectOkayAsync(Stream s, CancellationToken ct)
    {
        var status = await ReadExactAsync(s, 4, ct).ConfigureAwait(false);
        var text = Encoding.ASCII.GetString(status);
        if (text == "OKAY") return;
        if (text == "FAIL")
        {
            var reason = await ReadLengthPrefixedAsync(s, ct).ConfigureAwait(false);
            throw new IOException($"adb refused request: {reason}");
        }
        throw new IOException($"adb returned unexpected status '{text}'");
    }

    private static async Task<byte[]> ReadExactAsync(Stream s, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("adb closed the connection.");
            read += n;
        }
        return buf;
    }

    private static async Task<string> ReadLengthPrefixedAsync(Stream s, CancellationToken ct)
    {
        var lenBytes = await ReadExactAsync(s, 4, ct).ConfigureAwait(false);
        int len = Convert.ToInt32(Encoding.ASCII.GetString(lenBytes), 16);
        if (len == 0) return string.Empty;
        var payload = await ReadExactAsync(s, len, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(payload);
    }

    /// <summary>
    /// Long-lived subscription to the device list. The callback fires once immediately with
    /// the current set and then on every change. Returns only when cancelled or on error.
    /// </summary>
    public async Task TrackDevicesAsync(Func<IReadOnlyList<AdbDevice>, Task> onChange, CancellationToken ct)
    {
        using var stream = await OpenAsync(ct).ConfigureAwait(false);
        await SendAsync(stream, "host:track-devices", ct).ConfigureAwait(false);
        await ExpectOkayAsync(stream, ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            var payload = await ReadLengthPrefixedAsync(stream, ct).ConfigureAwait(false);
            var devices = ParseDeviceList(payload);
            await onChange(devices).ConfigureAwait(false);
        }
    }

    private static List<AdbDevice> ParseDeviceList(string payload)
    {
        var list = new List<AdbDevice>();
        foreach (var line in payload.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) list.Add(new AdbDevice(parts[0].Trim(), parts[1].Trim()));
        }
        return list;
    }

    /// <summary>
    /// Forwards a local TCP port to an abstract unix socket inside the tablet app.
    /// Using localabstract (rather than a tcp port on the device) means the tablet never
    /// opens a listening network socket - the only way in is through the cable.
    /// </summary>
    public async Task ForwardAsync(string serial, int localPort, string remoteAbstractName, CancellationToken ct)
    {
        using var stream = await OpenAsync(ct).ConfigureAwait(false);
        await SendAsync(stream,
            $"host-serial:{serial}:forward:norebind:tcp:{localPort};localabstract:{remoteAbstractName}",
            ct).ConfigureAwait(false);
        await ExpectOkayAsync(stream, ct).ConfigureAwait(false);
        // adb acknowledges forward requests twice; the second OKAY may be absent on
        // some server builds, so a clean EOF here is not an error.
        try { await ExpectOkayAsync(stream, ct).ConfigureAwait(false); }
        catch (EndOfStreamException) { }
    }

    public async Task RemoveForwardAsync(string serial, int localPort, CancellationToken ct)
    {
        try
        {
            using var stream = await OpenAsync(ct).ConfigureAwait(false);
            await SendAsync(stream, $"host-serial:{serial}:killforward:tcp:{localPort}", ct).ConfigureAwait(false);
            await ExpectOkayAsync(stream, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { Log.Debug($"killforward: {ex.Message}"); }
    }

    /// <summary>
    /// Removes forwards left behind by earlier runs. The adb server owns forwards, not us, so any
    /// that were not torn down cleanly - the host being killed, a crash - survive in the server
    /// and accumulate on every restart.
    /// </summary>
    public async Task<int> CleanStaleForwardsAsync(string remoteAbstractName, CancellationToken ct)
    {
        int removed = 0;
        try
        {
            string listing;
            using (var stream = await OpenAsync(ct).ConfigureAwait(false))
            {
                await SendAsync(stream, "host:list-forward", ct).ConfigureAwait(false);
                await ExpectOkayAsync(stream, ct).ConfigureAwait(false);
                listing = await ReadLengthPrefixedAsync(stream, ct).ConfigureAwait(false);
            }

            var target = $"localabstract:{remoteAbstractName}";

            foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // Each line is "<serial> <local> <remote>".
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;
                if (!parts[2].Trim().Equals(target, StringComparison.OrdinalIgnoreCase)) continue;

                var serial = parts[0].Trim();
                var local = parts[1].Trim();

                try
                {
                    using var kill = await OpenAsync(ct).ConfigureAwait(false);
                    await SendAsync(kill, $"host-serial:{serial}:killforward:{local}", ct).ConfigureAwait(false);
                    await ExpectOkayAsync(kill, ct).ConfigureAwait(false);
                    removed++;
                }
                catch (Exception ex) { Log.Debug($"killforward {local}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Debug($"list-forward: {ex.Message}"); }

        if (removed > 0) Log.Info($"cleaned {removed} stale adb forward(s)");
        return removed;
    }

    /// <summary>Runs a shell command on the device and returns its combined output.</summary>
    public async Task<string> ShellAsync(string serial, string command, CancellationToken ct)
    {
        using var stream = await OpenAsync(ct).ConfigureAwait(false);
        await SendAsync(stream, $"host:transport:{serial}", ct).ConfigureAwait(false);
        await ExpectOkayAsync(stream, ct).ConfigureAwait(false);
        await SendAsync(stream, $"shell:{command}", ct).ConfigureAwait(false);
        await ExpectOkayAsync(stream, ct).ConfigureAwait(false);

        using var ms = new MemoryStream();
        var buf = new byte[4096];
        while (true)
        {
            int n;
            try { n = await stream.ReadAsync(buf, ct).ConfigureAwait(false); }
            catch (IOException) { break; }
            if (n <= 0) break;
            ms.Write(buf, 0, n);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Picks a free loopback port for the forward.</summary>
    public static int FindFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
