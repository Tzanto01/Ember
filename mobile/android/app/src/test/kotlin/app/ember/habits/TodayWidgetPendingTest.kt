package app.ember.habits

import android.content.Context
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

    @Test
    fun aCheckInStillBeingSentStaysStoredUntilItsRequestEnds() {
        TodayWidget.addPending(context, "user-1", 5, "2026-03-14", 1)
        TodayWidget.addPending(context, "user-1", 6, "2026-03-14", 1)
        TodayWidget.inFlight("user-1", 5, "2026-03-14", 1)

        try {
            assertEquals(setOf("user-1/6/2026-03-14=1"), taken())
        } finally {
            TodayWidget.inFlight("user-1", 5, "2026-03-14", -1)
        }
        assertEquals(setOf("user-1/5/2026-03-14=1"), taken())
    }

    @Test
    fun checkInsStoredByTheInstalledVersionAreHandedOver() {
        context.getSharedPreferences("ember_widget", Context.MODE_PRIVATE).edit()
            .putString("pending", """[{"userId":"user-1","habitId":5,"date":"2026-03-14","count":2}]""")
            .commit()

        assertEquals(setOf("user-1/5/2026-03-14=2"), taken())
    }

    @Test
    fun anUnreadablePendingStoreHandsOverNothingAndTheNextTapStartsAfresh() {
        context.getSharedPreferences("ember_widget", Context.MODE_PRIVATE).edit().putString("pending", "{").commit()

        assertEquals(emptySet<String>(), taken())
        TodayWidget.addPending(context, "user-1", 5, "2026-03-14", 1)
        assertEquals(setOf("user-1/5/2026-03-14=1"), taken())
    }
}
