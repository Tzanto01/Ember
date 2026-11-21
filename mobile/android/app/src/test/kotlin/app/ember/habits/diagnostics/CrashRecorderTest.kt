package app.ember.habits.diagnostics

import org.junit.Assert.assertEquals
import org.junit.Assert.assertSame
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

/** The recorder stores the crash's structure synchronously and always hands the crash on to the handler before it. */
class CrashRecorderTest {

    @get:Rule
    val folder = TemporaryFolder()

    private val now = 1_795_000_000_000L

    private class Previous : Thread.UncaughtExceptionHandler {
        val seen = ArrayList<Throwable>()
        override fun uncaughtException(thread: Thread, throwable: Throwable) {
            seen.add(throwable)
        }
    }

    @Test
    fun aCrashIsRecordedAndThenChainedToThePreviousHandler() {
        val records = RecordFile(File(folder.root, "crashes.json"), DiagnosticsGrammar.KEEP_MS, 10)
        val previous = Previous()
        val crash = IllegalStateException("secret")

        CrashRecorder(records, previous) { now }.uncaughtException(Thread.currentThread(), crash)

        assertSame(crash, previous.seen.single())
        val record = records.read(now).single()
        assertEquals(now, record.getLong(RecordFile.AT))
        assertEquals("java.lang.IllegalStateException", record.getJSONArray(CrashRecorder.EXCEPTION).getString(0))
    }

    @Test
    fun aFailedRecordStillChainsTheCrash() {
        val records = RecordFile(File(folder.root, "missing/dir/crashes.json"), DiagnosticsGrammar.KEEP_MS, 10)
        val previous = Previous()
        val crash = OutOfMemoryError()

        CrashRecorder(records, previous) { now }.uncaughtException(Thread.currentThread(), crash)

        assertSame(crash, previous.seen.single())
    }
}
