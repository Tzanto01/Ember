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
import androidx.webkit.WebViewCompat
import app.ember.habits.diagnostics.CrashRecorder
import app.ember.habits.diagnostics.IgnoredRoute
import app.ember.habits.diagnostics.LaunchEvents
import app.ember.habits.diagnostics.LaunchSource
import app.ember.habits.diagnostics.RecordFile
import app.ember.habits.diagnostics.RendererThen
import app.ember.habits.plugin.BackupFilePlugin
import app.ember.habits.plugin.EmberDiagnosticsPlugin
import app.ember.habits.plugin.EmberWidgetPlugin
import app.ember.habits.plugin.FlavorPlugins
import com.getcapacitor.BridgeActivity
import com.getcapacitor.WebViewListener
import org.json.JSONObject
import java.io.IOException

/** The Capacitor activity hosting the web app, with Ember's plugins and the moment and widget launches routed to it. */
class MainActivity : BridgeActivity() {

    private var restoring = false
    private var starting = false
    private var createdAt = 0L
    private var pageLoaded = false
    private val launches by lazy { RecordFile.launches(applicationContext) }

    override fun onCreate(savedInstanceState: Bundle?) {
        CrashRecorder.install(this)
        createdAt = SystemClock.elapsedRealtime()
        val cold = !processStarted
        Log.i(LOG_TAG, describe("launch", intent, cold = cold, saved = savedInstanceState != null))
        record(
            LaunchEvents.launch(
                cold = cold,
                saved = savedInstanceState != null,
                source = LaunchSource.of(intent),
                history = fromHistory(intent),
                webview = webViewVersion(),
            ),
        )
        processStarted = true
        registerPlugin(EmberWidgetPlugin::class.java)
        registerPlugin(BackupFilePlugin::class.java)
        registerPlugin(EmberDiagnosticsPlugin::class.java)
        registerPlugins(FlavorPlugins.all())
        bridgeBuilder.addWebViewListener(
            object : WebViewListener() {
                override fun onPageLoaded(webView: WebView) {
                    if (pageLoaded) return
                    pageLoaded = true
                    record(LaunchEvents.pageLoaded(SystemClock.elapsedRealtime() - createdAt))
                }

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
        val then = when {
            isFinishing -> RendererThen.NONE
            again -> RendererThen.FINISH
            else -> RendererThen.RECREATE
        }
        Log.i(LOG_TAG, "renderer gone crashed=$crashed priorityAtExit=$priority then=${then.code}")
        record(LaunchEvents.rendererGone(crashed, priority, then))
        (webView.parent as? ViewGroup)?.removeView(webView)
        if (isFinishing) return true
        if (again) finish() else ActivityCompat.recreate(this)
        return true
    }

    override fun onNewIntent(intent: Intent) {
        if (!starting) {
            Log.i(LOG_TAG, describe("newIntent", intent, cold = false, saved = false))
            record(LaunchEvents.newIntent(LaunchSource.of(intent)))
        }
        super.onNewIntent(intent)
        route(intent, starting)
    }

    override fun onDestroy() {
        Log.i(LOG_TAG, "destroy finishing=$isFinishing changingConfigurations=$isChangingConfigurations")
        record(LaunchEvents.destroy(finishing = isFinishing, configChange = isChangingConfigurations))
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
        if (restoring) return ignored(IgnoredRoute.RESTORING)
        if (bridge == null) return ignored(IgnoredRoute.NO_BRIDGE)
        if (fromHistory(intent)) return ignored(IgnoredRoute.FROM_HISTORY)
        val plugin = bridge.getPlugin("EmberWidget")?.instance as? EmberWidgetPlugin
            ?: return ignored(IgnoredRoute.NO_WIDGET_PLUGIN)
        if (intent.action == ACTION_OPEN_MOMENT) {
            val component = intent.component ?: return ignored(IgnoredRoute.MOMENT_WITHOUT_COMPONENT)
            if (component.packageName != packageName) return ignored(IgnoredRoute.MOMENT_OTHER_PACKAGE)
            if (MainActivity::class.java.name != component.className) return ignored(IgnoredRoute.MOMENT_OTHER_ACTIVITY)
            if (cold) plugin.pendMoment() else plugin.openMoment()
            return
        }
        val tap = TodayWidget.validTap(this, intent) ?: return ignored(IgnoredRoute.NO_VALID_TAP)
        plugin.tap(tap)
    }

    private fun ignored(reason: IgnoredRoute) {
        Log.i(LOG_TAG, "route ignored reason=${reason.log}")
        record(LaunchEvents.routeIgnored(reason))
    }

    /** Adds [event] to the on-device launch log for "Send diagnostics"; a failed write never disturbs the launch. */
    private fun record(event: String) {
        val now = System.currentTimeMillis()
        try {
            launches.append(JSONObject().put(RecordFile.AT, now).put(RecordFile.EVENT, event), now)
        } catch (e: IOException) {
        } catch (e: RuntimeException) {
        }
    }

    /** The WebView provider's version name, or null when it is unknown or unreadable. */
    private fun webViewVersion(): String? =
        try {
            WebViewCompat.getCurrentWebViewPackage(this)?.versionName
        } catch (e: RuntimeException) {
            null
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
