package app.ember.habits

import android.content.Intent
import android.os.Build
import android.provider.Settings
import androidx.core.content.FileProvider
import androidx.core.net.toUri
import com.getcapacitor.JSObject
import com.getcapacitor.Plugin
import com.getcapacitor.PluginCall
import com.getcapacitor.PluginMethod
import com.getcapacitor.annotation.CapacitorPlugin
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest
import java.security.NoSuchAlgorithmException
import java.util.Locale
import java.util.concurrent.Executors
import javax.net.ssl.HttpsURLConnection

/**
 * Updates the sideloaded beta (beta flavor only; Play builds never contain it). `canInstall` resolves
 * `{ value }` for the "Install unknown apps" permission, `openInstallSettings` opens that setting for
 * Ember, and `downloadAndInstall({ url, sha256, size? })` downloads an https APK to the cache, checks its
 * SHA-256 (and size when given) and opens Android's package installer, which always asks the user to confirm.
 * Emits `downloadProgress` with `{ received, total }` while downloading.
 */
@CapacitorPlugin(name = "EmberUpdater")
class EmberUpdaterPlugin : Plugin() {

    private val worker = Executors.newSingleThreadExecutor()

    override fun load() {
        worker.execute { clear(updatesDir()) }
    }

    @PluginMethod
    fun canInstall(call: PluginCall) {
        val result = JSObject()
        result.put("value", Build.VERSION.SDK_INT < Build.VERSION_CODES.O || context.packageManager.canRequestPackageInstalls())
        call.resolve(result)
    }

    @PluginMethod
    fun openInstallSettings(call: PluginCall) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) {
            call.resolve()
            return
        }
        val intent = Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, "package:${context.packageName}".toUri())
        try {
            activity.startActivity(intent)
        } catch (e: RuntimeException) {
            call.reject("Could not open the setting")
            return
        }
        call.resolve()
    }

    @PluginMethod
    fun downloadAndInstall(call: PluginCall) {
        val url = call.getString("url")
        val sha256 = call.getString("sha256")
        val rawSize = if (call.data.isNull("size")) null else call.data.opt("size")
        val size = if (rawSize is Int || rawSize is Long) (rawSize as Number).toLong() else null
        if (url == null || !url.lowercase(Locale.ROOT).startsWith("https://")) {
            call.reject("The update must come over https")
            return
        }
        if (sha256 == null || !SHA256.matches(sha256)) {
            call.reject("Invalid checksum")
            return
        }
        if (rawSize != null && (size == null || size <= 0 || size > MAX_BYTES)) {
            call.reject("Invalid size")
            return
        }
        worker.execute { download(call, url, sha256, size) }
    }

    private fun download(call: PluginCall, url: String, sha256: String, size: Long?) {
        val dir = updatesDir()
        clear(dir)
        val apk = File(dir, "ember-update.apk")
        var connection: HttpURLConnection? = null
        try {
            if (!dir.isDirectory && !dir.mkdirs()) throw IOException("No cache directory")
            connection = URL(url).openConnection() as HttpsURLConnection
            connection.connectTimeout = 15_000
            connection.readTimeout = 30_000
            if (connection.responseCode != HttpURLConnection.HTTP_OK) throw IOException("HTTP " + connection.responseCode)
            val limit = size ?: MAX_BYTES
            val total = size ?: connection.contentLengthLong
            val digest = MessageDigest.getInstance("SHA-256")
            var received = 0L
            var reported = -1L
            connection.inputStream.use { input ->
                FileOutputStream(apk).use { out ->
                    val buffer = ByteArray(64 * 1024)
                    while (true) {
                        val read = input.read(buffer)
                        if (read == -1) break
                        received += read
                        if (received > limit) throw IOException("Too large")
                        digest.update(buffer, 0, read)
                        out.write(buffer, 0, read)
                        val percent = if (total > 0) received * 100 / total else -1
                        if (percent != reported) {
                            reported = percent
                            val progress = JSObject()
                            progress.put("received", received)
                            progress.put("total", if (total > 0) total else 0L)
                            notifyListeners("downloadProgress", progress)
                        }
                    }
                }
            }
            if (!matches(sha256, size, digest.digest(), received)) {
                clear(dir)
                call.reject("The download didn't match the update", "MISMATCH")
                return
            }
            val uri = FileProvider.getUriForFile(context, context.packageName + ".fileprovider", apk)
            val intent = Intent(Intent.ACTION_VIEW).setDataAndType(uri, APK_TYPE).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            activity.startActivity(intent)
            call.resolve()
        } catch (e: IOException) {
            failed(call, dir)
        } catch (e: NoSuchAlgorithmException) {
            failed(call, dir)
        } catch (e: RuntimeException) {
            failed(call, dir)
        } finally {
            connection?.disconnect()
        }
    }

    private fun failed(call: PluginCall, dir: File) {
        clear(dir)
        call.reject("Could not download the update")
    }

    private fun updatesDir() = File(context.cacheDir, "updates")

    companion object {
        private const val APK_TYPE = "application/vnd.android.package-archive"
        private const val MAX_BYTES = 200L * 1024 * 1024
        private val SHA256 = Regex("[0-9a-fA-F]{64}")

        /**
         * True when a download of [actualSize] bytes with SHA-256 [actualDigest] is the expected file:
         * the digest equals [expectedSha256] (hex, any case) and, when [expectedSize] is given, the size matches.
         */
        fun matches(expectedSha256: String, expectedSize: Long?, actualDigest: ByteArray, actualSize: Long): Boolean {
            if (expectedSize != null && expectedSize != actualSize) return false
            val hex = actualDigest.joinToString("") { "%02x".format(Locale.ROOT, it) }
            return MessageDigest.isEqual(
                hex.toByteArray(Charsets.US_ASCII),
                expectedSha256.lowercase(Locale.ROOT).toByteArray(Charsets.US_ASCII),
            )
        }

        private fun clear(dir: File) {
            dir.listFiles()?.forEach { it.delete() }
        }
    }
}
