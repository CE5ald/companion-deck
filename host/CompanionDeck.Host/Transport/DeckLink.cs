using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CompanionDeck.Host.Protocol;

namespace CompanionDeck.Host.Transport;

/// <summary>
/// One live connection to the tablet, independent of how it was established. Outbound frames go
/// through a channel drained by a single writer task.
/// </summary>
public sealed class DeckLink : IAsyncDisposable
{
    private sealed record Outbound(FrameKind Kind, byte[] Payload);

    private readonly Stream _stream;
    private readonly IDisposable? _owner;
    private readonly Channel<Outbound> _out =
        Channel.CreateBounded<Outbound>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    private readonly CancellationTokenSource _cts = new();
    private Task? _writer;
    private Task? _heartbeat;
    private long _lastInboundTicks = DateTime.UtcNow.Ticks;

    /// <summary>
    /// How long the peer may stay silent before we declare the link dead. A TCP connection
    /// carried over an adb forward can stop delivering without either end receiving a FIN - the
    /// phone sleeps, the adb server restarts, the cable is nudged.
    /// </summary>
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(16);

    public string Transport { get; }
    public ClientHello? Hello { get; private set; }

    /// <summary>
    /// The cable is trusted by being a cable. A network peer is not served anything until it has
    /// proved it holds the pairing key, and until then it cannot displace a working link.
    /// </summary>
    public bool Authenticated { get; set; }
    public string? HostNonce { get; set; }
    public int AuthFailures { get; set; }
    /// <summary>Set by the server: takes over the PC for this link once it is allowed to.</summary>
    public Action? Claim { get; set; }

    /// <summary>Fires for each inbound JSON message, with the parsed "type" discriminator.</summary>
    public event Func<string, JsonDocument, Task>? OnMessage;

    public DeckLink(Stream stream, string transport, IDisposable? owner = null)
    {
        _stream = stream;
        Transport = transport;
        Authenticated = transport == "usb";
        _owner = owner;
    }

    public void Start()
    {
        _writer = Task.Run(() => WriteLoopAsync(_cts.Token));
        _heartbeat = Task.Run(() => HeartbeatAsync(_cts.Token));
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PingInterval, ct).ConfigureAwait(false);

                var silence = DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastInboundTicks));
                if (silence > SilenceLimit)
                {
                    Log.Warn($"peer silent for {silence.TotalSeconds:F0}s - dropping link so it can reconnect");
                    Kill();
                    return;
                }

                Send(FrameKind.Ping, Array.Empty<byte>());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Debug($"heartbeat ended: {ex.Message}"); }
    }

    /// <summary>
    /// Forces the link down. Cancelling alone is not always enough: a socket read that is
    /// already blocked in the kernel may not observe the token, so the stream is closed
    /// underneath it to guarantee the read loop unblocks.
    /// </summary>
    private void Kill()
    {
        // The link may already have been disposed by its own teardown, which disposes the
        // token source; cancelling a disposed one throws, and that must not matter here.
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { _stream.Close(); } catch { }
        try { _owner?.Dispose(); } catch { }
    }

    /// <summary>
    /// Ends this link from outside, for a caller that has decided another link replaces it
    /// or that the device behind it is gone. Safe at any time and any number of times.
    /// </summary>
    public void Abort() => Kill();

    public async Task ReadLoopAsync(CancellationToken external)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(external, _cts.Token);
        var ct = linked.Token;

        while (!ct.IsCancellationRequested)
        {
            using var frame = await Frame.ReadAsync(_stream, ct).ConfigureAwait(false);

            // Any frame at all proves the peer is alive, including its Pong replies.
            Interlocked.Exchange(ref _lastInboundTicks, DateTime.UtcNow.Ticks);

            switch (frame.Kind)
            {
                case FrameKind.Json:
                    // Decode before awaiting: a span cannot cross an async boundary.
                    await DispatchAsync(Encoding.UTF8.GetString(frame.Span)).ConfigureAwait(false);
                    break;

                case FrameKind.Ping:
                    Send(FrameKind.Pong, Array.Empty<byte>());
                    break;

                case FrameKind.Pong:
                    break;
            }
        }
    }

    private async Task DispatchAsync(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { Log.Warn($"malformed message: {ex.Message}"); return; }

        try
        {
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            if (type == "hello")
            {
                Hello = JsonSerializer.Deserialize<ClientHello>(doc.RootElement.GetRawText(), Json.Options);
                Log.Info($"tablet: {Hello?.Device} {Hello?.Width}x{Hello?.Height} " +
                         $"app {Hello?.Version}, {Hello?.CachedAssets?.Count ?? 0} cached assets");
            }

            var handler = OnMessage;
            if (handler is not null)
            {
                try { await handler(type, doc).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // A defect while handling one message must not kill the link. It did: a
                    // NullReferenceException thrown out of a handler propagated through the read
                    // loop, dropped the connection, and the tablet reconnected straight into the
                    // same fault - roughly twenty times a minute, with nothing on screen and a
                    // log that said only "Object reference not set to an instance of an object".
                    Log.Error($"handler for '{type}' failed: {ex}");
                }
            }
        }
        finally { doc.Dispose(); }
    }


    public void SendJson<T>(T message) => Send(FrameKind.Json, Json.Serialize(message));

    /// <summary>Asset frame: 32 ASCII hex chars of key, then the raw bytes.</summary>
    public void SendAsset(string key, byte[] bytes)
    {
        var payload = new byte[32 + bytes.Length];
        Encoding.ASCII.GetBytes(key, 0, 32, payload, 0);
        bytes.CopyTo(payload, 32);
        Send(FrameKind.Asset, payload);
    }

    private void Send(FrameKind kind, byte[] payload)
    {
        if (_cts.IsCancellationRequested) return;
        _out.Writer.TryWrite(new Outbound(kind, payload));
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _out.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await Frame.WriteAsync(_stream, item.Kind, item.Payload, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Debug($"write loop ended: {ex.Message}"); }
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        // Idempotent: a link superseded by a newer one is aborted by its replacement and
        // then disposed again by its own teardown, and the second pass used to cancel a
        // token source the first had already disposed.
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        _cts.Cancel();
        _out.Writer.TryComplete();
        try { if (_writer is not null) await _writer.ConfigureAwait(false); } catch { }
        try { if (_heartbeat is not null) await _heartbeat.ConfigureAwait(false); } catch { }
        try { await _stream.DisposeAsync().ConfigureAwait(false); } catch { }
        _owner?.Dispose();
        _cts.Dispose();
    }
}
