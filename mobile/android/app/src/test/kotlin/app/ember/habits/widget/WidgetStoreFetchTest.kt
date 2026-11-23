package app.ember.habits.widget

import app.ember.habits.InMemoryContext
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/**
 * A day the widget fetched replaces the stored snapshot only for the same user and no earlier date, and never shows
 * less than the check-ins the server has not confirmed yet.
 */
class WidgetStoreFetchTest {

    private val context = InMemoryContext()

    private fun row(id: Long, state: String = "open", count: Int = 0, timesPerDay: Int = 1) =
        JSONObject().put("id", id).put("name", "Habit $id").put("state", state).put("count", count).put("timesPerDay", timesPerDay)

    private fun day(date: String, vararg rows: JSONObject) = JSONObject()
        .put("date", date)
        .put("habits", JSONArray(rows.toList()))
        .put("more", 0)
        .put("done", rows.count { it.getString("state") == "done" })
        .put("total", rows.size)

    private fun answer(date: String, vararg rows: JSONObject, userId: String = "user-1", next: JSONObject? = null) =
        day(date, *rows).put("userId", userId).put("timeZone", "Europe/Berlin").put("next", next ?: JSONObject.NULL)

    @Before
    fun setUp() {
        WidgetStore.save(context, "user-1", "2027-03-10", "Europe/Berlin", JSONArray().put(row(1)), 0, null, null, null)
    }

    private fun stored() = WidgetStore.stored(context)!!

    private fun JSONObject.row(id: Long): JSONObject {
        val habits = getJSONArray("habits")
        return (0 until habits.length()).map { habits.getJSONObject(it) }.first { it.getLong("id") == id }
    }

    @Test
    fun aPendingCheckInRaisesTheFetchedRowAndKeepsTheDaysDoneInStep() {
        WidgetStore.addPending(context, "user-1", 1, "2027-03-11", 1)
        WidgetStore.addPending(context, "user-1", 2, "2027-03-11", 2)
        WidgetStore.addPending(context, "user-1", 3, "2027-03-12", 2)

        val fetched = answer(
            "2027-03-11",
            row(1, timesPerDay = 3),
            row(2, timesPerDay = 2),
            next = day("2027-03-12", row(3, timesPerDay = 3)),
        )
        assertTrue(WidgetStore.saveFetched(context, fetched))

        val today = stored()
        assertEquals("partial", today.row(1).getString("state"))
        assertEquals(1, today.row(1).getInt("count"))
        assertEquals("done", today.row(2).getString("state"))
        assertEquals(2, today.row(2).getInt("count"))
        assertEquals(1, today.getInt("done"))
        val next = today.getJSONObject("next")
        assertEquals("partial", next.row(3).getString("state"))
        assertEquals(2, next.row(3).getInt("count"))
        assertEquals(0, next.getInt("done"))
    }

    @Test
    fun aPendingCheckInNeverLowersARowTheServerAlreadyHasFurther() {
        WidgetStore.addPending(context, "user-1", 1, "2027-03-11", 1)
        WidgetStore.addPending(context, "user-1", 2, "2027-03-11", 1)

        assertTrue(WidgetStore.saveFetched(context, answer("2027-03-11", row(1, "partial", 2, 3), row(2, "done", 1, 2))))

        assertEquals("partial", stored().row(1).getString("state"))
        assertEquals(2, stored().row(1).getInt("count"))
        assertEquals("done", stored().row(2).getString("state"))
        assertEquals(1, stored().getInt("done"))
    }

    @Test
    fun pendingCheckInsOfAnotherUserOrDayLeaveTheRowAndEveryEntryStaysPending() {
        WidgetStore.addPending(context, "user-2", 1, "2027-03-11", 1)
        WidgetStore.addPending(context, "user-1", 1, "2027-03-09", 1)
        WidgetStore.addPending(context, "user-1", 1, "2027-03-11", 1)

        assertTrue(WidgetStore.saveFetched(context, answer("2027-03-11", row(1), row(2))))

        assertEquals("done", stored().row(1).getString("state"))
        assertEquals("open", stored().row(2).getString("state"))
        assertEquals(3, WidgetStore.takePending(context).length())
    }

    @Test
    fun aDayForTheSameUserReplacesTheSnapshotWithAFreshTapToken() {
        val token = stored().getString("token")

        assertTrue(WidgetStore.saveFetched(context, answer("2027-03-10", row(1, "done", 1))))

        assertEquals("done", stored().row(1).getString("state"))
        assertNotEquals(token, stored().getString("token"))
    }

    @Test
    fun aDayForAnotherUserOrAnEarlierDateOrWithoutAStoredSnapshotIsNotStored() {
        val before = stored().toString()

        assertFalse(WidgetStore.saveFetched(context, answer("2027-03-11", row(2), userId = "user-2")))
        assertFalse(WidgetStore.saveFetched(context, answer("2027-03-09", row(2))))
        assertEquals(before, stored().toString())

        WidgetStore.clear(context)
        assertFalse(WidgetStore.saveFetched(context, answer("2027-03-11", row(2))))
        assertFalse(WidgetStore.hasSnapshot(context))
    }

    @Test
    fun anInvalidDayKeepsTheStoredSnapshot() {
        val before = stored().toString()
        val invalid = listOf(
            answer("2027-03-11", row(2, "skipped")),
            answer("2027-03-11", row(0)),
            answer("11.03.2027", row(2)),
            answer("2027-03-11", row(2)).put("total", 5),
            answer("2027-03-11", row(2), next = day("2027-03-11", row(3))),
            answer("2027-03-11", *(1L..26L).map { row(it) }.toTypedArray()),
            answer("2027-03-11", row(2)).apply { remove("timeZone") },
            answer("2027-03-11", row(2)).apply { remove("habits") },
        )
        for (each in invalid) assertFalse(each.toString(), WidgetStore.saveFetched(context, each))
        assertEquals(before, stored().toString())
    }
}
