package app.ember.habits.diagnostics

import android.app.ActivityManager
import android.app.ApplicationExitInfo
import android.content.Context
import android.os.Build
import androidx.annotation.RequiresApi
import org.json.JSONArray
import org.json.JSONObject
import java.io.FilterInputStream
import java.io.IOException
import java.io.InputStream

/**
 * One exit as Android reports it: the [process] that ended, epoch-millisecond [at], `REASON_*` value, status, and its
 * trace if it has one.
 */
internal class ExitRecord(
    val process: String,
    val at: Long,
    val reason: Int,
    val status: Int,
    val trace: () -> InputStream?,
)

/**
 * Ember's recent process exits from `ActivityManager.getHistoricalProcessExitReasons` (API 30+): the app's own process
 * only, never the WebView's sandboxed renderer that Android lists under the same package, the last 7 days less an hour,
 * newest first, at most [DiagnosticsPayload.MAX_EXITS]. Each carries its reason name, the signal for `CRASH_NATIVE`
 * and `SIGNALED`, for `CRASH` the matching record of Ember's own [CrashRecorder], for `ANR` the main thread's frames
 * and for `CRASH_NATIVE` the crashing thread's tombstone frames, all through the [Frames] allowlist parser. It never
 * reads the exit's description.
 */
internal object ExitReasons {

    /** How long before an exit Ember's crash record may be and still belong to it. */
    const val CRASH_MATCH_MS = 60_000L
    private const val MAX_TRACE_BYTES = 512 * 1024
    private const val SIGNALED = 2
    private const val CRASH = 4
    private const val CRASH_NATIVE = 5
    private const val ANR = 6

    /** The exits for this app as of [now]; empty below API 30. */
    fun collect(context: Context, crashes: List<JSONObject>, now: Long): List<Exit> {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) return emptyList()
        return from(records(context), context.packageName, crashes, now, Build.VERSION.SDK_INT)
    }

    /**
     * [records] of the main process [app] turned into exits as of [now] on API level [sdk], each crash matched against
     * [crashes].
     */
    fun from(records: List<ExitRecord>, app: String, crashes: List<JSONObject>, now: Long, sdk: Int): List<Exit> =
        records.filter { it.process == app && it.at in (now - DiagnosticsGrammar.REPORT_MS)..now }
            .sortedByDescending { it.at }
            .take(DiagnosticsPayload.MAX_EXITS)
            .map { exit(it, crashes, sdk) }

    private fun exit(record: ExitRecord, crashes: List<JSONObject>, sdk: Int): Exit {
        val crash = if (record.reason == CRASH) crashFor(record.at, crashes) else null
        return Exit(
            at = record.at,
            reason = DiagnosticsGrammar.reason(record.reason),
            signal = if (record.reason == CRASH_NATIVE || record.reason == SIGNALED) {
                DiagnosticsGrammar.signal(record.status)
            } else {
                null
            },
            exception = crash?.let { strings(it, CrashRecorder.EXCEPTION) }.orEmpty(),
            frames = when (record.reason) {
                CRASH -> crash?.let { strings(it, CrashRecorder.FRAMES) }.orEmpty()
                ANR -> trace(record) { Frames.anr(it.bufferedReader(Charsets.UTF_8)) }
                CRASH_NATIVE -> trace(record) {
                    if (sdk >= Build.VERSION_CODES.S) {
                        Frames.tombstone(it.readBytes())
                    } else {
                        Frames.nativeText(it.bufferedReader(Charsets.UTF_8))
                    }
                }
                else -> emptyList()
            },
        )
    }

    /** The newest crash record from at most [CRASH_MATCH_MS] before [exitAt]. */
    private fun crashFor(exitAt: Long, crashes: List<JSONObject>): JSONObject? =
        crashes.filter { it.optLong(RecordFile.AT) in (exitAt - CRASH_MATCH_MS)..exitAt }
            .maxByOrNull { it.optLong(RecordFile.AT) }

    private fun strings(record: JSONObject, key: String): List<String> {
        val array = record.optJSONArray(key) ?: JSONArray()
        return (0 until array.length()).mapNotNull { array.opt(it) as? String }
    }

    private fun trace(record: ExitRecord, parse: (InputStream) -> List<String>): List<String> =
        try {
            record.trace()?.use { parse(Bounded(it, MAX_TRACE_BYTES.toLong())) }.orEmpty()
        } catch (e: IOException) {
            emptyList()
        } catch (e: RuntimeException) {
            emptyList()
        }

    @RequiresApi(Build.VERSION_CODES.R)
    private fun records(context: Context): List<ExitRecord> {
        val manager = context.getSystemService(ActivityManager::class.java) ?: return emptyList()
        return manager.getHistoricalProcessExitReasons(context.packageName, 0, 0).map { info: ApplicationExitInfo ->
            ExitRecord(info.processName, info.timestamp, info.reason, info.status) { info.traceInputStream }
        }
    }
}

/** [stream] cut off after [limit] bytes, so a huge trace is never read whole. */
private class Bounded(stream: InputStream, private var limit: Long) : FilterInputStream(stream) {

    override fun read(): Int {
        if (limit <= 0) return -1
        return super.read().also { if (it >= 0) limit-- }
    }

    override fun read(buffer: ByteArray, offset: Int, length: Int): Int {
        if (limit <= 0) return -1
        return super.read(buffer, offset, minOf(length.toLong(), limit).toInt()).also { if (it > 0) limit -= it }
    }

    override fun skip(count: Long): Long = super.skip(minOf(count, limit)).also { limit -= it }

    override fun available(): Int = minOf(super.available().toLong(), limit).toInt()

    override fun markSupported() = false
}
