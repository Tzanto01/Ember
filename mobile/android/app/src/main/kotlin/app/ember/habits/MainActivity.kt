package app.ember.habits

import android.content.Intent
import android.os.Bundle
import android.os.SystemClock
import android.view.ViewGroup
import android.webkit.RenderProcessGoneDetail
import android.webkit.WebView
import androidx.activity.OnBackPressedCallback
import androidx.annotation.VisibleForTesting
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
        registerPlugin(EmberWidgetPlugin::class.java)
        registerPlugin(BackupFilePlugin::class.java)
        registerPlugins(FlavorPlugins.all())
        bridgeBuilder.addWebViewListener(
            object : WebViewListener() {
                override fun onRenderProcessGone(webView: WebView, detail: RenderProcessGoneDetail?) = recover(webView)
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
     * blank. A second death within [RENDERER_RETRY_WINDOW_MS] of the last one finishes the activity instead of looping.
     */
    private fun recover(webView: WebView): Boolean {
        val now = SystemClock.elapsedRealtime()
        val again = rendererGoneAt?.let { now - it < RENDERER_RETRY_WINDOW_MS } == true
        rendererGoneAt = now
        (webView.parent as? ViewGroup)?.removeView(webView)
        if (again) finish() else recreate()
        return true
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        route(intent, starting)
    }

    /**
     * Hands a moment door or a validated widget tap to the EmberWidget plugin. Capacitor also routes the launch intent
     * here during `super.onCreate`; there a moment becomes the pending launch action, later an `openMoment` event.
     * Intents replayed from recents or a restored activity, moments not aimed at this activity, and anything
     * [TodayWidget.validTap] rejects, are ignored.
     */
    private fun route(intent: Intent, cold: Boolean) {
        val bridge = bridge
        if (restoring || bridge == null) return
        if ((intent.flags and Intent.FLAG_ACTIVITY_LAUNCHED_FROM_HISTORY) != 0) return
        val plugin = bridge.getPlugin("EmberWidget")?.instance as? EmberWidgetPlugin ?: return
        if (intent.action == ACTION_OPEN_MOMENT) {
            val component = intent.component ?: return
            if (component.packageName != packageName) return
            if (MainActivity::class.java.name != component.className) return
            if (cold) plugin.pendMoment() else plugin.openMoment()
            return
        }
        TodayWidget.validTap(this, intent)?.let(plugin::tap)
    }

    companion object {
        internal const val ACTION_OPEN_MOMENT = "app.ember.habits.OPEN_MOMENT"
        internal const val RENDERER_RETRY_WINDOW_MS = 30_000L

        /** When this process last saw Ember's renderer die, on the [SystemClock.elapsedRealtime] clock. */
        @VisibleForTesting
        internal var rendererGoneAt: Long? = null
    }
}
