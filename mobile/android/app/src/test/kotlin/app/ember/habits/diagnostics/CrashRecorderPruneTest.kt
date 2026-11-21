package app.ember.habits.diagnostics

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

/** The start-up prune that [CrashRecorder.install] runs removes records past the 7 days from the files themselves. */
class CrashRecorderPruneTest {

    @get:Rule
    val folder = TemporaryFolder()

    private val now = 1_795_000_000_000L

    private fun stored(file: File): List<Long> {
        val array = JSONArray(file.readText())
        return (0 until array.length()).map { array.getJSONObject(it).getLong(RecordFile.AT) }
    }

    @Test
    fun recordsOlderThanSevenDaysAreGoneFromBothFiles() {
        val crashFile = File(folder.root, "crashes.json")
        val launchFile = File(folder.root, "launches.json")
        val old = now - DiagnosticsGrammar.KEEP_MS - 1
        val recent = now - 1_000
        listOf(crashFile, launchFile).forEach { file ->
            val records = listOf(JSONObject().put(RecordFile.AT, old), JSONObject().put(RecordFile.AT, recent))
            RecordFile.write(file, JSONArray(records).toString())
        }
        val files = listOf(crashFile, launchFile).map { RecordFile(it, DiagnosticsGrammar.KEEP_MS, 10) }

        CrashRecorder.pruneInBackground(files) { now }.join()

        assertEquals(listOf(recent), stored(crashFile))
        assertEquals(listOf(recent), stored(launchFile))
    }
}
