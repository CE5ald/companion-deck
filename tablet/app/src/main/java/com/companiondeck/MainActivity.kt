package com.companiondeck

import android.Manifest
import android.annotation.SuppressLint
import android.content.ComponentCallbacks2
import android.content.Intent
import android.content.SharedPreferences
import android.content.pm.PackageManager
import android.os.Build
import android.provider.Settings
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.View
import android.webkit.JavascriptInterface
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import android.view.WindowManager
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.core.view.ViewCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayInputStream
import java.net.URLDecoder

/**
 * Full-screen shell around the web UI. The UI is a web page rather than Android views because a
 * weak tablet GPU handles GPU-composited CSS transforms far more happily than it handles a
 * Compose recomposition storm, and because the same page can be developed in a desktop browser.
 * Everything is served from the virtual origin https://deck.local so the page gets a real secure
 * origin - file:// would block fetch, localStorage and module scripts.
 */
class MainActivity : AppCompatActivity() {

    private lateinit var web: WebView
    private lateinit var assetStore: AssetStore

    /** Sections the dashboard currently wants. Sent to the host so it collects nothing else. */
    private var sections = listOf("sys", "media", "audio", "ctx", "apps")
    private var refreshHz = 2.0

    private val mainHandler = Handler(Looper.getMainLooper())

    private val KeyEverConnected = "everConnected"

    /**
     * Chromium's own native heap only ever grows across a long-lived WebView session - it is not
     * a leak in this app's code, it is how the renderer's allocator behaves when nothing ever
     * calls loadUrl again. On a 918MB device a multi-day session was observed climbing past 300MB
     * of native heap (a third of all RAM), which is what made the whole tablet feel heavy, not
     * just this app.
     */
    private val RELOAD_INTERVAL_MS = 3 * 60 * 60 * 1000L
    private val periodicReload = object : Runnable {
        override fun run() {
            if (!isFinishing && !isDestroyed) web.reload()
            mainHandler.postDelayed(this, RELOAD_INTERVAL_MS)
        }
    }

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        assetStore = AssetStore(this)
        ContextCompat.startForegroundService(this, Intent(this, DeckService::class.java))
        requestNotificationPermission()

        // Draw behind the bars and into a display cutout, then publish the insets to the
        // page as CSS variables so the layout can keep clear of a notch on its own.
        WindowCompat.setDecorFitsSystemWindows(window, false)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            window.attributes.layoutInDisplayCutoutMode =
                WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
        }

        web = WebView(this)
        setContentView(web)
        goImmersive()
        observeInsets()

        // Remote debugging keeps a DevTools inspector agent alive inside the renderer for the
        // life of the process, which is pure overhead on a 1GB device. Only worth it while
        // actively debugging on a desktop build machine.
        WebView.setWebContentsDebuggingEnabled(BuildConfig.DEBUG)

        web.setBackgroundColor(0xFF05060A.toInt())
        web.overScrollMode = View.OVER_SCROLL_NEVER
        web.settings.apply {
            javaScriptEnabled = true
            domStorageEnabled = true
            loadWithOverviewMode = false
            useWideViewPort = false
            builtInZoomControls = false
            displayZoomControls = false
            setSupportZoom(false)
            cacheMode = WebSettings.LOAD_NO_CACHE
            mediaPlaybackRequiresUserGesture = false

            // Ignore the system font-size setting. WebView otherwise multiplies every
            // authored font-size *and* line-height by it, which inflates a control
            // surface that has no room to grow and makes every text metric fractional -
            // on a 1:1 density screen that leaves 1px borders straddling two physical
            // pixels and rendering as smeared, shifting lines.
            textZoom = 100
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) safeBrowsingEnabled = false
        }

        web.webViewClient = object : WebViewClient() {
            override fun onPageFinished(view: WebView, url: String?) = applyInsets()

            override fun shouldInterceptRequest(view: WebView, request: WebResourceRequest): WebResourceResponse? {
                val url = request.url
                if (url.host != "deck.local") return null
                val path = url.path ?: "/"
                return if (path.startsWith("/asset/")) serveCachedAsset(path.removePrefix("/asset/"))
                else servePackagedFile(if (path == "/" || path.isEmpty()) "index.html" else path.trimStart('/'))
            }
        }

        // Without this, console output from the page goes nowhere - it does not reach
        // logcat on its own. Forwarding it is the only practical way to diagnose
        // rendering behaviour on the tablet itself rather than in a desktop browser.
        web.webChromeClient = object : android.webkit.WebChromeClient() {
            override fun onConsoleMessage(m: android.webkit.ConsoleMessage): Boolean {
                val line = "${m.message()} (${m.sourceId()}:${m.lineNumber()})"
                when (m.messageLevel()) {
                    android.webkit.ConsoleMessage.MessageLevel.ERROR -> android.util.Log.e("DeckWeb", line)
                    android.webkit.ConsoleMessage.MessageLevel.WARNING -> android.util.Log.w("DeckWeb", line)
                    else -> android.util.Log.i("DeckWeb", line)
                }
                return true
            }
        }

        web.addJavascriptInterface(Bridge(), "DeckNative")
        web.loadUrl("https://deck.local/index.html")

        observeLink()

        mainHandler.postDelayed(periodicReload, RELOAD_INTERVAL_MS)
    }


    /** Artwork and icons already pushed by the host, keyed by content hash. */
    private fun serveCachedAsset(rawKey: String): WebResourceResponse {
        val key = try { URLDecoder.decode(rawKey, "UTF-8") } catch (_: Exception) { rawKey }
        val bytes = assetStore.read(key)
            ?: return WebResourceResponse("text/plain", "utf-8", 404, "Not Found", emptyMap(), null)
        return WebResourceResponse("image/*", null, 200, "OK",
            mapOf("Cache-Control" to "max-age=31536000, immutable"),
            ByteArrayInputStream(bytes))
    }

    /** The UI itself, packaged in the APK. */
    private fun servePackagedFile(rel: String): WebResourceResponse {
        return try {
            val stream = assets.open(rel)
            WebResourceResponse(mimeOf(rel), "utf-8", 200, "OK",
                mapOf("Cache-Control" to "no-cache"), stream)
        } catch (_: Exception) {
            WebResourceResponse("text/plain", "utf-8", 404, "Not Found", emptyMap(), null)
        }
    }

    private fun mimeOf(path: String): String = when {
        path.endsWith(".html") -> "text/html"
        path.endsWith(".css") -> "text/css"
        path.endsWith(".js") -> "application/javascript"
        path.endsWith(".json") -> "application/json"
        path.endsWith(".svg") -> "image/svg+xml"
        path.endsWith(".woff2") -> "font/woff2"
        path.endsWith(".png") -> "image/png"
        else -> "application/octet-stream"
    }


    inner class Bridge {
        /** UI -> host. */
        @JavascriptInterface
        fun send(json: String) = DeckBus.sendJson(json)

        /**
         * Screen brightness for this app's window only, 1 to 100, or anything below 1 to hand
         * control back to the system. Needs no permission, and is what lets the deck dim itself
         * at night without touching the tablet's own setting.
         */
        @JavascriptInterface
        fun setBrightness(percent: Int) {
            runOnUiThread {
                val lp = window.attributes
                lp.screenBrightness = if (percent < 1) WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE
                                      else percent.coerceIn(1, 100) / 100f
                window.attributes = lp
            }
        }

        /** Lets the UI declare what it is showing, which throttles the laptop's work. */
        @JavascriptInterface
        fun setSubscription(sectionsCsv: String, hz: Double) {
            sections = sectionsCsv.split(',').map { it.trim() }.filter { it.isNotEmpty() }
            refreshHz = hz
            pushSubscription()
        }

        /**
         * What the tablet still needs switched on before the cable can carry a link.
         *
         * Android lets any app read these two, with no permission, which is what makes a
         * live checklist possible: the page can tick each step as the user completes it
         * instead of asking them to read instructions and hope.
         */
        @JavascriptInterface
        fun setupState(): String = JSONObject().apply {
            put("devOptions", globalFlag(Settings.Global.DEVELOPMENT_SETTINGS_ENABLED))
            put("usbDebugging", globalFlag(Settings.Global.ADB_ENABLED))
            put("everConnected", prefs().getBoolean(KeyEverConnected, false))
            put("model", Build.MODEL)
            put("android", Build.VERSION.RELEASE)
        }.toString()

        /**
         * Opens the Settings page for one step. Each falls back to the top level of
         * Settings, because OEM builds move these around and a button that does nothing is
         * worse than one that lands a screen away.
         */
        @JavascriptInterface
        fun openSettings(which: String) {
            val primary = when (which) {
                "dev" -> Intent(Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS)
                "about" -> Intent(Settings.ACTION_DEVICE_INFO_SETTINGS)
                "tether" -> Intent().setClassName("com.android.settings",
                                                  "com.android.settings.TetherSettings")
                else -> Intent(Settings.ACTION_SETTINGS)
            }
            runOnUiThread {
                for (intent in listOf(primary, Intent(Settings.ACTION_SETTINGS))) {
                    try {
                        startActivity(intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                        return@runOnUiThread
                    } catch (_: Exception) { }
                }
            }
        }

        /** Replays the last payload of each section so a reload paints instantly. */
        @JavascriptInterface
        fun requestSnapshot() {
            val snap = DeckBus.snapshot()
            runOnUiThread { snap.forEach { deliver(it) } }
        }

        @JavascriptInterface
        fun linkState(): String = JSONObject().apply {
            val l = DeckBus.link.value
            put("phase", l.phase.name.lowercase())
            put("transport", l.transport)
            put("hostName", l.hostName)
        }.toString()
    }

    private fun globalFlag(name: String): Boolean =
        try { Settings.Global.getInt(contentResolver, name, 0) == 1 } catch (_: Exception) { false }

    private fun prefs(): SharedPreferences = getSharedPreferences("deck", MODE_PRIVATE)

    /**
     * Remembers that a link has succeeded at least once, so the setup guide leads on a
     * fresh install and gets out of the way afterwards. A later disconnection is then just
     * a disconnection, not a reason to re-teach someone what they already did.
     */
    private fun markConnected() {
        if (prefs().getBoolean(KeyEverConnected, false)) return
        prefs().edit().putBoolean(KeyEverConnected, true).apply()
    }

    /** Re-reads the checklist and hands it to the page. */
    private fun pushSetupState() {
        val json = Bridge().setupState()
        runOnUiThread {
            web.evaluateJavascript(
                "window.__deckSetup && window.__deckSetup(" + JSONObject.quote(json) + ")", null)
        }
    }

    private fun pushSubscription() {
        DeckBus.sendJson(JSONObject().apply {
            put("type", "sub")
            put("sections", JSONArray(sections))
            put("hz", refreshHz)
        }.toString())
    }


    private fun observeLink() {
        lifecycleScope.launch {
            DeckBus.incoming.collectLatest { deliver(it) }
        }
        lifecycleScope.launch {
            DeckBus.link.collectLatest { state ->
                val json = JSONObject().apply {
                    put("type", "link")
                    put("phase", state.phase.name.lowercase())
                    put("transport", state.transport)
                    put("hostName", state.hostName)
                    put("note", state.note)
                    put("paired", state.paired)
                }.toString()
                runOnUiThread { deliver(json) }
                if (state.phase == LinkPhase.Connected) { markConnected(); pushSubscription() }
                pushSetupState()
            }
        }
    }

    private fun deliver(json: String) {
        // Base64 rather than interpolating the JSON directly: it sidesteps every quoting and
        // newline hazard of building a JS string literal, and keeps payloads opaque to the
        // evaluator until the page decodes them.
        val b64 = android.util.Base64.encodeToString(json.toByteArray(Charsets.UTF_8), android.util.Base64.NO_WRAP)
        web.evaluateJavascript("window.__deckDeliver && window.__deckDeliver('$b64')", null)
    }


    override fun onResume() {
        super.onResume()
        goImmersive()
        pushSubscription()
        // The user may have just come back from Settings having changed one of these.
        pushSetupState()
    }

    override fun onPause() {
        super.onPause()
        // Tell the host to stop collecting the moment the dashboard is not on screen.
        DeckBus.sendJson(JSONObject().apply {
            put("type", "sub")
            put("sections", JSONArray(emptyList<String>()))
            put("hz", 0)
        }.toString())
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) goImmersive()
    }

    private fun goImmersive() {
        WindowInsetsControllerCompat(window, window.decorView).apply {
            systemBarsBehavior =
                WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            hide(WindowInsetsCompat.Type.systemBars())
        }
    }

    /**
     * Pushes the window cutout insets into the page as CSS variables.
     * WebView on Android has no env(safe-area-inset-*), so the shell has to hand them over.
     */
    private fun observeInsets() {
        ViewCompat.setOnApplyWindowInsetsListener(web) { _, insets ->
            // Cutout only, not the system bars. The bars are hidden by immersive mode, but
            // the window still reports them here at their full size - 48dp of navigation
            // bar on this tablet - and applying that as padding left a dead strip across
            // the bottom of a screen that is already only 600dp tall. Anything a hidden bar
            // does show over the page on a swipe is transient and meant to overlay it.
            val cut = insets.getInsets(WindowInsetsCompat.Type.displayCutout())
            val d = resources.displayMetrics.density
            fun dp(px: Int) = (px / d).toInt()
            lastInsets = intArrayOf(dp(cut.top), dp(cut.right), dp(cut.bottom), dp(cut.left))
            applyInsets()
            insets
        }
        ViewCompat.requestApplyInsets(web)
    }

    /** Last insets the window reported, in dp, as top, right, bottom, left. */
    private var lastInsets = intArrayOf(0, 0, 0, 0)

    /**
     * Writes the insets into the page. Called both when they change and when a page finishes
     * loading, because the first report arrives before any document exists: the page has no
     * root element yet, the write throws, and since insets are only reported again when they
     * change the variables would then stay at zero for the life of the process. Guarded for
     * the same reason.
     */
    private fun applyInsets() {
        val (t, r, b, l) = lastInsets
        web.evaluateJavascript(
            "(function(){var e=document.documentElement;if(!e)return;" +
            "e.style.setProperty('--inset-top','${t}px');" +
            "e.style.setProperty('--inset-right','${r}px');" +
            "e.style.setProperty('--inset-bottom','${b}px');" +
            "e.style.setProperty('--inset-left','${l}px');})()",
            null)
    }

    /**
     * Android 13+ hides the service's ongoing notification without this. The link does not
     * depend on it, so the result is never checked and never blocks anything.
     */
    private fun requestNotificationPermission() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) return
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS)
            == PackageManager.PERMISSION_GRANTED) return
        try {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        } catch (_: Exception) {}
    }

    override fun onDestroy() {
        mainHandler.removeCallbacks(periodicReload)
        try { web.destroy() } catch (_: Exception) {}
        super.onDestroy()
    }

    /**
     * The system calls this when it wants memory back, which on a 918MB device happens
     * routinely. Dropping the WebView's own caches here is cheap - everything that actually
     * matters (artwork, the last payload per section) lives outside the WebView, in
     * AssetStore and DeckBus, so nothing user-visible is lost.
     */
    @Suppress("DEPRECATION")
    override fun onTrimMemory(level: Int) {
        super.onTrimMemory(level)
        // The RUNNING_* levels are deprecated from API 34, where the system no longer
        // sends them. They are kept because this still has to run on the old, small-RAM
        // tablets where they are both delivered and the whole point.
        if (level >= ComponentCallbacks2.TRIM_MEMORY_RUNNING_LOW) {
            try { web.clearCache(false) } catch (_: Exception) {}
        }
    }
}
