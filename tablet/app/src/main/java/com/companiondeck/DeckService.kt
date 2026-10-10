package com.companiondeck

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.pm.ServiceInfo
import android.content.Intent
import android.net.LocalServerSocket
import android.net.LocalSocket
import android.net.wifi.WifiManager
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import android.util.Log
import androidx.core.app.ServiceCompat
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.Closeable
import java.io.OutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.net.Socket

/**
 * Owns the link to the laptop for the whole life of the app. Two transports, both requiring zero
 * user setup: - USB: we listen on the abstract local socket "companiondeck".
 */
class DeckService : Service() {

    companion object {
        private const val TAG = "DeckService"
        const val ABSTRACT_SOCKET = "companiondeck"
        const val DISCOVERY_PORT = 45888

        /** Socket read timeout. The host pings every 5s, so silence far past that is real. */
        private const val READ_TIMEOUT_MS = 6000

        /** Give up on the link after this much total silence. */
        private const val SILENCE_LIMIT_MS = 18_000L
        private const val CHANNEL_ID = "deck_link"
        private const val NOTIF_ID = 1

        @Volatile var running: Boolean = false
            private set
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private lateinit var assets: AssetStore
    private var wakeLock: PowerManager.WakeLock? = null
    private var multicastLock: WifiManager.MulticastLock? = null

    @Volatile private var activeLink: Closeable? = null
    @Volatile private var linkGeneration = 0

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        running = true
        assets = AssetStore(this)
        goForeground(getString(R.string.svc_waiting))
        holdMulticast()
        scope.launch { usbLoop() }
        scope.launch { wifiLoop() }
        Log.i(TAG, "service started")
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int = START_STICKY

    override fun onDestroy() {
        running = false
        try { activeLink?.close() } catch (_: Exception) {}
        releaseWakeLock()
        try { multicastLock?.takeIf { it.isHeld }?.release() } catch (_: Exception) {}
        scope.cancel()
        super.onDestroy()
        Log.i(TAG, "service stopped")
    }


    private suspend fun usbLoop() {
        while (scope.isActive) {
            var server: LocalServerSocket? = null
            try {
                server = LocalServerSocket(ABSTRACT_SOCKET)
                setPhase(LinkPhase.Listening, "usb")
                Log.i(TAG, "listening on localabstract:$ABSTRACT_SOCKET")

                while (scope.isActive) {
                    val sock: LocalSocket = server.accept()
                    // Bounded read so a host that stops responding is noticed rather than
                    // leaving us blocked forever on a socket that will never speak again.
                    sock.soTimeout = READ_TIMEOUT_MS
                    Log.i(TAG, "USB peer connected")
                    serve(
                        transport = "usb",
                        closeable = sock,
                        input = BufferedInputStream(sock.inputStream, 16 * 1024),
                        output = BufferedOutputStream(sock.outputStream, 16 * 1024)
                    )
                }
            } catch (e: Exception) {
                if (scope.isActive) Log.w(TAG, "usb loop: ${e.message}")
            } finally {
                try { server?.close() } catch (_: Exception) {}
            }
            // The abstract name lingers briefly after a crash; back off before rebinding.
            delay(1200)
        }
    }


    private suspend fun wifiLoop() {
        while (scope.isActive) {
            try {
                DatagramSocket(null).use { udp ->
                    udp.reuseAddress = true
                    udp.broadcast = true
                    udp.bind(InetSocketAddress(DISCOVERY_PORT))
                    udp.soTimeout = 4000

                    val buf = ByteArray(1024)
                    while (scope.isActive) {
                        if (activeLink != null) { discardPending(udp, buf); delay(1500); continue }

                        val pkt = DatagramPacket(buf, buf.size)
                        try { udp.receive(pkt) } catch (_: Exception) { continue }

                        val text = String(pkt.data, 0, pkt.length, Charsets.UTF_8)
                        val beacon = try { JSONObject(text) } catch (_: Exception) { continue }
                        if (beacon.optString("app") != "companiondeck") continue

                        val port = beacon.optInt("port", 0)
                        if (port <= 0) continue
                        val host = pkt.address.hostAddress ?: continue

                        if (activeLink != null) continue
                        Log.i(TAG, "beacon from $host:$port")
                        connectWifi(host, port)

                        // Beacons that arrived while that link was up describe a host
                        // address that was current then. Acting on them all at once, the
                        // moment the link ended, produced a burst of connect-and-drop
                        // cycles that flickered the UI during every recovery.
                        discardPending(udp, buf)
                    }
                }
            } catch (e: Exception) {
                if (scope.isActive) Log.w(TAG, "wifi loop: ${e.message}")
                delay(3000)
            }
        }
    }

    /** Empties the socket's receive queue without waiting for anything new to arrive. */
    private fun discardPending(udp: DatagramSocket, buf: ByteArray) {
        val previous = udp.soTimeout
        try {
            udp.soTimeout = 30
            while (true) udp.receive(DatagramPacket(buf, buf.size))
        } catch (_: Exception) {
            // The timeout is the normal exit: the queue is empty.
        } finally {
            try { udp.soTimeout = previous } catch (_: Exception) {}
        }
    }

    private fun connectWifi(host: String, port: Int) {
        try {
            val s = Socket()
            s.tcpNoDelay = true
            s.connect(InetSocketAddress(host, port), 4000)
            s.soTimeout = READ_TIMEOUT_MS
            serve(
                transport = "wifi",
                closeable = s,
                input = BufferedInputStream(s.getInputStream(), 16 * 1024),
                output = BufferedOutputStream(s.getOutputStream(), 16 * 1024)
            )
        } catch (e: Exception) {
            Log.w(TAG, "wifi connect failed: ${e.message}")
        }
    }


    private fun serve(
        transport: String,
        closeable: Closeable,
        input: java.io.InputStream,
        output: OutputStream
    ) {
        val generation = ++linkGeneration
        activeLink = closeable
        acquireWakeLock()

        val writeLock = Any()
        DeckBus.sender = { kind, payload ->
            try {
                synchronized(writeLock) { Frame.write(output, kind, payload) }
            } catch (e: Exception) {
                Log.w(TAG, "send failed: ${e.message}")
                try { closeable.close() } catch (_: Exception) {}
            }
        }

        setPhase(LinkPhase.Connected, transport)
        sendHello()

        var lastRx = System.currentTimeMillis()

        try {
            while (linkGeneration == generation) {
                val pkt = try {
                    Frame.read(input)
                } catch (idle: Frame.IdleTimeout) {
                    // Nothing was consumed, so it is safe to look again - but if the host
                    // has been quiet well past its ping interval, treat the link as dead
                    // and fall back to accepting a fresh one.
                    if (System.currentTimeMillis() - lastRx > SILENCE_LIMIT_MS) {
                        Log.w(TAG, "host silent for ${SILENCE_LIMIT_MS}ms - dropping link")
                        break
                    }
                    continue
                }

                lastRx = System.currentTimeMillis()

                when (pkt.kind) {
                    Frame.JSON -> handleJson(String(pkt.payload, Charsets.UTF_8))
                    Frame.ASSET -> handleAsset(pkt.payload)
                    Frame.PING -> synchronized(writeLock) { Frame.write(output, Frame.PONG, ByteArray(0)) }
                    Frame.PONG -> {}
                }
            }
        } catch (e: Exception) {
            Log.i(TAG, "link closed: ${e.message}")
        } finally {
            if (linkGeneration == generation) {
                DeckBus.sender = null
                activeLink = null
                DeckBus.clearSnapshot()
                setPhase(LinkPhase.Listening, transport)
                releaseWakeLock()
                updateNotification(getString(R.string.svc_waiting))
            }
            try { closeable.close() } catch (_: Exception) {}
        }
    }

    private fun sendHello() {
        val hello = JSONObject().apply {
            put("type", "hello")
            put("device", Build.MODEL)
            put("android", Build.VERSION.SDK_INT)
            put("width", resources.displayMetrics.widthPixels)
            put("height", resources.displayMetrics.heightPixels)
            // Logical size is what the UI actually lays out in; the pixel counts above
            // mean nothing without it once the screen is not 1:1.
            put("dpWidth", (resources.displayMetrics.widthPixels / resources.displayMetrics.density).toInt())
            put("dpHeight", (resources.displayMetrics.heightPixels / resources.displayMetrics.density).toInt())
            put("density", resources.displayMetrics.density.toDouble())
            put("version", BuildConfig.VERSION_NAME)
            put("cachedAssets", JSONArray(assets.knownKeys()))
        }
        DeckBus.sendJson(hello.toString())
    }

    private fun handleJson(text: String) {
        val type = try { JSONObject(text).optString("type") } catch (_: Exception) { "" }
        if (type == "hello") {
            val name = try { JSONObject(text).optString("hostName") } catch (_: Exception) { "" }
            DeckBus.link.value = DeckBus.link.value.copy(hostName = name)
            updateNotification(getString(R.string.svc_connected, name))
        }
        DeckBus.remember(text)
        DeckBus.incoming.tryEmit(text)
    }

    /** Asset frame: 32 ASCII hex chars of key, followed by the raw image bytes. */
    private fun handleAsset(payload: ByteArray) {
        if (payload.size <= 32) return
        val key = String(payload, 0, 32, Charsets.US_ASCII)
        val bytes = payload.copyOfRange(32, payload.size)
        assets.write(key, bytes)
        DeckBus.incoming.tryEmit(JSONObject().apply {
            put("type", "assetReady")
            put("key", key)
        }.toString())
    }


    private fun setPhase(phase: LinkPhase, transport: String) {
        DeckBus.link.value = DeckBus.link.value.copy(
            phase = phase,
            transport = transport,
            since = System.currentTimeMillis()
        )
    }

    /**
     * Without this, Android discards broadcast and multicast frames while the Wi-Fi radio is
     * in power-save, which is where an idle tablet with its screen off spends its time. The
     * host announces itself with a UDP broadcast, so a sleeping tablet never heard it: the
     * network fallback worked only while the screen happened to be on. Held for the life of
     * the service rather than per link, because the whole point is to hear the first beacon
     * before any link exists. The cost is a radio that wakes for broadcast frames, which on
     * a tablet that is plugged in over USB is not a cost anyone can see.
     */
    private fun holdMulticast() {
        try {
            val wifi = applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
                ?: return
            multicastLock = wifi.createMulticastLock("CompanionDeck:beacon").apply {
                setReferenceCounted(false)
                acquire()
            }
        } catch (e: Exception) {
            Log.w(TAG, "multicast lock unavailable: ${e.message}")
        }
    }

    private fun acquireWakeLock() {
        if (wakeLock != null) return
        val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
        wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "CompanionDeck:link").apply {
            setReferenceCounted(false)
            acquire()
        }
    }

    private fun releaseWakeLock() {
        try { wakeLock?.takeIf { it.isHeld }?.release() } catch (_: Exception) {}
        wakeLock = null
    }

    private fun buildNotification(text: String): Notification {
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val ch = NotificationChannel(CHANNEL_ID, getString(R.string.svc_channel), NotificationManager.IMPORTANCE_MIN)
            ch.setShowBadge(false)
            nm.createNotificationChannel(ch)
        }
        val tap = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java),
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M)
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
            else PendingIntent.FLAG_UPDATE_CURRENT
        )

        val b = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O)
            Notification.Builder(this, CHANNEL_ID) else @Suppress("DEPRECATION") Notification.Builder(this)

        return b.setContentTitle(getString(R.string.app_name))
            .setContentText(text)
            .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
            .setContentIntent(tap)
            .setOngoing(true)
            .build()
    }

    /**
     * Android 14 wants the service type declared at the call site as well as in the
     * manifest, and Android 12 can refuse a foreground start outright if the app was not
     * eligible at that moment. Neither is fatal here - the link itself is just a socket -
     * so a refusal downgrades to a plain background service rather than crashing.
     */
    private fun goForeground(text: String) {
        try {
            ServiceCompat.startForeground(
                this, NOTIF_ID, buildNotification(text),
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q)
                    ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE else 0
            )
        } catch (e: Exception) {
            Log.w(TAG, "foreground start refused: ${e.message}")
        }
    }

    private fun updateNotification(text: String) {
        try {
            val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            nm.notify(NOTIF_ID, buildNotification(text))
        } catch (_: Exception) {}
    }
}
