using System.Net;
using System.Net.Sockets;

namespace CompanionDeck.Host.Transport;

/// <summary>
/// The primary transport: the tablet over its USB cable, via the adb server. The whole "plug it
/// in and it works" promise lives here.
/// </summary>
public sealed class UsbTransport
{
    private const string Package = "com.companiondeck";
    private const string AbstractSocket = "companiondeck";

    private readonly AdbClient _adb;
    private readonly bool _autoLaunchUi;

    /// <summary>Invoked with each established link; returns when that link has ended.</summary>
    public Func<DeckLink, Task>? OnLink { get; set; }

    /// <summary>True while a tablet is physically attached and authorised.</summary>
    public volatile bool DeviceAttached;

    public UsbTransport(AdbClient adb, bool autoLaunchUi = true)
    {
        _adb = adb;
        _autoLaunchUi = autoLaunchUi;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _adb.EnsureServerAsync(ct).ConfigureAwait(false);
                await _adb.CleanStaleForwardsAsync(AbstractSocket, ct).ConfigureAwait(false);
                await WatchAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Debug($"usb watcher restarting: {ex.Message}");
            }

            DeviceAttached = false;
            // adb server may be restarting (driver reset, cable replug); don't spin.
            await Task.Delay(2000, ct).ConfigureAwait(false);
        }
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        string? current = null;
        CancellationTokenSource? sessionCts = null;
        Task? session = null;

        // The session must be wound down however this method ends. When the adb server dies,
        // TrackDevicesAsync throws rather than returning, and the cleanup that used to sit
        // after it was skipped: the old session kept reconnecting against the restarted
        // server while the retry loop started a second one for the same tablet, so two links
        // came up 24ms apart and the loser stalled for the full silence timeout.
        try
        {
        await _adb.TrackDevicesAsync(async devices =>
        {
            var ready = devices.FirstOrDefault(d => d.IsReady);
            DeviceAttached = ready is not null;

            if (ready is null)
            {
                // A device that is plugged in but not usable is the single most common
                // first-run problem, and adb's own word for it ("unauthorized") tells an
                // ordinary person nothing. Say what to do instead, once per state change.
                ExplainUnusable(devices);

                if (current is not null)
                {
                    Log.Info($"tablet {current} detached");
                    sessionCts?.Cancel();
                    current = null;
                }
                return;
            }
            _lastExplained = null;

            if (current == ready.Serial) return;

            sessionCts?.Cancel();
            current = ready.Serial;
            Log.Info($"tablet {current} attached");

            sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var serial = ready.Serial;
            var token = sessionCts.Token;
            session = Task.Run(() => SessionLoopAsync(serial, token), token);

            await Task.CompletedTask;
        }, ct).ConfigureAwait(false);
        }
        finally
        {
            sessionCts?.Cancel();
            if (session is not null) { try { await session.ConfigureAwait(false); } catch { } }
        }
    }

    private string? _lastExplained;

    /// <summary>
    /// Turns an unusable device into an instruction. Each distinct state is explained once,
    /// because this runs on every device-list change and a cable being wiggled should not
    /// fill the log with the same paragraph.
    /// </summary>
    private void ExplainUnusable(IReadOnlyList<AdbDevice> devices)
    {
        var blocked = devices.FirstOrDefault(d => !d.IsReady);
        var key = blocked is null ? "none" : blocked.State;
        if (key == _lastExplained) return;
        _lastExplained = key;

        if (blocked is null) return;

        Log.Warn(blocked.State switch
        {
            "unauthorized" =>
                $"tablet {blocked.Serial} is plugged in but has not trusted this computer. " +
                "Look at the tablet: tap Allow on the \"Allow USB debugging?\" prompt, and tick " +
                "\"Always allow from this computer\" so it is never asked again.",
            "offline" =>
                $"tablet {blocked.Serial} is not responding. Unplug the cable and plug it back in; " +
                "if that does not help, the cable may be charge-only.",
            _ =>
                $"tablet {blocked.Serial} is in state '{blocked.State}' and cannot be used yet.",
        });
    }

    /// <summary>Keeps a link to one device alive, reconnecting until it detaches.</summary>
    private async Task SessionLoopAsync(string serial, CancellationToken ct)
    {
        int backoffMs = 400;

        while (!ct.IsCancellationRequested)
        {
            int port = 0;
            try
            {
                await EnsureAppRunningAsync(serial, ct).ConfigureAwait(false);

                port = AdbClient.FindFreePort();
                await _adb.ForwardAsync(serial, port, AbstractSocket, ct).ConfigureAwait(false);

                var tcp = new TcpClient { NoDelay = true };
                await tcp.ConnectAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false);

                Log.Info($"USB link up (127.0.0.1:{port} -> {serial})");
                backoffMs = 400;

                var link = new DeckLink(tcp.GetStream(), "usb", tcp);
                link.Start();

                // Cancelling the session has to end the link too. The server serves a link
                // under its own token, not this one, so without this a cancelled session
                // would sit in ServeAsync until the peer happened to go quiet.
                using var endWithSession = ct.Register(link.Abort);

                if (OnLink is not null) await OnLink(link).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Debug($"usb session: {ex.Message}");
            }
            finally
            {
                if (port != 0) await _adb.RemoveForwardAsync(serial, port, CancellationToken.None).ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested) break;
            await Task.Delay(backoffMs, ct).ConfigureAwait(false);
            backoffMs = Math.Min(backoffMs * 2, 5000);
        }
    }

    /// <summary>
    /// Starts the tablet's service (and optionally its UI) so the cable alone is enough.
    /// Both commands are harmless if it is already running.
    /// </summary>
    private async Task EnsureAppRunningAsync(string serial, CancellationToken ct)
    {
        try
        {
            await _adb.ShellAsync(serial, $"am startservice -n {Package}/.DeckService", ct).ConfigureAwait(false);
            if (_autoLaunchUi)
                await _adb.ShellAsync(serial, $"am start -n {Package}/.MainActivity", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug($"could not start tablet app: {ex.Message}");
        }
    }
}
