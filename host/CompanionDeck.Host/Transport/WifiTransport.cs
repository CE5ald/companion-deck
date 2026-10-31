using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CompanionDeck.Host.Transport;

/// <summary>
/// The fallback transport, and the only one that needs nothing enabled on the tablet.
///
/// The PC listens on an ephemeral TCP port and announces it in a small UDP broadcast; the
/// tablet hears the broadcast and dials back. Doing it that way round means the tablet
/// never keeps a listening port open, and it works over any network the two share -
/// including the one Android's USB tethering creates over the cable, which is the route
/// for someone who does not want to turn on developer options.
///
/// Beacons stop the moment a USB link exists, so the two transports never race for the
/// same tablet.
/// </summary>
public sealed class WifiTransport
{
    private const int DiscoveryPort = 45888;
    private static readonly TimeSpan BeaconInterval = TimeSpan.FromSeconds(2);

    private readonly Func<bool> _usbActive;

    public Func<DeckLink, Task>? OnLink { get; set; }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<DeckLink, byte> _links = new();

    /// <summary>
    /// How many strangers may be mid-handshake at once. Each is dropped after a few seconds, but an
    /// open listener should not let one machine hold hundreds of sockets while it waits.
    /// </summary>
    private const int MaxUnproven = 8;

    /// <summary>
    /// True while a tablet that has proved it holds the key is connected over this transport.
    /// A connection that has not is not a tablet yet, and must not silence the beacon.
    /// </summary>
    public bool Connected => _links.Keys.Any(l => l.Authenticated);

    public WifiTransport(Func<bool> usbActive) => _usbActive = usbActive;

    public async Task RunAsync(CancellationToken ct)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Log.Info($"network transport listening on port {port} (used when no USB cable is present)");

            var beacon = Task.Run(() => BeaconLoopAsync(port, ct), ct);
            var accept = Task.Run(() => AcceptLoopAsync(listener, ct), ct);
            await Task.WhenAll(beacon, accept).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"network transport stopped: {ex.Message}");
        }
        finally
        {
            try { listener?.Stop(); } catch { }
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Debug($"accept failed: {ex.Message}"); continue; }

            // A cable that arrives mid-session wins, but a tablet already talking over the
            // network is left alone until it goes away by itself.
            if (_usbActive())
            {
                Log.Debug("refusing network connection: a USB link is already up");
                try { tcp.Close(); } catch { }
                continue;
            }

            if (_links.Keys.Count(l => !l.Authenticated) >= MaxUnproven)
            {
                Log.Debug("refusing network connection: too many peers mid-handshake");
                try { tcp.Close(); } catch { }
                continue;
            }

            // Each connection gets its own task. They used to be served one after another, which
            // would let a peer that connects and says nothing keep the real tablet waiting.
            _ = Task.Run(() => ServeOneAsync(tcp));
        }
    }

    private async Task ServeOneAsync(TcpClient tcp)
    {
        tcp.NoDelay = true;
        var peer = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
        Log.Info($"network link up from {peer}");

        DeckLink? link = null;
        try
        {
            link = new DeckLink(tcp.GetStream(), "wifi", tcp);
            _links[link] = 0;
            link.Start();
            if (OnLink is not null) await OnLink(link).ConfigureAwait(false);
        }
        catch (Exception ex) { Log.Debug($"network session: {ex.Message}"); }
        finally
        {
            if (link is not null) _links.TryRemove(link, out _);
            try { tcp.Close(); } catch { }
            Log.Info("network link down");
        }
    }

    /// <summary>
    /// Announces the listening port on every network the PC is actually on.
    ///
    /// Each interface is addressed by its own directed broadcast rather than relying on
    /// 255.255.255.255 alone, because Windows sends a limited broadcast out of exactly one
    /// interface - whichever the routing table prefers - and the one that matters here is
    /// usually the one it would not pick: the tether network that appeared when the cable
    /// went in.
    /// </summary>
    private async Task BeaconLoopAsync(int port, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            app = "companiondeck",
            port,
            host = Environment.MachineName,
        }));

        using var udp = new UdpClient { EnableBroadcast = true };
        var endpoints = Array.Empty<IPEndPoint>();
        var refreshedAt = DateTime.MinValue;

        while (!ct.IsCancellationRequested)
        {
            // Silent while the cable is doing the job, which is most of the time.
            if (!_usbActive() && !Connected)
            {
                if ((DateTime.UtcNow - refreshedAt).TotalSeconds > 15)
                {
                    refreshedAt = DateTime.UtcNow;
                    endpoints = BroadcastTargets();
                }

                foreach (var ep in endpoints)
                {
                    try { await udp.SendAsync(payload, payload.Length, ep).ConfigureAwait(false); }
                    catch (Exception ex) { Log.Debug($"beacon to {ep}: {ex.Message}"); }
                }
            }

            try { await Task.Delay(BeaconInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static IPEndPoint[] BroadcastTargets()
    {
        var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, DiscoveryPort) };

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var info in nic.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (info.IPv4Mask is null) continue;

                    var addr = info.Address.GetAddressBytes();
                    var mask = info.IPv4Mask.GetAddressBytes();
                    if (mask.Length != 4) continue;

                    var bcast = new byte[4];
                    for (int i = 0; i < 4; i++) bcast[i] = (byte)(addr[i] | (byte)~mask[i]);
                    targets.Add(new IPEndPoint(new IPAddress(bcast), DiscoveryPort));
                }
            }
        }
        catch (Exception ex) { Log.Debug($"enumerating broadcast targets: {ex.Message}"); }

        return targets.DistinctBy(e => e.ToString()).ToArray();
    }
}
