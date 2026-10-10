package com.companiondeck

import android.content.Context
import java.io.File

/**
 * Content-addressed cache for artwork and app icons. Images are the only large payloads in this
 * protocol, so each one crosses the link exactly once: the host advertises a key (a hash of the
 * bytes), and we only ask for the bytes if we don't already have that key on disk.
 */
class AssetStore(context: Context) {

    private val dir: File = File(context.filesDir, "assets").apply { mkdirs() }

    // Bound the cache so a long-lived install can't fill a tablet with 5 GB free.
    private val maxBytes = 24L * 1024 * 1024

    fun has(key: String): Boolean = safeFile(key)?.exists() == true

    fun read(key: String): ByteArray? =
        safeFile(key)?.takeIf { it.exists() }?.readBytes()

    fun write(key: String, bytes: ByteArray) {
        val f = safeFile(key) ?: return
        try {
            val tmp = File(f.absolutePath + ".tmp")
            tmp.writeBytes(bytes)
            if (!tmp.renameTo(f)) { tmp.delete(); return }
            f.setLastModified(System.currentTimeMillis())
            trimIfNeeded()
        } catch (_: Exception) { /* cache write failure is never fatal */ }
    }

    fun knownKeys(): List<String> =
        dir.listFiles()?.filter { it.isFile && !it.name.endsWith(".tmp") }?.map { it.name } ?: emptyList()

    /** Keys are hex hashes from the host; reject anything else so nothing escapes the dir. */
    private fun safeFile(key: String): File? {
        if (key.isEmpty() || key.length > 64) return null
        if (!key.all { it in '0'..'9' || it in 'a'..'f' }) return null
        return File(dir, key)
    }

    private fun trimIfNeeded() {
        val files = dir.listFiles()?.filter { it.isFile } ?: return
        var total = files.sumOf { it.length() }
        if (total <= maxBytes) return
        // Evict least-recently-used until comfortably under the cap.
        for (f in files.sortedBy { it.lastModified() }) {
            if (total <= maxBytes * 3 / 4) break
            total -= f.length()
            f.delete()
        }
    }
}
