package app.ember.habits.diagnostics

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.util.concurrent.atomic.AtomicBoolean

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

        /** Installs the recorder as the default handler, once per process, chained to the one before it. */
        fun install(context: Context) {
            if (!installed.compareAndSet(false, true)) return
            val records = RecordFile.crashes(context.applicationContext ?: context)
            Thread.setDefaultUncaughtExceptionHandler(CrashRecorder(records, Thread.getDefaultUncaughtExceptionHandler()))
        }

        /** The stored form of [throwable] at [at]: `{ at, exception: [...], frames: [...] }`. */
        fun record(throwable: Throwable, at: Long): JSONObject = JSONObject()
            .put(RecordFile.AT, at)
            .put(EXCEPTION, JSONArray(Frames.exceptionChain(throwable)))
            .put(FRAMES, JSONArray(Frames.java(throwable)))
    }
}
