package app.ember.habits

import android.content.Intent
import android.os.Build
import android.os.Bundle
import android.os.SystemClock
import android.util.Log
import android.view.ViewGroup
import android.webkit.RenderProcessGoneDetail
import android.webkit.WebView
import androidx.activity.OnBackPressedCallback
import androidx.annotation.VisibleForTesting
import androidx.core.app.ActivityCompat
import app.ember.habits.plugin.BackupFilePlugin
import app.ember.habits.plugin.EmberWidgetPlugin
import app.ember.habits.plugin.FlavorPlugins
import com.getcapacitor.BridgeActivity
import com.getcapacitor.WebViewListener

/** The Capacitor activity hosting the web app, with Ember's plugins and the moment and widget launches routed to it. */
class MainActivity : BridgeActivity() {

    private var restoring = false
    private var starting = false

    override fun onCreate(savedInstanceState: Bundle?) {
        Log.i(LOG_TAG, describe("launch", intent, cold = !processStarted, saved = savedInstanceState != null))
        processStarted = true
        registerPlugin(EmberWidgetPlugin::class.java)
        registerPlugin(BackupFilePlugin::class.java)
        registerPlugins(FlavorPlugins.all())
        bridgeBuilder.addWebViewListener(
            object : WebViewListener() {
                override fun onRenderProcessGone(webView: WebView, detail: RenderProcessGoneDetail?) =
                    recover(webView, detail)
            },
        )
        restoring = savedInstanceState != null
        starting = true
        super.onCreate(savedInstanceState)
        starting = false
        restoring = false
        onBackPressedDispatcher.addCallback(
            this,
            object : OnBackPressedCallback(true) {
                /** Goes back in the web history, or at the app's root moves Ember to the background. */
                override fun handleOnBackPressed() {
                    val bridge = bridge
                    if (bridge != null && bridge.webView.canGoBack()) bridge.webView.goBack() else moveTaskToBack(true)
                }
            },
        )
    }

    /**
     * Replaces a WebView whose render process died or was killed by recreating the activity, so Ember never stays
     * blank. A second death within [RENDERER_RETRY_WINDOW_MS] of the last one finishes the activity instead of looping,
     * and an activity that is already finishing is left to finish.
     */
    private fun recover(webView: WebView, detail: RenderProcessGoneDetail?): Boolean {
        val now = SystemClock.elapsedRealtime()
        val again = rendererGoneAt?.let { now - it < RENDERER_RETRY_WINDOW_MS } == true
        rendererGoneAt = now
        val crashed = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) detail?.didCrash() else null
        val priority = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) detail?.rendererPriorityAtExit() else null
        Log.i(
            LOG_TAG,
            "renderer gone crashed=$crashed priorityAtExit=$priority then=" +
                when {
                    isFinishing -> "none"
                    again -> "finish"
                    else -> "recreate"
                },
        )
        (webView.parent as? ViewGroup)?.removeView(webView)
        if (isFinishing) return true
        if (again) finish() else ActivityCompat.recreate(this)
        return true
    }

    override fun onNewIntent(intent: Intent) {
        if (!starting) Log.i(LOG_TAG, describe("newIntent", intent, cold = false, saved = false))
        super.onNewIntent(intent)
        route(intent, starting)
    }

    override fun onDestroy() {
        Log.i(LOG_TAG, "destroy finishing=$isFinishing changingConfigurations=$isChangingConfigurations")
        super.onDestroy()
    }

    /**
     * Hands a moment door or a validated widget tap to the EmberWidget plugin. Capacitor also routes the launch intent
     * here during `super.onCreate`; there a moment becomes the pending launch action, later an `openMoment` event.
     * Intents replayed from recents or a restored activity, moments not aimed at this activity, and anything
     * [TodayWidget.validTap] rejects, are ignored.
     */
    private fun route(intent: Intent, cold: Boolean) {
        val bridge = bridge
        if (restoring) return ignored("restoring")
        if (bridge == null) return ignored("no bridge")
        if (fromHistory(intent)) return ignored("from history")
        val plugin = bridge.getPlugin("EmberWidget")?.instance as? EmberWidgetPlugin ?: return ignored("no widget plugin")
        if (intent.action == ACTION_OPEN_MOMENT) {
            val component = intent.component ?: return ignored("moment without component")
            if (component.packageName != packageName) return ignored("moment for another package")
            if (MainActivity::class.java.name != component.className) return ignored("moment for another activity")
            if (cold) plugin.pendMoment() else plugin.openMoment()
            return
        }
        val tap = TodayWidget.validTap(this, intent) ?: return ignored("no moment or valid widget tap")
        plugin.tap(tap)
    }

    private fun ignored(reason: String) {
        Log.i(LOG_TAG, "route ignored reason=$reason")
    }

    companion object {
        internal const val ACTION_OPEN_MOMENT = "app.ember.habits.OPEN_MOMENT"
        internal const val RENDERER_RETRY_WINDOW_MS = 30_000L
        internal const val LOG_TAG = "EmberLaunch"
        private const val MAX_LOGGED_LENGTH = 120

        private var processStarted = false

        /** When this process last saw Ember's renderer die, on the [SystemClock.elapsedRealtime] clock. */
        @VisibleForTesting
        internal var rendererGoneAt: Long? = null

        private fun fromHistory(intent: Intent) = (intent.flags and Intent.FLAG_ACTIVITY_LAUNCHED_FROM_HISTORY) != 0

        /**
         * One diagnostic line about [intent]: its action, categories, flags, component class, whether it carries data,
         * and only the names of its extras, never a value, a URI or anything else the sender put in it. The strings a
         * sender chooses are cut short and lose control characters, so they cannot forge further log lines.
         */
        private fun describe(event: String, intent: Intent, cold: Boolean, saved: Boolean): String {
            val flags = "0x%08x".format(intent.flags)
            val categories = intent.categories?.map(::plain)?.sorted().orEmpty()
            val extras = runCatching { intent.extras?.keySet()?.map(::plain)?.sorted().orEmpty().toString() }
                .getOrDefault("unreadable")
            return "$event action=${intent.action?.let(::plain)} categories=$categories flags=$flags " +
                "component=${intent.component?.className?.let(::plain)} data=${intent.data != null} extras=$extras " +
                "cold=$cold saved=$saved history=${fromHistory(intent)}"
        }

        private fun plain(text: String) =
            text.take(MAX_LOGGED_LENGTH).map { if (it.isISOControl()) '?' else it }.joinToString("")
    }
}
