package app.ember.habits.diagnostics

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File
import kotlin.concurrent.thread

/** The on-device ring buffer keeps 7 days and a fixed count, survives a new instance, and loses no concurrent write. */
class RecordFileTest {

    @get:Rule
    val folder = TemporaryFolder()

    private val day = 24 * 60 * 60 * 1000L
    private val now = 1_795_000_000_000L

    private fun file() = File(folder.root, "records.json")

    private fun record(at: Long, event: String = "destroy finishing=true config_change=false") =
        JSONObject().put(RecordFile.AT, at).put(RecordFile.EVENT, event)

    private fun RecordFile.ats(at: Long) = read(at).map { it.getLong(RecordFile.AT) }

    @Test
    fun writesPruneByAgeAndCountAndANewInstanceReadsWhatWasWritten() {
        val records = RecordFile(file(), 7 * day, 3)
        records.append(record(now - 8 * day), now - 8 * day)
        records.append(record(now - 2 * day), now)
        records.append(record(now - 3 * day), now)
        records.append(record(now - day), now)
        records.append(record(now), now)

        assertEquals(listOf(now - 2 * day, now - day, now), RecordFile(file(), 7 * day, 3).ats(now))
        assertFalse(File(folder.root, "records.json.tmp").exists())
    }

    @Test
    fun readsDropRecordsThatAgedOutAndRewriteTheFile() {
        val records = RecordFile(file(), 7 * day, 10)
        records.append(record(now - 6 * day), now)
        records.append(record(now), now)

        assertEquals(listOf(now), records.ats(now + 2 * day))
        assertEquals(1, org.json.JSONArray(file().readText()).length())
    }

    @Test
    fun anUnreadableFileCountsAsEmptyAndIsReplacedOnTheNextWrite() {
        file().writeText("{not json")
        val records = RecordFile(file(), 7 * day, 10)

        assertEquals(emptyList<Long>(), records.ats(now))
        records.append(record(now), now)
        assertEquals(listOf(now), records.ats(now))
    }

    @Test
    fun concurrentWritersOnSeparateInstancesLoseNothing() {
        val writers = (0 until 8).map { worker ->
            thread {
                val records = RecordFile(file(), 7 * day, 1000)
                repeat(20) { records.append(record(now - worker * 100L - it), now) }
            }
        }
        writers.forEach { it.join() }

        assertEquals(160, RecordFile(file(), 7 * day, 1000).read(now).size)
    }
}
