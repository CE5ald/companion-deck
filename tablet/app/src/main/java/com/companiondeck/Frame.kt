package com.companiondeck

import java.io.DataInputStream
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream

/**
 * Wire framing, mirroring host/CompanionDeck.Host/Protocol/Frame.cs. `uint32 be payloadLength` +
 * `uint8 kind` + `payload...` The length covers the payload only.
 */
object Frame {
    const val JSON: Byte = 0
    const val ASSET: Byte = 1
    const val PING: Byte = 2
    const val PONG: Byte = 3

    const val MAX_PAYLOAD = 8 * 1024 * 1024

    class Packet(val kind: Byte, val payload: ByteArray)

    /**
     * Thrown when the read timed out with no frame in progress. This is recoverable: nothing was
     * consumed, so the caller can simply look again.
     */
    class IdleTimeout : IOException("no data from peer")

    @Throws(IOException::class)
    fun write(out: OutputStream, kind: Byte, payload: ByteArray) {
        if (payload.size > MAX_PAYLOAD) throw IOException("frame too large: ${payload.size}")
        val header = ByteArray(5)
        val n = payload.size
        header[0] = (n ushr 24 and 0xFF).toByte()
        header[1] = (n ushr 16 and 0xFF).toByte()
        header[2] = (n ushr 8 and 0xFF).toByte()
        header[3] = (n and 0xFF).toByte()
        header[4] = kind
        // Single write for the header, then the body; callers flush.
        out.write(header)
        if (n > 0) out.write(payload)
        out.flush()
    }

    @Throws(IOException::class)
    fun read(input: InputStream): Packet {
        val din = if (input is DataInputStream) input else DataInputStream(input)

        // Only this first byte may time out harmlessly; past it we are committed to a frame.
        val b0 = try { din.read() } catch (t: java.net.SocketTimeoutException) { throw IdleTimeout() }
        if (b0 < 0) throw EOFException("peer closed")
        val b1 = din.read(); val b2 = din.read(); val b3 = din.read()
        if (b1 < 0 || b2 < 0 || b3 < 0) throw EOFException("peer closed mid-header")

        val len = (b0 shl 24) or (b1 shl 16) or (b2 shl 8) or b3
        if (len < 0 || len > MAX_PAYLOAD) throw IOException("bad frame length $len")

        val kind = din.read()
        if (kind < 0) throw EOFException("peer closed before kind byte")

        if (len == 0) return Packet(kind.toByte(), ByteArray(0))

        val payload = ByteArray(len)
        din.readFully(payload)
        return Packet(kind.toByte(), payload)
    }
}
