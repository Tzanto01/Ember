package app.ember.habits

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Test

/** Widget check-ins the server has not confirmed are kept once per user, habit and day until JS takes them. */
class TodayWidgetPendingTest {

    private val context = InMemoryContext()

    private fun taken(): Set<String> {
        val entries = TodayWidget.takePending(context)
        return (0 until entries.length()).map { entries.getJSONObject(it).describe() }.toSet()
    }

    private fun JSONObject.describe() = "${getString("userId")}/${getLong("habitId")}/${getString("date")}=${getInt("count")}"

    @Test
    fun takePendingHandsOverEachCheckInOnce() {
        TodayWidget.addPending(context, "user-1", 5, "2026-03-14", 1)
        TodayWidget.addPending(context, "user-1", 6, "2026-03-14", 2)

        assertEquals(setOf("user-1/5/2026-03-14=1", "user-1/6/2026-03-14=2"), taken())
        assertEquals(emptySet<String>(), taken())
    }

    @Test
    fun repeatedTapsOnOneHabitAndDayKeepTheHighestCount() {
        TodayWidget.addPending(context, "user-1", 5, "2026-03-14", 1)
        TodayWidget.addPending(context, "user-1", 5, "2026-03-14", 3)
        TodayWidget.addPending(context, "user-1", 5, "2026-03-14", 2)

        assertEquals(setOf("user-1/5/2026-03-14=3"), taken())
    }

    @Test
    fun checkInsForAnotherUserHabitOrDayStaySeparate() {
        TodayWidget.addPending(context, "user-1", 5, "2026-03-14", 1)
        TodayWidget.addPending(context, "user-2", 5, "2026-03-14", 2)
        TodayWidget.addPending(context, "user-1", 6, "2026-03-14", 3)
        TodayWidget.addPending(context, "user-1", 5, "2026-03-15", 4)

        assertEquals(
            setOf("user-1/5/2026-03-14=1", "user-2/5/2026-03-14=2", "user-1/6/2026-03-14=3", "user-1/5/2026-03-15=4"),
            taken()
        )
    }

    @Test
    fun settlingDropsOnlyTheMatchingEntryOnceTheServerReachedItsCount() {
        TodayWidget.addPending(context, "user-1", 5, "2026-03-14", 3)
        TodayWidget.addPending(context, "user-1", 6, "2026-03-14", 1)

        TodayWidget.settlePending(context, "user-1", 5, "2026-03-14", 2)
        TodayWidget.settlePending(context, "user-2", 5, "2026-03-14", 9)
        TodayWidget.settlePending(context, "user-1", 5, "2026-03-15", 9)
        TodayWidget.settlePending(context, "user-1", 6, "2026-03-14", 1)

        assertEquals(setOf("user-1/5/2026-03-14=3"), taken())
    }

    @Test
    fun signingOutForgetsPendingCheckIns() {
        TodayWidget.addPending(context, "user-1", 5, "2026-03-14", 1)

        TodayWidget.clear(context)

        assertEquals(emptySet<String>(), taken())
    }
}
