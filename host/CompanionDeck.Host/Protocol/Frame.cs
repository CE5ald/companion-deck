using System.Buffers;
using System.Buffers.Binary;

namespace CompanionDeck.Host.Protocol;

public enum FrameKind : byte
{
    /// <summary>UTF-8 JSON control message.</summary>
    Json = 0,
    /// <summary>Binary asset: 16-byte key followed by raw bytes (artwork, icons, cover art).</summary>
    Asset = 1,
    /// <summary>Keepalive. Empty payload.</summary>
    Ping = 2,
    Pong = 3,
}

/// <summary>
/// Length-prefixed framing shared by every transport (USB/adb, TCP, and later Bluetooth). Layout:
/// [uint32 be payloadLength][uint8 kind][payload...] The length covers only the payload, not the
/// kind byte.
/// </summary>
public static class Frame
{
    public const int MaxPayload = 8 * 1024 * 1024;
    private const int HeaderSize = 5;

    public static async ValueTask WriteAsync(
        Stream stream, FrameKind kind, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > MaxPayload)
            throw new InvalidOperationException($"Frame payload {payload.Length} exceeds {MaxPayload}");

        var header = ArrayPool<byte>.Shared.Rent(HeaderSize);
        try
        {
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)payload.Length);
            header[4] = (byte)kind;
            await stream.WriteAsync(header.AsMemory(0, HeaderSize), ct).ConfigureAwait(false);
            if (!payload.IsEmpty)
                await stream.WriteAsync(payload, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }
    }

    /// <summary>
    /// Reads one frame. The returned buffer is rented from the shared pool and the caller
    /// must dispose the result to return it.
    /// </summary>
    public static async ValueTask<RentedFrame> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        await ReadExactAsync(stream, header, ct).ConfigureAwait(false);

        uint len = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
        var kind = (FrameKind)header[4];

        if (len > MaxPayload)
            throw new InvalidDataException($"Frame length {len} exceeds cap; stream desynchronised.");

        if (len == 0)
            return new RentedFrame(kind, Array.Empty<byte>(), 0, pooled: false);

        var buf = ArrayPool<byte>.Shared.Rent((int)len);
        try
        {
            await ReadExactAsync(stream, buf.AsMemory(0, (int)len), ct).ConfigureAwait(false);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buf);
            throw;
        }

        return new RentedFrame(kind, buf, (int)len, pooled: true);
    }

    private static async ValueTask ReadExactAsync(Stream stream, Memory<byte> dest, CancellationToken ct)
    {
        int read = 0;
        while (read < dest.Length)
        {
            int n = await stream.ReadAsync(dest[read..], ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("Peer closed the connection.");
            read += n;
        }
    }
}

public readonly struct RentedFrame : IDisposable
{
    private readonly byte[] _buffer;
    private readonly bool _pooled;

    public FrameKind Kind { get; }
    public int Length { get; }
    public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, Length);
    public ReadOnlyMemory<byte> Memory => _buffer.AsMemory(0, Length);

    internal RentedFrame(FrameKind kind, byte[] buffer, int length, bool pooled)
    {
        Kind = kind; _buffer = buffer; Length = length; _pooled = pooled;
    }

    public void Dispose()
    {
        if (_pooled && _buffer.Length > 0) ArrayPool<byte>.Shared.Return(_buffer);
    }
}
