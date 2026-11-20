package app.ember.habits

import android.app.Activity
import android.content.Intent
import android.net.Uri
import androidx.activity.result.ActivityResult
import com.getcapacitor.JSObject
import com.getcapacitor.Plugin
import com.getcapacitor.PluginCall
import com.getcapacitor.PluginMethod
import com.getcapacitor.annotation.ActivityCallback
import com.getcapacitor.annotation.CapacitorPlugin
import java.io.IOException
import kotlin.concurrent.thread

/**
 * Saves an export where the user can find it: `save` opens the system "Save as" picker
 * (Storage Access Framework, so no storage permission) and writes `text` as UTF-8 to the chosen document
 * on a worker thread. Resolves `{ saved: false }` when the user cancels.
 */
@CapacitorPlugin(name = "BackupFile")
class BackupFilePlugin : Plugin() {

    @PluginMethod
    fun save(call: PluginCall) {
        val fileName = call.getString("fileName")
        val mimeType = call.getString("mimeType")
        val text = call.getString("text")
        if (fileName.isNullOrBlank() || fileName.contains("/") || fileName.contains("\\")) {
            call.reject("Invalid file name")
            return
        }
        if (mimeType == null || mimeType !in MIME_TYPES) {
            call.reject("Unsupported file type")
            return
        }
        if (text == null) {
            call.reject("Text is required")
            return
        }
        val intent = Intent(Intent.ACTION_CREATE_DOCUMENT)
            .addCategory(Intent.CATEGORY_OPENABLE)
            .setType(mimeType)
            .putExtra(Intent.EXTRA_TITLE, fileName)
        startActivityForResult(call, intent, "created")
    }

    @ActivityCallback
    private fun created(call: PluginCall?, result: ActivityResult) {
        if (call == null) return
        bridge.releaseCall(call)
        val uri = result.data?.data
        if (result.resultCode != Activity.RESULT_OK || uri == null) {
            call.resolve(saved(false))
            return
        }
        val text = call.getString("text", "").orEmpty()
        thread(name = "BackupFile") {
            if (write(uri, text)) call.resolve(saved(true)) else call.reject("Could not write the file")
        }
    }

    private fun write(uri: Uri, text: String): Boolean =
        try {
            val out = context.contentResolver.openOutputStream(uri, "wt") ?: throw IOException("No output stream")
            out.use { it.write(text.toByteArray(Charsets.UTF_8)) }
            true
        } catch (e: IOException) {
            false
        } catch (e: RuntimeException) {
            false
        }

    private companion object {
        val MIME_TYPES = setOf("application/json", "text/csv", "text/plain")

        fun saved(saved: Boolean) = JSObject().apply { put("saved", saved) }
    }
}
