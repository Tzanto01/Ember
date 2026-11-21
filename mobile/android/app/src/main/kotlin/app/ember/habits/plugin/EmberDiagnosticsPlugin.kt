package app.ember.habits.plugin

import android.content.Context
import android.os.Build
import app.ember.habits.diagnostics.DeviceFacts
import app.ember.habits.diagnostics.DiagnosticsPayload
import app.ember.habits.diagnostics.ExitReasons
import app.ember.habits.diagnostics.Installation
import app.ember.habits.diagnostics.Launch
import app.ember.habits.diagnostics.RecordFile
import com.getcapacitor.JSObject
import com.getcapacitor.Plugin
import com.getcapacitor.PluginCall
import com.getcapacitor.PluginMethod
import com.getcapacitor.annotation.CapacitorPlugin
import org.json.JSONException
import kotlin.concurrent.thread

/**
 * "Send diagnostics": `collect` resolves Ember's `DeviceDiagnostics` (installation id, version, OS, device, the last 7
 * days of launch events and process exits; see [DiagnosticsPayload]) for the web app to preview and upload. It reads
 * on a worker thread, and a part it can't read is left out instead of failing the call.
 */
@CapacitorPlugin(name = "EmberDiagnostics")
class EmberDiagnosticsPlugin : Plugin() {

    @PluginMethod
    fun collect(call: PluginCall) {
        val context = context
        thread(name = "EmberDiagnostics") {
            try {
                call.resolve(JSObject(collect(context, System.currentTimeMillis())))
            } catch (e: JSONException) {
                call.reject("Could not collect diagnostics")
            } catch (e: RuntimeException) {
                call.reject("Could not collect diagnostics")
            }
        }
    }

    internal companion object {
        /** The `DeviceDiagnostics` JSON as of [now]. */
        fun collect(context: Context, now: Long): String {
            val launches = orEmpty {
                RecordFile.launches(context).read(now).mapNotNull { record ->
                    record.optString(RecordFile.EVENT).takeIf { it.isNotEmpty() }?.let { Launch(record.optLong(RecordFile.AT), it) }
                }
            }
            val exits = orEmpty { ExitReasons.collect(context, RecordFile.crashes(context).read(now), now) }
            return DiagnosticsPayload.build(Installation.id(context), facts(context), launches, exits, now).toJson()
        }

        private fun facts(context: Context) = DeviceFacts(
            versionName = safely { context.packageManager.getPackageInfo(context.packageName, 0).versionName },
            release = safely { Build.VERSION.RELEASE },
            sdk = Build.VERSION.SDK_INT,
            manufacturer = safely { Build.MANUFACTURER },
            model = safely { Build.MODEL },
        )

        private fun <T> orEmpty(read: () -> List<T>): List<T> = safely(read).orEmpty()

        private fun <T> safely(read: () -> T?): T? =
            try {
                read()
            } catch (e: Exception) {
                null
            }
    }
}
