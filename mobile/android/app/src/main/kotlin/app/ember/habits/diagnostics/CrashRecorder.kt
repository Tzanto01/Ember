package app.ember.habits.diagnostics

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.concurrent.thread

/**
 * Ember's own uncaught-exception recorder: it stores the type names of the cause chain and the outermost throwable's
 * frames ([Frames]), never a message, in [records], and then always hands the exception on to the [previous] handler,
 * so the crash still reaches Android. A failure to record never changes how the crash proceeds.
 */
internal class CrashRecorder(
    private val records: RecordFile,
    private val previous: Thread.UncaughtExceptionHandler?,
    private val clock: () -> Long = System::currentTimeMillis,
) : Thread.UncaughtExceptionHandler {

    override fun uncaughtException(thread: Thread, throwable: Throwable) {
        try {
            val now = clock()
            records.append(record(throwable, now), now)
        } catch (ignored: Throwable) {
        }
        if (previous != null) {
            previous.uncaughtException(thread, throwable)
        } else {
            System.err.print("Exception in thread \"${thread.name}\" ")
            throwable.printStackTrace()
        }
    }

    companion object {
        const val EXCEPTION = "exception"
        const val FRAMES = "frames"

        private val installed = AtomicBoolean(false)

        /**
         * Installs the recorder as the default handler, once per process, chained to the one before it, and prunes the
         * crash and launch files to [DiagnosticsGrammar.KEEP_MS] in the background ([pruneInBackground]).
         */
        fun install(context: Context) {
            if (!installed.compareAndSet(false, true)) return
            val app = context.applicationContext ?: context
            val records = RecordFile.crashes(app)
            Thread.setDefaultUncaughtExceptionHandler(CrashRecorder(records, Thread.getDefaultUncaughtExceptionHandler()))
            pruneInBackground(listOf(records, RecordFile.launches(app)))
        }

        /** Prunes [files] as of [clock] on a low-priority daemon thread and returns it; any failure is ignored. */
        fun pruneInBackground(files: List<RecordFile>, clock: () -> Long = System::currentTimeMillis): Thread =
            thread(name = "ember-diagnostics-prune", isDaemon = true, priority = Thread.MIN_PRIORITY) {
                try {
                    val now = clock()
                    files.forEach { it.prune(now) }
                } catch (ignored: Throwable) {
                }
            }

        /** The stored form of [throwable] at [at]: `{ at, exception: [...], frames: [...] }`. */
        fun record(throwable: Throwable, at: Long): JSONObject = JSONObject()
            .put(RecordFile.AT, at)
            .put(EXCEPTION, JSONArray(Frames.exceptionChain(throwable)))
            .put(FRAMES, JSONArray(Frames.java(throwable)))
    }
}
