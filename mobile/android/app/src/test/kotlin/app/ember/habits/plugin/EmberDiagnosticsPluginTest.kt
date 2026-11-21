package app.ember.habits.plugin

import android.app.ActivityManager
import android.app.ApplicationExitInfo
import android.content.ComponentName
import android.content.Intent
import android.content.pm.PackageInfo
import android.os.Looper
import app.ember.habits.MainActivity
import app.ember.habits.NoServiceWorkers
import app.ember.habits.diagnostics.CrashRecorder
import app.ember.habits.diagnostics.LaunchEventsTest
import app.ember.habits.diagnostics.RecordFile
import com.getcapacitor.JSObject
import com.getcapacitor.PluginCall
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import org.robolectric.shadows.ShadowActivityManager.ApplicationExitInfoBuilder
import org.robolectric.shadows.ShadowWebView
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/**
 * `EmberDiagnostics.collect()` through the real bridge: it resolves the spec's `DeviceDiagnostics` with the launch
 * just recorded and the app's recent exits, the crash matched to Ember's own record; below API 30 `exits` is empty.
 */
@RunWith(RobolectricTestRunner::class)
@Config(shadows = [NoServiceWorkers::class])
class EmberDiagnosticsPluginTest {

    /** A plugin call that waits for the worker thread to resolve or reject it. */
    private class Call : PluginCall(null, "EmberDiagnostics", "test", "collect", JSObject()) {
        val done = CountDownLatch(1)
        var resolved: JSObject? = null
        var rejected: String? = null

        override fun resolve(data: JSObject?) {
            resolved = data
            done.countDown()
        }

        override fun reject(msg: String?, code: String?, ex: Exception?, data: JSObject?) {
            rejected = msg
            done.countDown()
        }
    }

    private val context get() = RuntimeEnvironment.getApplication()

    private fun collect(): JSONObject {
        ShadowWebView.setCurrentWebViewPackage(
            PackageInfo().apply {
                packageName = "com.google.android.webview"
                versionName = "140.0.0.0"
            },
        )
        shadowOf(context).declareActionUnbindable("android.support.customtabs.action.CustomTabsService")
        val launcher = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
            .setComponent(ComponentName(context, MainActivity::class.java))
        val activity = Robolectric.buildActivity(MainActivity::class.java, launcher).create().get()
        val plugin = activity.bridge.getPlugin("EmberDiagnostics").instance as EmberDiagnosticsPlugin
        val call = Call()
        plugin.collect(call)
        assertTrue(call.done.await(10, TimeUnit.SECONDS))
        shadowOf(Looper.getMainLooper()).idle()
        assertEquals(null, call.rejected)
        return JSONObject(call.resolved!!.toString())
    }

    private fun exit(at: Long, reason: Int, status: Int = 0): ApplicationExitInfo =
        ApplicationExitInfoBuilder.newBuilder()
            .setProcessName(context.packageName)
            .setTimestamp(at)
            .setReason(reason)
            .setStatus(status)
            .setDescription("secret description user@example.com")
            .build()

    @Test
    @Config(sdk = [36])
    fun collectResolvesTheDiagnosticsWithLaunchesAndMatchedExits() {
        val now = System.currentTimeMillis()
        val crash = IllegalStateException("secret message")
        RecordFile.crashes(context).append(CrashRecorder.record(crash, now - 3_000), now)
        val manager = shadowOf(context.getSystemService(ActivityManager::class.java))
        manager.addApplicationExitInfo(exit(now - 2_000, ApplicationExitInfo.REASON_CRASH))
        manager.addApplicationExitInfo(exit(now - 60_000, ApplicationExitInfo.REASON_SIGNALED, 9))

        val result = collect()

        assertEquals(setOf("installation", "version", "os", "device", "launches", "exits"), result.keys().asSequence().toSet())
        assertTrue(Regex("^[A-Za-z0-9_-]{22}$").matches(result.getString("installation")))
        assertTrue(result.getString("os"), Regex("""^Android [A-Za-z0-9._]{1,20} \(API 36\)$""").matches(result.getString("os")))
        val launches = result.getJSONArray("launches")
        val events = (0 until launches.length()).map { launches.getJSONObject(it).getString("event") }
        val launch = Regex("^launch cold=(true|false) saved=false source=user history=false webview=140.0.0.0$")
        assertTrue(events.toString(), events.any(launch::matches))
        events.forEach(LaunchEventsTest::assertInVocabulary)
        val exits = result.getJSONArray("exits")
        assertEquals(2, exits.length())
        val first = exits.getJSONObject(0)
        assertEquals("CRASH", first.getString("reason"))
        assertEquals(JSONArray(listOf("java.lang.IllegalStateException")).toString(), first.getJSONArray("exception").toString())
        assertTrue(first.isNull("signal"))
        assertEquals("SIGNALED", exits.getJSONObject(1).getString("reason"))
        assertEquals("SIGKILL", exits.getJSONObject(1).getString("signal"))
        assertTrue(result.toString(), "secret" !in result.toString() && "example.com" !in result.toString())
        assertEquals(result.getString("installation"), collect().getString("installation"))
    }

    @Test
    @Config(sdk = [29])
    fun belowApi30ThereAreNoExits() {
        val result = collect()

        assertEquals(0, result.getJSONArray("exits").length())
        assertNotNull(result.getJSONArray("launches"))
        assertEquals("Android 10 (API 29)", result.getString("os"))
    }
}
