package com.companiondeck

import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import org.json.JSONObject

enum class LinkPhase { Idle, Listening, Connected }

data class LinkState(
    val phase: LinkPhase = LinkPhase.Idle,
    val transport: String = "",
    val hostName: String = "",
    val since: Long = 0L,
    /** "unpaired" while a network host is refused for want of a key; blank otherwise. */
    val note: String = "",
    /** This tablet holds the key that lets it use the network fallback. */
    val paired: Boolean = false
)

/**
 * Single-process message bus between the connection service and the WebView UI. Using a shared
 * object rather than a bound Service binder keeps the activity free to be destroyed and recreated
 * (rotation, memory pressure on a 1 GB device) without tearing down or re-handshaking the laptop
 * link.
 */
object DeckBus {

    /** Frames arriving from the laptop, already decoded to JSON text. */
    val incoming = MutableSharedFlow<String>(replay = 0, extraBufferCapacity = 128)

    /** Latest payload per section, so a freshly created activity can paint immediately. */
    private val latest = LinkedHashMap<String, String>()

    val link = MutableStateFlow(LinkState())

    /** Installed by whichever transport currently owns the link. */
    @Volatile
    var sender: ((Byte, ByteArray) -> Unit)? = null

    @Synchronized
    fun remember(json: String) {
        val type = try { JSONObject(json).optString("type") } catch (_: Exception) { "" }
        if (type.isNotEmpty()) latest[type] = json
    }

    @Synchronized
    fun snapshot(): List<String> = latest.values.toList()

    @Synchronized
    fun clearSnapshot() = latest.clear()

    fun sendJson(json: String) {
        sender?.invoke(Frame.JSON, json.toByteArray(Charsets.UTF_8))
    }
}
