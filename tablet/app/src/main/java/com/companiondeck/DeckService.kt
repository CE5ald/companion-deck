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
import android.util.Base64
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
import java.security.MessageDigest
import java.security.SecureRandom
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

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

        /** The network handshake gets this long, end to end, before the link is abandoned. */
        private const val AUTH_WINDOW_MS = 8000L
        private const val PREFS = "deck"
        private const val KEY_PAIR = "pairKey"

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
        DeckLog.file = java.io.File(filesDir, "deck.log")
        assets = AssetStore(this)
        goForeground(getString(R.string.svc_waiting))
        holdMulticast()
        scope.launch { usbLoop() }
        scope.launch { wifiLoop() }
        DeckLog.i(TAG, "service started")
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int = START_STICKY

    override fun onDestroy() {
        running = false
        try { activeLink?.close() } catch (_: Exception) {}
        releaseWakeLock()
        try { multicastLock?.takeIf { it.isHeld }?.release() } catch (_: Exception) {}
        scope.cancel()
        super.onDestroy()
        DeckLog.i(TAG, "service stopped")
    }


    private suspend fun usbLoop() {
        while (scope.isActive) {
            var server: LocalServerSocket? = null
            try {
                server = LocalServerSocket(ABSTRACT_SOCKET)
                setPhase(LinkPhase.Listening, "usb")
                DeckLog.i(TAG, "listening on localabstract:$ABSTRACT_SOCKET")

                while (scope.isActive) {
                    val sock: LocalSocket = server.accept()
                    // Bounded read so a host that stops responding is noticed rather than
                    // leaving us blocked forever on a socket that will never speak again.
                    sock.soTimeout = READ_TIMEOUT_MS
                    DeckLog.i(TAG, "USB peer connected")
                    serve(
                        transport = "usb",
                        closeable = sock,
                        input = BufferedInputStream(sock.inputStream, 16 * 1024),
                        output = BufferedOutputStream(sock.outputStream, 16 * 1024)
                    )
                }
            } catch (e: Exception) {
                if (scope.isActive) DeckLog.w(TAG, "usb loop: ${e.message}")
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
                        DeckLog.i(TAG, "beacon from $host:$port")
                        connectWifi(host, port)

                        // A host that cannot be authenticated will be refused again on the next
                        // beacon, two seconds later. Trying every two seconds forever helps no
                        // one, so wait; the cable, which pairs, is detected independently.
                        if (DeckBus.link.value.note == "unpaired") delay(10_000)

                        // Beacons that arrived while that link was up describe a host
                        // address that was current then. Acting on them all at once, the
                        // moment the link ended, produced a burst of connect-and-drop
                        // cycles that flickered the UI during every recovery.
                        discardPending(udp, buf)
                    }
                }
            } catch (e: Exception) {
                if (scope.isActive) DeckLog.w(TAG, "wifi loop: ${e.message}")
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
            DeckLog.w(TAG, "wifi connect failed: ${e.message}")
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
        // The page and the activity send from the UI thread, and Android refuses network writes
        // there (USB sockets are exempt, which is why this only ever showed on Wi-Fi). Every
        // send is queued to one writer thread instead, which also keeps frames in order.
        val writer = java.util.concurrent.Executors.newSingleThreadExecutor()
        DeckBus.sender = { kind, payload ->
            try { writer.execute { writeOut(output, closeable, writeLock, kind, payload) } }
            catch (_: java.util.concurrent.RejectedExecutionException) { /* the link is already over */ }
        }

        val nonce = newNonce()
        if (transport == "wifi") {
            // The cable is trusted by being a cable. The network is not: the laptop and this
            // tablet each prove they hold the key before either tells the other anything, and the
            // UI is not told there is a link until both have.
            sendHello(nonce)
            if (!authenticate(nonce, input, output, writeLock)) {
                if (linkGeneration == generation) {
                    DeckBus.sender = null
                    activeLink = null
                    releaseWakeLock()
                }
                writer.shutdownNow()
                try { closeable.close() } catch (_: Exception) {}
                return
            }
            setPhase(LinkPhase.Connected, transport)
        } else {
            setPhase(LinkPhase.Connected, transport)
            sendHello(nonce)
        }

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
                        DeckLog.w(TAG, "host silent for ${SILENCE_LIMIT_MS}ms - dropping link")
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
            DeckLog.i(TAG, "link closed: ${e.message}")
        } finally {
            if (linkGeneration == generation) {
                DeckBus.sender = null
                activeLink = null
                DeckBus.clearSnapshot()
                setPhase(LinkPhase.Listening, transport)
                releaseWakeLock()
                updateNotification(getString(R.string.svc_waiting))
            }
            writer.shutdownNow()
            try { closeable.close() } catch (_: Exception) {}
        }
    }

    private fun writeOut(output: OutputStream, closeable: Closeable, lock: Any, kind: Byte, payload: ByteArray) {
        try {
            synchronized(lock) { Frame.write(output, kind, payload) }
        } catch (e: Exception) {
            DeckLog.w(TAG, "send failed: $e")
            try { closeable.close() } catch (_: Exception) {}
        }
    }

    private fun newNonce(): String {
        val b = ByteArray(16)
        SecureRandom().nextBytes(b)
        return Base64.encodeToString(b, Base64.NO_WRAP)
    }

    private fun proof(key: String, message: String): String {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(Base64.decode(key, Base64.DEFAULT), "HmacSHA256"))
        return Base64.encodeToString(mac.doFinal(message.toByteArray(Charsets.UTF_8)), Base64.NO_WRAP)
    }

    private fun setNote(note: String) {
        if (DeckBus.link.value.note != note) DeckBus.link.value = DeckBus.link.value.copy(note = note)
    }

    /**
     * Both directions, so a stranger's laptop cannot pass for yours any more than a stranger's
     * tablet can drive it: check the host's proof, answer with ours, wait for its acceptance.
     */
    private fun authenticate(
        nonce: String,
        input: java.io.InputStream,
        output: OutputStream,
        writeLock: Any
    ): Boolean {
        val key = getSharedPreferences(PREFS, MODE_PRIVATE).getString(KEY_PAIR, null)
        if (key == null) {
            DeckLog.i(TAG, "network host found, but this tablet has not been paired by cable yet")
            setNote("unpaired")
            return false
        }

        val deadline = System.currentTimeMillis() + AUTH_WINDOW_MS
        var hostNonce: String? = null
        try {
            while (System.currentTimeMillis() < deadline) {
                val pkt = try { Frame.read(input) } catch (_: Frame.IdleTimeout) { continue }
                if (pkt.kind == Frame.PING) {
                    synchronized(writeLock) { Frame.write(output, Frame.PONG, ByteArray(0)) }
                    continue
                }
                if (pkt.kind != Frame.JSON) continue

                val msg = try { JSONObject(String(pkt.payload, Charsets.UTF_8)) } catch (_: Exception) { continue }
                when (msg.optString("type")) {
                    "hello" -> {
                        val hn = msg.optString("nonce")
                        val expected = proof(key, "host|$nonce|$hn")
                        if (hn.isEmpty() || !MessageDigest.isEqual(
                                expected.toByteArray(Charsets.UTF_8),
                                msg.optString("proof").toByteArray(Charsets.UTF_8))) {
                            DeckLog.w(TAG, "network host could not prove it holds this tablet's key")
                            setNote("unpaired")
                            return false
                        }
                        hostNonce = hn
                        DeckBus.sendJson(JSONObject().apply {
                            put("type", "auth")
                            put("proof", proof(key, "tablet|$hn|$nonce"))
                        }.toString())
                    }
                    "authed" -> if (hostNonce != null) { setNote(""); DeckLog.i(TAG, "authenticated with the laptop"); return true }
                }
            }
        } catch (e: Exception) {
            DeckLog.i(TAG, "authentication ended: ${e.message}")
            return false
        }
        DeckLog.w(TAG, "network authentication timed out")
        return false
    }

    private fun sendHello(nonce: String) {
        val hello = JSONObject().apply {
            put("type", "hello")
            put("nonce", nonce)
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
        val json = try { JSONObject(text) } catch (_: Exception) { JSONObject() }
        val type = json.optString("type")
        // A limit was crossed. Handled here rather than in the page, because the page may be asleep.
        if (type == "alert") alertUser()
        if (type == "hello") {
            val name = json.optString("hostName")
            // Only the cable ever carries the key, so receiving one means this tablet is paired.
            val pair = json.optString("pair")
            if (pair.isNotEmpty()) {
                getSharedPreferences(PREFS, MODE_PRIVATE).edit().putString(KEY_PAIR, pair).apply()
            }
            DeckBus.link.value = DeckBus.link.value.copy(hostName = name, paired = hasKey(), note = "")
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


    /**
     * Gets the person's attention for an alert they asked for: a buzz if the tablet can, a sound if it
     * cannot, and the screen on so the banner can be read. Each part is best effort; none may break
     * the link.
     */
    private fun alertUser() {
        try {
            val vib = getSystemService(Context.VIBRATOR_SERVICE) as? android.os.Vibrator
            if (vib != null && vib.hasVibrator()) {
                val pattern = longArrayOf(0, 350, 150, 350)
                if (Build.VERSION.SDK_INT >= 26)
                    vib.vibrate(android.os.VibrationEffect.createWaveform(pattern, -1))
                else @Suppress("DEPRECATION") vib.vibrate(pattern, -1)
            } else {
                val tone = android.media.RingtoneManager.getRingtone(
                    applicationContext, android.media.RingtoneManager.getDefaultUri(android.media.RingtoneManager.TYPE_NOTIFICATION))
                tone?.play()
            }
        } catch (e: Exception) { DeckLog.w(TAG, "alert sound failed: $e") }

        try {
            val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
            @Suppress("DEPRECATION")
            val wl = pm.newWakeLock(
                PowerManager.SCREEN_BRIGHT_WAKE_LOCK or PowerManager.ACQUIRE_CAUSES_WAKEUP, "CompanionDeck:alert")
            wl.acquire(4000)
        } catch (e: Exception) { DeckLog.w(TAG, "could not wake the screen: $e") }
    }

    private fun hasKey(): Boolean =
        getSharedPreferences(PREFS, MODE_PRIVATE).getString(KEY_PAIR, null) != null

    private fun setPhase(phase: LinkPhase, transport: String) {
        DeckBus.link.value = DeckBus.link.value.copy(
            phase = phase,
            transport = transport,
            since = System.currentTimeMillis(),
            paired = hasKey()
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
            DeckLog.w(TAG, "multicast lock unavailable: ${e.message}")
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
            DeckLog.w(TAG, "foreground start refused: ${e.message}")
        }
    }

    private fun updateNotification(text: String) {
        try {
            val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            nm.notify(NOTIF_ID, buildNotification(text))
        } catch (_: Exception) {}
    }
}
