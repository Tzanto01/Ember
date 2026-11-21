package app.ember.habits.diagnostics

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.IOException
import java.io.InputStream

/**
 * Exit records become exits: the last 7 days newest first, at most 5, reason and signal names, a CRASH matched to
 * Ember's own crash record, an ANR's main-thread frames, a trace that fails to read giving no frames.
 */
class ExitReasonsTest {

    private val now = 1_795_000_000_000L
    private val noTrace: () -> InputStream? = { null }

    private fun crash(at: Long, type: String) = JSONObject()
        .put(RecordFile.AT, at)
        .put(CrashRecorder.EXCEPTION, JSONArray(listOf(type)))
        .put(CrashRecorder.FRAMES, JSONArray(listOf("app.ember.habits.$type.run($type.kt:1)")))

    @Test
    fun exitsAreTheLastSevenDaysNewestFirstWithReasonAndSignalNames() {
        val records = listOf(
            ExitRecord(now - 10, 2, 9, noTrace),
            ExitRecord(now - 20, 5, 11, noTrace),
            ExitRecord(now - 30, 5, 64, noTrace),
            ExitRecord(now - 40, 3, 9, noTrace),
            ExitRecord(now - 50, 99, 0, noTrace),
            ExitRecord(now - 60, 10, 0, noTrace),
            ExitRecord(now - DiagnosticsGrammar.KEEP_MS - 1, 4, 0, noTrace),
        ).shuffled()

        val exits = ExitReasons.from(records, emptyList(), now, 34)

        assertEquals(listOf(now - 10, now - 20, now - 30, now - 40, now - 50), exits.map { it.at })
        assertEquals(listOf("SIGNALED", "CRASH_NATIVE", "CRASH_NATIVE", "LOW_MEMORY", "UNKNOWN"), exits.map { it.reason })
        assertEquals(listOf("SIGKILL", "SIGSEGV", null, null, null), exits.map { it.signal })
    }

    @Test
    fun aCrashTakesTheNewestOwnRecordWithinAMinuteBeforeIt() {
        val exitAt = now - 1000
        val crashes = listOf(
            crash(exitAt - 61_000, "TooEarly"),
            crash(exitAt - 50_000, "Older"),
            crash(exitAt - 2_000, "Newest"),
            crash(exitAt + 10, "After"),
        )

        val exit = ExitReasons.from(listOf(ExitRecord(exitAt, 4, 0, noTrace)), crashes, now, 34).single()
        val unmatched = ExitReasons.from(listOf(ExitRecord(exitAt - 120_000, 4, 0, noTrace)), crashes, now, 34).single()

        assertEquals(listOf("Newest"), exit.exception)
        assertEquals(listOf("app.ember.habits.Newest.run(Newest.kt:1)"), exit.frames)
        assertNull(exit.signal)
        assertEquals(emptyList<String>(), unmatched.exception)
        assertEquals(emptyList<String>(), unmatched.frames)
    }

    @Test
    fun anAnrReadsTheMainThreadAndABrokenTraceGivesNoFrames() {
        val trace = "\"main\" prio=5 tid=1 Blocked\n  at app.ember.habits.A.b(A.kt:3)\n  - locked <0x1>\n"
        val broken = object : InputStream() {
            override fun read(): Int = throw IOException("gone")
        }

        val exits = ExitReasons.from(
            listOf(
                ExitRecord(now, 6, 0) { ByteArrayInputStream(trace.toByteArray()) },
                ExitRecord(now - 1, 6, 0) { broken },
                ExitRecord(now - 2, 5, 6) { throw SecurityException("no") },
            ),
            emptyList(),
            now,
            34,
        )

        assertEquals(listOf("app.ember.habits.A.b(A.kt:3)"), exits[0].frames)
        assertEquals(emptyList<String>(), exits[1].frames)
        assertEquals(emptyList<String>(), exits[2].frames)
        assertEquals("SIGABRT", exits[2].signal)
        assertTrue(exits.all { it.exception.isEmpty() })
    }

    @Test
    fun aNativeCrashOnApi30ReadsTheTextTombstone() {
        val tombstone = "backtrace:\n      #00 pc 0004e1d4  /system/lib/libc.so (abort+164) (BuildId: 0a1b)\n"

        val exit = ExitReasons.from(listOf(ExitRecord(now, 5, 6) { ByteArrayInputStream(tombstone.toByteArray()) }), emptyList(), now, 30)
            .single()

        assertEquals(listOf("#00 pc 0004e1d4 libc.so (abort)"), exit.frames)
    }
}
