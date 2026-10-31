package com.companiondeck

import android.util.Log
import java.io.File

/**
 * Logging that also lands in a file, because some tablets simply do not provide logcat to
 * ordinary apps and a link that fails without a trace is impossible to diagnose. Kept small: when
 * it passes 96 KB the older half is dropped.
 *
 * A debug build can read it with: adb shell run-as com.companiondeck cat files/deck.log
 */
object DeckLog {
    @Volatile var file: File? = null
    private const val MAX_BYTES = 96 * 1024L

    @Synchronized
    private fun append(level: String, tag: String, msg: String) {
        val f = file ?: return
        try {
            if (f.length() > MAX_BYTES) {
                val tail = f.readText().takeLast((MAX_BYTES / 2).toInt())
                f.writeText(tail.substringAfter('\n', tail))
            }
            val t = java.text.SimpleDateFormat("HH:mm:ss.SSS", java.util.Locale.US).format(java.util.Date())
            f.appendText("$t $level $tag: $msg\n")
        } catch (_: Exception) { /* a log must never be the thing that breaks the link */ }
    }

    fun i(tag: String, msg: String) { Log.i(tag, msg); append("I", tag, msg) }
    fun w(tag: String, msg: String) { Log.w(tag, msg); append("W", tag, msg) }
}
