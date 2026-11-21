package app.ember.habits.diagnostics

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The `collect()` result: device facts sanitized, only the last 7 days, newest first, the caps, minute times, and the
 * 56 000-byte budget trimmed in the spec's order.
 */
class DiagnosticsPayloadTest {

    private val now = 1_795_000_000_000L
    private val minute = 60_000L
    private val facts = DeviceFacts("0.10.0-beta.4", "14", 34, "Google", "Pixel 7")

    private fun build(launches: List<Launch> = emptyList(), exits: List<Exit> = emptyList(), facts: DeviceFacts = this.facts) =
        DiagnosticsPayload.build("AAAAAAAAAAAAAAAAAAAAAA", facts, launches, exits, now)

    private fun frame(index: Int, padding: Int = 0) = "app.ember.habits.F${"x".repeat(padding)}.m$index(F.kt:$index)"

    private fun exit(at: Long, frames: Int = 0, padding: Int = 0) =
        Exit(at, "ANR", null, emptyList(), (0 until frames).map { frame(it, padding) })

    @Test
    fun theResultHasTheSpecShapeWithMinuteTimes() {
        val at = 1_794_999_937_123L
        val json = JSONObject(
            build(
                launches = listOf(Launch(at, "launch cold=true saved=false source=user history=false")),
                exits = listOf(Exit(at, "CRASH_NATIVE", "SIGSEGV", listOf("java.lang.Error"), listOf("#00 pc 0000000000001234 libc.so (abort)"))),
            ).toJson(),
        )

        assertEquals(setOf("installation", "version", "os", "device", "launches", "exits"), json.keys().asSequence().toSet())
        assertEquals("0.10.0-beta.4", json.getString("version"))
        assertEquals("Android 14 (API 34)", json.getString("os"))
        assertEquals("Google Pixel 7", json.getString("device"))
        val launch = json.getJSONArray("launches").getJSONObject(0)
        assertEquals(DiagnosticsGrammar.minute(at), launch.getString("at"))
        assertTrue(launch.getString("at"), Regex("""^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:00Z$""").matches(launch.getString("at")))
        val exit = json.getJSONArray("exits").getJSONObject(0)
        assertEquals(setOf("at", "reason", "signal", "exception", "frames"), exit.keys().asSequence().toSet())
        assertEquals("SIGSEGV", exit.getString("signal"))
        assertEquals(1, exit.getJSONArray("frames").length())
    }

    @Test
    fun timesAreCutToTheMinuteInUtc() {
        assertEquals("2026-11-20T18:45:00Z", DiagnosticsGrammar.minute(1_795_200_312_345L))
        assertEquals("1970-01-01T00:00:00Z", DiagnosticsGrammar.minute(59_999L))
    }

    @Test
    fun onlyTheLastSevenDaysNewestFirstWithinTheCaps() {
        val launches = (0 until 60).map { Launch(now - it * minute, "new_intent source=user") } +
            Launch(now - DiagnosticsGrammar.KEEP_MS - 1, "destroy finishing=true config_change=false") +
            Launch(now + minute, "destroy finishing=false config_change=false") +
            Launch(now - 5, "Launch with Habit Walk")
        val exits = (0 until 7).map { exit(now - it * minute) } + exit(now - DiagnosticsGrammar.KEEP_MS - 1)

        val result = build(launches.shuffled(), exits.shuffled())

        assertEquals(DiagnosticsPayload.MAX_LAUNCHES, result.launches.size)
        assertEquals((0 until 50).map { now - it * minute }, result.launches.map { it.at })
        assertTrue(result.launches.all { it.event == "new_intent source=user" })
        assertEquals((0 until 5).map { now - it * minute }, result.exits.map { it.at })
    }

    @Test
    fun exitsKeepOnlyListedNamesAndFramesWithinTheirCaps() {
        val exits = listOf(
            Exit(now, "CRASH", "SIGWHAT", (0 until 12).map { "app.E$it" } + "not a type: message", (0 until 45).map(::frame) + "secret text"),
            Exit(now - 1, "SOMETHING_ELSE", null, emptyList(), emptyList()),
            exit(now - 2, frames = 40, padding = 250),
        )

        val result = build(exits = exits)

        assertEquals(2, result.exits.size)
        val crash = result.exits[0]
        assertNull(crash.signal)
        assertEquals((0 until 10).map { "app.E$it" }, crash.exception)
        assertEquals((0 until 40).map(::frame), crash.frames)
        val wide = result.exits[1].frames
        assertTrue(wide.sumOf { it.length } <= DiagnosticsPayload.MAX_FRAME_BYTES)
        assertEquals(DiagnosticsPayload.MAX_FRAME_BYTES / wide[0].length, wide.size)
    }

    @Test
    fun overTheBudgetFramesGoFromTheEndOfTheOldestExitsFirstAndNoMoreThanNeeded() {
        val event = "launch" + ('a'..'h').joinToString("") { " ${it.toString().repeat(20)}=${"9".repeat(40)}" }
        assertTrue(DiagnosticsGrammar.EVENT.matches(event))
        val launches = (0 until 50).map { Launch(now - it * minute, event) }
        val exits = (0 until 5).map { exit(now - it * minute, frames = 40, padding = 160) }
        val untrimmed = DeviceDiagnostics.timeline(launches, exits).toByteArray().size
        assertTrue("$untrimmed", untrimmed >= DiagnosticsPayload.BUDGET_BYTES)

        val result = build(launches, exits)

        assertTrue(bytes(result) < DiagnosticsPayload.BUDGET_BYTES)
        assertEquals(launches, result.launches)
        assertEquals(exits.map { it.at }, result.exits.map { it.at })
        val counts = result.exits.map { it.frames.size }
        val partial = counts.indexOfFirst { it < 40 }
        assertTrue("$counts", partial in 0..4)
        assertTrue("$counts", counts.drop(partial + 1).all { it == 0 })
        result.exits.forEachIndexed { index, exit -> assertEquals(exits[index].frames.take(counts[index]), exit.frames) }
        val oneMore = result.exits.toMutableList().also { it[partial] = exits[partial].copy(frames = exits[partial].frames.take(counts[partial] + 1)) }
        assertTrue(DeviceDiagnostics.timeline(result.launches, oneMore).toByteArray().size >= DiagnosticsPayload.BUDGET_BYTES)
    }

    private fun bytes(result: DeviceDiagnostics) =
        DeviceDiagnostics.timeline(result.launches, result.exits).toByteArray(Charsets.UTF_8).size

    @Test
    fun deviceAndOsAreSanitized() {
        assertEquals("Android 14QPR2 (API 34)", DiagnosticsPayload.os("14 QPR2", 34))
        assertEquals("Android 34 (API 34)", DiagnosticsPayload.os("\u0000 \n", 34))
        assertEquals("Android 34 (API 34)", DiagnosticsPayload.os(null, 34))
        assertEquals("Android ${"1".repeat(20)} (API 34)", DiagnosticsPayload.os("1".repeat(30), 34))
        assertEquals("Samsung SM-S918B (Galaxy)", DiagnosticsPayload.device(" Samsung\u0007 ", "SM-S918B   (Galaxy)\n\u00e9"))
        assertEquals("a".repeat(40) + " " + "b".repeat(23), DiagnosticsPayload.device("a".repeat(40), "b".repeat(40)))
        assertEquals("a".repeat(63), DiagnosticsPayload.device("a".repeat(63), "b"))
        assertNull(DiagnosticsPayload.device("\u00c4\u00d6", null))
        assertNull(DiagnosticsPayload.version("1.0 beta"))
        assertNull(DiagnosticsPayload.version("1".repeat(33)))
        assertNull(DiagnosticsPayload.version(""))
        assertEquals("0.10.0+build_7-beta", DiagnosticsPayload.version("0.10.0+build_7-beta"))
        val json = JSONObject(build(facts = DeviceFacts(null, null, 30, null, null)).toJson())
        assertTrue(json.isNull("version"))
        assertTrue(json.isNull("device"))
        assertEquals("Android 30 (API 30)", json.getString("os"))
        assertEquals(JSONArray().toString(), json.getJSONArray("exits").toString())
    }
}
