package com.companiondeck

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import androidx.core.content.ContextCompat

/**
 * Brings the link service back after a reboot or an app update, so the tablet is ready
 * the moment it powers on without anyone opening the app first.
 */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent?) {
        when (intent?.action) {
            Intent.ACTION_BOOT_COMPLETED,
            Intent.ACTION_MY_PACKAGE_REPLACED -> {
                if (DeckService.running) return
                // startService from a receiver is refused on Android 8+. Receiving
                // BOOT_COMPLETED is one of the exemptions that lets a foreground service
                // start from the background, so this is the one route that works.
                ContextCompat.startForegroundService(
                    context, Intent(context, DeckService::class.java))
            }
        }
    }
}
