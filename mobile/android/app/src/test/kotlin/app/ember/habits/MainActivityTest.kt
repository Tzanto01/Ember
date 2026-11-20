package app.ember.habits

import android.content.ComponentName
import android.content.Intent
import android.content.pm.PackageInfo
import android.os.Bundle
import android.os.Looper
import android.webkit.RenderProcessGoneDetail
import android.webkit.ServiceWorkerClient
import android.webkit.ServiceWorkerController
import android.webkit.ServiceWorkerWebSettings
import app.ember.habits.plugin.EmberWidgetPlugin
import app.ember.habits.widget.WidgetStore
import com.getcapacitor.JSObject
import com.getcapacitor.PluginCall
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotSame
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows.shadowOf
import org.robolectric.android.controller.ActivityController
import org.robolectric.annotation.Config
import org.robolectric.annotation.Implementation
import org.robolectric.annotation.Implements
import org.robolectric.shadows.ShadowLog
import org.robolectric.shadows.ShadowSystemClock
import org.robolectric.shadows.ShadowWebView
import java.time.Duration

/**
 * How launches reach the web app: a moment door on a cold start is the pending launch action, on a warm one an
 * `openMoment` event; a validated widget tap is a `widgetTap` event; replayed, restored and foreign launches do
 * nothing; a dead WebView renderer recreates the activity once and then gives up; the launch log names extras but
 * never their values. Runs the real Capacitor bridge, so the web app's side is a listener on the EmberWidget plugin.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [36], shadows = [NoServiceWorkers::class])
class MainActivityTest {

    /** A plugin call that records what the plugin resolves it with. */
    private class Call(data: JSObject = JSObject()) : PluginCall(null, "EmberWidget", "test", "test", data) {
        val resolved = ArrayList<JSObject?>()
        override fun resolve(data: JSObject?) {
            resolved.add(data)
        }
        override fun resolve() {
            resolved.add(null)
        }
    }

    private val context get() = RuntimeEnvironment.getApplication()

    @Before
    fun setUp() {
        shadowOf(context).declareActionUnbindable("android.support.customtabs.action.CustomTabsService")
        MainActivity.rendererGoneAt = null
        ShadowWebView.setCurrentWebViewPackage(
            PackageInfo().apply {
                packageName = "com.google.android.webview"
                versionName = "140.0.0.0"
            },
        )
    }

    private fun start(intent: Intent, saved: Bundle? = null): ActivityController<MainActivity> =
        Robolectric.buildActivity(MainActivity::class.java, intent).create(saved)

    private fun ActivityController<MainActivity>.plugin() =
        get().bridge.getPlugin("EmberWidget").instance as EmberWidgetPlugin

    private fun ActivityController<MainActivity>.launchAction(): Any? {
        val call = Call()
        plugin().takeLaunchAction(call)
        return call.resolved.single()!!.get("action").takeUnless { it == JSONObject.NULL }
    }

    /** What the web app hears on [event] once it listens, retained events included. */
    private fun ActivityController<MainActivity>.listen(event: String): Call {
        val call = Call(JSObject().put("eventName", event))
        plugin().addListener(call)
        return call
    }

    /**
     * The WebView's render process dies, as Android reports it to the bridge's client, and the main thread then runs
     * what that scheduled; true when the death was handled.
     */
    private fun ActivityController<MainActivity>.rendererGone(): Boolean {
        val bridge = get().bridge
        val handled = bridge.webViewClient.onRenderProcessGone(
            bridge.webView,
            object : RenderProcessGoneDetail() {
                override fun didCrash() = true
                override fun rendererPriorityAtExit() = 0
            },
        )
        shadowOf(Looper.getMainLooper()).idle()
        return handled
    }

    private fun launcher() = Intent(Intent.ACTION_MAIN).setComponent(ComponentName(context, MainActivity::class.java))

    private fun moment(component: ComponentName? = ComponentName(context, MainActivity::class.java)) =
        Intent(MainActivity.ACTION_OPEN_MOMENT).setComponent(component)

    private fun today(): String {
        val host = TodayHost()
        host.today(host.habit(2, "Walk"))
        return host.token
    }

    private fun tap(token: String) = Intent(TodayWidget.ACTION_CHECK_IN)
        .setComponent(ComponentName(context, MainActivity::class.java))
        .putExtra("habitId", 2L)
        .putExtra("date", WidgetStore.current(context)!!.getString("date"))
        .putExtra("userId", "user-1")
        .putExtra("token", token)

    @Test
    fun aMomentDoorOnAColdStartIsTheLaunchActionOnce() {
        val app = start(moment())

        assertEquals("moment", app.launchAction())
        assertNull(app.launchAction())
        assertEquals(emptyList<JSObject?>(), app.listen("openMoment").resolved)
    }

    @Test
    fun aMomentDoorWhileRunningOpensTheMoment() {
        val app = start(launcher())
        val opened = app.listen("openMoment")

        app.newIntent(moment())

        assertEquals(1, opened.resolved.size)
        assertNull(app.launchAction())
    }

    @Test
    fun launchesReplayedFromRecentsOrARestoredActivityAreIgnored() {
        val fromHistory = moment().addFlags(Intent.FLAG_ACTIVITY_LAUNCHED_FROM_HISTORY)
        assertNull(start(fromHistory).launchAction())
        assertNull(start(moment(), Bundle()).launchAction())

        val app = start(launcher())
        val opened = app.listen("openMoment")
        app.newIntent(moment().addFlags(Intent.FLAG_ACTIVITY_LAUNCHED_FROM_HISTORY))
        assertEquals(0, opened.resolved.size)
    }

    @Test
    fun aMomentDoorMustNameThisActivity() {
        val app = start(launcher())
        val opened = app.listen("openMoment")

        app.newIntent(moment(null))
        app.newIntent(moment(ComponentName("com.example.other", MainActivity::class.java.name)))
        app.newIntent(moment(ComponentName(context, TodayWidget::class.java)))

        assertEquals(0, opened.resolved.size)
    }

    @Test
    fun onlyAValidatedWidgetTapReachesTheAppEvenOnAColdStart() {
        val token = today()

        val cold = start(tap(token))
        val coldTaps = cold.listen("widgetTap").resolved
        assertEquals(1, coldTaps.size)
        assertEquals(2L, coldTaps[0]!!.getLong("habitId"))
        assertEquals("user-1", coldTaps[0]!!.getString("userId"))

        val warm = start(launcher())
        val taps = warm.listen("widgetTap")
        warm.newIntent(tap("0".repeat(32)))
        assertEquals(0, taps.resolved.size)
        warm.newIntent(tap(token))
        assertEquals(1, taps.resolved.size)
    }

    @Test
    fun aDeadRendererRecreatesTheActivityWithoutReplayingTheLaunch() {
        val app = start(moment())
        val first = app.get()
        val deadWebView = first.bridge.webView
        assertEquals("moment", app.launchAction())

        assertTrue(app.rendererGone())

        assertNull(deadWebView.parent)
        assertNotSame(first, app.get())
        assertTrue(first.isDestroyed)
        assertFalse(app.get().isFinishing)
        assertNull(app.launchAction())
    }

    @Test
    fun aSecondRendererDeathWithinTheWindowFinishesInsteadOfLooping() {
        val app = start(launcher())
        assertTrue(app.rendererGone())
        val recreated = app.get()

        ShadowSystemClock.advanceBy(Duration.ofMillis(MainActivity.RENDERER_RETRY_WINDOW_MS / 2))
        assertTrue(app.rendererGone())

        assertTrue(recreated.isFinishing)
        assertSame(recreated, app.get())
    }

    @Test
    fun aRendererDeathAfterTheWindowRecreatesAgain() {
        val app = start(launcher())
        assertTrue(app.rendererGone())
        val recreated = app.get()

        ShadowSystemClock.advanceBy(Duration.ofMillis(MainActivity.RENDERER_RETRY_WINDOW_MS))
        assertTrue(app.rendererGone())

        assertNotSame(recreated, app.get())
        assertFalse(app.get().isFinishing)
    }

    @Test
    fun theLaunchLogNamesExtrasButNeverTheirValues() {
        val token = today()
        val date = WidgetStore.current(context)!!.getString("date")
        ShadowLog.clear()

        val app = start(tap(token))
        app.newIntent(tap(token).addFlags(Intent.FLAG_ACTIVITY_LAUNCHED_FROM_HISTORY))
        app.newIntent(launcher())

        val lines = ShadowLog.getLogsForTag(MainActivity.LOG_TAG).map { it.msg }
        val launch = lines.singleOrNull { it.startsWith("launch ") }
        assertNotNull(lines.toString(), launch)
        assertTrue(launch, launch!!.contains("component=app.ember.habits.MainActivity"))
        assertTrue(launch, launch.contains("extras=[date, habitId, token, userId]"))
        assertTrue(launch, launch.contains("saved=false history=false"))
        assertEquals(2, lines.count { it.startsWith("newIntent ") })
        assertTrue(lines.toString(), "route ignored reason=from history" in lines)
        assertTrue(lines.toString(), "route ignored reason=no moment or valid widget tap" in lines)
        for (value in listOf(token, "user-1", date)) {
            assertTrue("$value leaked into $lines", lines.none { value in it })
        }
    }
}

/** Robolectric has no service worker controller; Capacitor only registers a client with it. */
@Implements(ServiceWorkerController::class)
class NoServiceWorkers {
    companion object {
        @JvmStatic
        @Implementation
        @Suppress("DEPRECATION")
        fun getInstance(): ServiceWorkerController = object : ServiceWorkerController() {
            override fun getServiceWorkerWebSettings(): ServiceWorkerWebSettings = throw UnsupportedOperationException()
            override fun setServiceWorkerClient(client: ServiceWorkerClient?) = Unit
        }
    }
}
