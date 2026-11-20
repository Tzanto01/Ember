package app.ember.habits

import android.content.Context
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.ZoneOffset
import java.util.Locale
import org.json.JSONArray
import org.json.JSONException
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test

/** The widget shows the snapshot the app pushes only when it is valid and for today in the account's zone. */
class WidgetStoreSnapshotTest {

    private val context = InMemoryContext()

    private class Zone(val id: String, val today: String)

    /** UTC offsets whose local time is at least an hour from midnight, so no test straddles a date change. */
    private val zones = (-12..14)
        .map { offset -> offset to LocalDateTime.now(ZoneOffset.ofHours(offset)) }
        .filter { (_, now) -> now.hour in 1..22 }
        .map { (offset, now) -> Zone(String.format(Locale.ROOT, "GMT%+03d:00", offset), now.toLocalDate().toString()) }

    private val zone = zones.first()

    private fun habit(id: Long, name: String = "Water", state: String = "open", count: Int = 0, timesPerDay: Int = 1) =
        JSONObject().put("id", id).put("name", name).put("state", state).put("count", count).put("timesPerDay", timesPerDay)

    private fun rows(vararg habits: JSONObject) = JSONArray().apply { habits.forEach { put(it) } }

    private fun save(
        userId: String? = "user-1",
        date: String? = zone.today,
        timeZone: String? = zone.id,
        habits: JSONArray = rows(habit(1)),
        more: Int = 0,
        done: Int? = null,
        total: Int? = null,
        next: JSONObject? = null
    ) = WidgetStore.save(context, userId, date, timeZone, habits, more, done, total, next)

    private fun dayBefore(date: String) = LocalDate.parse(date).minusDays(1).toString()

    @Test
    fun snapshotForTodayInItsZoneIsShownWithCleanedRowsAndCounts() {
        save(
            habits = rows(habit(1, name = "  Water  ", state = "done", count = 1), habit(2, name = "x".repeat(120), state = "partial", count = 5, timesPerDay = 3)),
            more = 2
        )

        val current = WidgetStore.current(context)
        assertNotNull(current)
        assertEquals(zone.today, current!!.getString("date"))
        val habits = current.getJSONArray("habits")
        assertEquals("Water", habits.getJSONObject(0).getString("name"))
        assertEquals("x".repeat(100), habits.getJSONObject(1).getString("name"))
        assertEquals(3, habits.getJSONObject(1).getInt("count"))
        assertEquals(1, current.getInt("done"))
        assertEquals(4, current.getInt("total"))
    }

    @Test
    fun snapshotForAnEarlierDayIsNotShown() {
        save(date = dayBefore(zone.today))

        assertNull(WidgetStore.current(context))
    }

    @Test
    fun todayIsJudgedInTheSnapshotsZone() {
        val elsewhere = zones.first { it.today != zone.today }

        save(timeZone = elsewhere.id)

        assertNull(WidgetStore.current(context))
    }

    @Test
    fun nextDayTakesOverOnceItsDateArrives() {
        val next = JSONObject()
            .put("date", zone.today)
            .put("habits", rows(habit(7, name = "Read", state = "done", count = 1)))
            .put("more", 1)
            .put("done", 2)
            .put("total", 2)

        save(date = dayBefore(zone.today), habits = rows(habit(1)), next = next)

        val current = WidgetStore.current(context)
        assertNotNull(current)
        assertEquals(zone.today, current!!.getString("date"))
        val habits = current.getJSONArray("habits")
        assertEquals(1, habits.length())
        assertEquals("Read", habits.getJSONObject(0).getString("name"))
        assertEquals(2, current.getInt("done"))
        assertEquals(2, current.getInt("total"))
    }

    @Test
    fun invalidSnapshotIsRejectedAndKeepsTheShownOne() {
        save(habits = rows(habit(1, name = "Kept")))
        val invalid = mapOf<String, () -> Unit>(
            "blank user" to { save(userId = "") },
            "long user" to { save(userId = "u".repeat(101)) },
            "no zone" to { save(timeZone = null) },
            "non-ISO date" to { save(date = "2026-3-14") },
            "too many rows" to { save(habits = rows(*Array(WidgetStore.MAX_HABITS + 1) { habit(it + 1L) })) },
            "negative more" to { save(more = -1) },
            "habit id 0" to { save(habits = rows(habit(0))) },
            "blank name" to { save(habits = rows(habit(1, name = "   "))) },
            "unknown state" to { save(habits = rows(habit(1, state = "skipped"))) },
            "no times per day" to { save(habits = rows(habit(1, timesPerDay = 0))) },
            "negative count" to { save(habits = rows(habit(1, count = -1))) },
            "missing name" to { save(habits = rows(habit(1).apply { remove("name") })) },
            "done above total" to { save(done = 2) },
            "fewer done than done rows" to { save(habits = rows(habit(1, state = "done", count = 1)), done = 0) },
            "total not rows plus more" to { save(total = 3) },
            "next not after date" to {
                save(next = JSONObject().put("date", zone.today).put("habits", rows(habit(2))).put("more", 0))
            },
            "invalid next row" to {
                save(next = JSONObject().put("date", "9999-12-31").put("habits", rows(habit(2, state = "skipped"))).put("more", 0))
            }
        )

        for ((case, push) in invalid) {
            try {
                push()
                fail("accepted: $case")
            } catch (e: IllegalArgumentException) {
            } catch (e: JSONException) {}
            val current = WidgetStore.current(context)
            assertNotNull(case, current)
            assertEquals(case, "Kept", current!!.getJSONArray("habits").getJSONObject(0).getString("name"))
        }
    }

    private val prefs get() = context.getSharedPreferences("ember_widget", Context.MODE_PRIVATE)

    @Test
    fun snapshotStoredByTheInstalledVersionIsShown() {
        val stored = JSONObject()
            .put("date", zone.today)
            .put("habits", rows(habit(3, name = "Walk")))
            .put("more", 0)
            .put("done", 0)
            .put("total", 1)
            .put("userId", "user-1")
            .put("timeZone", zone.id)
            .put("token", "0".repeat(32))
        prefs.edit().putString("snapshot", stored.toString()).commit()

        val current = WidgetStore.current(context)
        assertNotNull(current)
        assertEquals("Walk", current!!.getJSONArray("habits").getJSONObject(0).getString("name"))
        assertEquals("0".repeat(32), current.getString("token"))
    }

    @Test
    fun everySavedSnapshotGetsAFreshTapToken() {
        save()
        val first = WidgetStore.current(context)!!.getString("token")
        save()
        val second = WidgetStore.current(context)!!.getString("token")

        assertTrue(first, Regex("[0-9a-f]{32}").matches(first))
        assertTrue(second, Regex("[0-9a-f]{32}").matches(second))
        assertNotEquals(first, second)
    }

    @Test
    fun signingOutKeepsOnlyTheStyle() {
        WidgetStore.setStyle(context, "ember", true, "dark")
        save()
        WidgetStore.addPending(context, "user-1", 1, zone.today, 1)
        prefs.edit().putString("widgetKey", "sealed").putString("widgetKeyIv", "iv").putString("apiBase", "https://api.example.test").commit()

        WidgetStore.clear(context)

        assertEquals(mapOf("theme" to "ember", "colorScheme" to "dark", "motion" to true), prefs.all)
        assertNull(WidgetStore.current(context))
    }

    private fun markDone(token: String, date: String, habitId: Long) =
        synchronized(WidgetStore.LOCK) {
            WidgetStore.editRow(context, token, date, habitId) { row ->
                row.put("state", "done").put("count", 1)
                true
            }
        }

    @Test
    fun aRowEditLandsOnlyWhileTheSnapshotIsTheOneTapped() {
        save(habits = rows(habit(1), habit(2)))
        val token = WidgetStore.current(context)!!.getString("token")

        assertFalse(markDone("0".repeat(32), zone.today, 1))
        assertFalse(markDone(token, dayBefore(zone.today), 1))
        assertFalse(markDone(token, zone.today, 3))
        assertEquals(0, WidgetStore.current(context)!!.getInt("done"))

        assertTrue(markDone(token, zone.today, 1))
        val current = WidgetStore.current(context)!!
        assertEquals("done", current.getJSONArray("habits").getJSONObject(0).getString("state"))
        assertEquals(1, current.getInt("done"))
    }

    @Test
    fun aRowOfTheNextDayIsEditedInThatDay() {
        val tomorrow = LocalDate.parse(zone.today).plusDays(1).toString()
        save(habits = rows(habit(1)), next = JSONObject().put("date", tomorrow).put("habits", rows(habit(1))).put("more", 0))
        val token = WidgetStore.current(context)!!.getString("token")

        assertTrue(markDone(token, tomorrow, 1))

        val stored = JSONObject(prefs.getString("snapshot", null)!!)
        assertEquals("open", stored.getJSONArray("habits").getJSONObject(0).getString("state"))
        val next = stored.getJSONObject("next")
        assertEquals("done", next.getJSONArray("habits").getJSONObject(0).getString("state"))
        assertEquals(1, next.getInt("done"))
    }
}
