package app.ember.habits.diagnostics

import android.content.Context
import android.util.Base64
import java.io.File
import java.io.IOException
import java.security.SecureRandom

/**
 * The random per-install id the API rate-limits diagnostics uploads by: 16 random bytes as 22 characters of unpadded
 * base64url, kept in `noBackupFilesDir` so it is never backed up or restored onto another device. It is never part of
 * the uploaded body.
 */
internal object Installation {

    private val ID = Regex("""^[A-Za-z0-9_-]{22}$""")
    private const val FILE = "diagnostics-installation"

    /** The stored id, or a new one written atomically when there is none or it is malformed. */
    @Synchronized
    fun id(context: Context): String {
        val file = File(context.noBackupFilesDir, FILE)
        val stored = try {
            file.readText(Charsets.US_ASCII).trim()
        } catch (e: IOException) {
            null
        }
        if (stored != null && ID.matches(stored)) return stored
        val bytes = ByteArray(16).also(SecureRandom()::nextBytes)
        val id = Base64.encodeToString(bytes, Base64.URL_SAFE or Base64.NO_PADDING or Base64.NO_WRAP)
        try {
            RecordFile.write(file, id)
        } catch (e: IOException) {
        }
        return id
    }
}
