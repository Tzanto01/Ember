package app.ember.habits.diagnostics

import android.content.Context
import org.json.JSONArray
import org.json.JSONException
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.util.concurrent.ConcurrentHashMap

/**
 * A small on-device ring buffer of JSON records, each with an `at` time in epoch milliseconds, in one file under
 * `noBackupFilesDir` (never backed up). Every write and every read drops records older than [keepMs], and a write then
 * the oldest beyond [maxCount]. Instances on the same file share one lock, so writes from any thread never lose each
 * other. An unreadable file counts as empty.
 */
internal class RecordFile(private val file: File, private val keepMs: Long, private val maxCount: Int) {

    private val lock = LOCKS.getOrPut(file.absolutePath) { Any() }

    /** Adds [record] (which must carry `at`) and prunes, as of [now]. Throws [IOException] when it can't write. */
    fun append(record: JSONObject, now: Long) {
        synchronized(lock) {
            write(file, JSONArray(recent(readAll() + record, now).takeLast(maxCount)).toString())
        }
    }

    /** The stored records from the last [keepMs] before [now], oldest first; older ones are deleted from the file. */
    fun read(now: Long): List<JSONObject> =
        synchronized(lock) {
            val all = readAll()
            val kept = recent(all, now)
            if (kept.size < all.size) {
                try {
                    write(file, JSONArray(kept).toString())
                } catch (e: IOException) {
                }
            }
            kept
        }

    /** Deletes the records older than [keepMs] before [now] from the file. */
    fun prune(now: Long) {
        read(now)
    }

    private fun recent(records: List<JSONObject>, now: Long) =
        records.filter { it.optLong(AT) >= now - keepMs }.sortedBy { it.optLong(AT) }

    private fun readAll(): List<JSONObject> =
        try {
            val array = JSONArray(file.readText(Charsets.UTF_8))
            (0 until array.length()).mapNotNull { array.optJSONObject(it)?.takeIf { record -> record.has(AT) } }
        } catch (e: IOException) {
            emptyList()
        } catch (e: JSONException) {
            emptyList()
        }

    companion object {
        const val AT = "at"

        /** A launch record's event string, one of [LaunchEvents]. */
        const val EVENT = "event"
        private val LOCKS = ConcurrentHashMap<String, Any>()
        private const val MAX_LAUNCHES = 200
        private const val MAX_CRASHES = 10

        /** Ember's launch events, at most [MAX_LAUNCHES] from the last 7 days. */
        fun launches(context: Context) =
            RecordFile(File(context.noBackupFilesDir, "diagnostics-launches.json"), DiagnosticsGrammar.KEEP_MS, MAX_LAUNCHES)

        /** Ember's uncaught-exception records, at most [MAX_CRASHES] from the last 7 days. */
        fun crashes(context: Context) =
            RecordFile(File(context.noBackupFilesDir, "diagnostics-crashes.json"), DiagnosticsGrammar.KEEP_MS, MAX_CRASHES)

        /** Replaces [file]'s content with [text] atomically: a temp file beside it, then a rename over it. */
        fun write(file: File, text: String) {
            val temp = File(file.parentFile, "${file.name}.tmp")
            temp.writeText(text, Charsets.UTF_8)
            if (!temp.renameTo(file)) {
                temp.delete()
                throw IOException("Could not replace ${file.name}")
            }
        }
    }
}
