package app.ember.habits

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class WidgetPlanTest {

    private val day = listOf("done", "done", "open", "partial", "open", "done")

    @Test
    fun twoByTwoShowsOnlyTheNextOpenHabit() {
        assertEquals(WidgetPlan(WidgetPlan.SINGLE, 2, 1, 0, false), WidgetPlan.of(110f, 110f, day, 0))
    }

    @Test
    fun twoByTwoMovesOnAfterACheckIn() {
        val checked = listOf("done", "done", "done", "partial", "open", "done")
        assertEquals(3, WidgetPlan.of(110f, 110f, checked, 0).start)
    }

    @Test
    fun twoByTwoOnARealLauncherIsSingle() {
        val plan = WidgetPlan.of(186f, 212f, day, 0)
        assertEquals(WidgetPlan.SINGLE, plan.layout)
        assertEquals(2, plan.start)
        assertEquals(1, plan.shown)
        val rendered = launcherPick(186f, 212f)
        assertEquals(WidgetPlan.SINGLE, WidgetPlan.layout(rendered[0], rendered[1]))
        assertTrue(WidgetPlan.dots(WidgetPlan.SINGLE, rendered[0], day.size))
    }

    @Test
    fun wideAndShortStartsAtTheFirstOpenHabit() {
        assertEquals(WidgetPlan(WidgetPlan.SHORT, 2, 1, 0, false), WidgetPlan.of(250f, 110f, day, 0))

        for (width in listOf(287f, 400f)) {
            assertEquals(WidgetPlan(WidgetPlan.SHORT, 2, 3, 0, false), WidgetPlan.of(width, 212f, day, 0))
        }

        val cut = WidgetPlan.of(287f, 212f, listOf("done", "open", "open"), 0)
        assertEquals(1, cut.start)
        assertEquals(2, cut.shown)
    }

    @Test
    fun threeRowsHighShowsTheWholeDayFromTheTop() {
        assertEquals(WidgetPlan(WidgetPlan.TALL, 0, 4, 2, false), WidgetPlan.of(186f, 325f, day, 0))
        assertEquals(WidgetPlan(WidgetPlan.TALL, 0, 6, 0, false), WidgetPlan.of(287f, 437f, day, 0))
    }

    @Test
    fun tallNeverShowsMoreRowsThanItHas() {
        val plan = WidgetPlan.of(250f, 300f, listOf("open", "done"), 5)
        assertEquals(2, plan.shown)
        assertEquals(5, plan.more)
    }

    @Test
    fun tallShowsEveryRowWhenTheyFit() {
        assertEquals(WidgetPlan(WidgetPlan.TALL, 0, 5, 0, false), WidgetPlan.of(250f, 300f, day.subList(0, 5), 0))
    }

    @Test
    fun tallCountsUnlistedHabitsInMore() {
        val plan = WidgetPlan.of(250f, 300f, day.subList(0, 5), 2)
        assertEquals(4, plan.shown)
        assertEquals(3, plan.more)
    }

    @Test
    fun allDoneIgnoresRestButNotOpenPartialOrUnlistedRows() {
        assertTrue(WidgetPlan.allDone(listOf("done", "rest", "done"), 0))
        assertTrue(WidgetPlan.of(250f, 300f, listOf("done", "rest"), 0).allDone)
        assertFalse(WidgetPlan.allDone(listOf("done", "partial"), 0))
        assertFalse(WidgetPlan.allDone(listOf("done", "open"), 0))
        assertFalse(WidgetPlan.allDone(listOf("done", "done"), 1))
        assertFalse(WidgetPlan.allDone(emptyList(), 0))
    }

    @Test
    fun everyWidgetSizePicksTheBreakpointThatRendersLikeItsRealSize() {
        assertTrue(WidgetPlan.SIZES.size <= 16)
        for (width in 110..420 step 7) {
            for (height in 110..420 step 3) {
                val picked = launcherPick(width.toFloat(), height.toFloat())
                val layout = WidgetPlan.layout(width.toFloat(), height.toFloat())
                val at = "${width}x$height"
                assertEquals(at, layout, WidgetPlan.layout(picked[0], picked[1]))
                assertEquals(at, WidgetPlan.rows(layout, height.toFloat()), WidgetPlan.rows(layout, picked[1]))
            }
        }
    }

    /** The launcher's rule on API 31+: the closest breakpoint that fits, else the smallest. */
    private fun launcherPick(width: Float, height: Float): FloatArray =
        WidgetPlan.SIZES
            .filter { it[0] <= width && it[1] <= height }
            .minByOrNull { (width - it[0]) * (width - it[0]) + (height - it[1]) * (height - it[1]) }
            ?: WidgetPlan.SIZES[0]

    @Test
    fun doneCountFollowsRowsReachingAndLeavingDone() {
        assertEquals(3, WidgetPlan.done(2, 5, "partial", "done"))
        assertEquals(3, WidgetPlan.done(2, 5, "open", "done"))
        assertEquals(2, WidgetPlan.done(2, 5, "open", "partial"))
        assertEquals(1, WidgetPlan.done(2, 5, "done", "partial"))
        assertEquals(2, WidgetPlan.done(2, 5, "done", "done"))
        assertEquals(5, WidgetPlan.done(5, 5, "open", "done"))
        assertEquals(0, WidgetPlan.done(0, 5, "done", "open"))
    }

    @Test
    fun intentUriIsUniquePerActionHabitAndDate() {
        assertEquals("ember://widget/check-in/42/2026-11-19", WidgetPlan.uri(true, 42, "2026-11-19"))
        assertEquals("ember://widget/open/42/2026-11-19", WidgetPlan.uri(false, 42, "2026-11-19"))
        assertNotEquals(WidgetPlan.uri(true, 42, "2026-11-19"), WidgetPlan.uri(true, 43, "2026-11-19"))
        assertNotEquals(WidgetPlan.uri(true, 42, "2026-11-19"), WidgetPlan.uri(true, 42, "2026-11-20"))
    }

    @Test
    fun motionNeedsAStyleTheSettingAndRunningAnimations() {
        val on = mapOf(WidgetPlan.MOTION to true)
        assertTrue(WidgetPlan.motion(on, 1f))
        assertTrue(WidgetPlan.motion(on, 0.5f))
        assertFalse(WidgetPlan.motion(mapOf("snapshot" to "{}"), 1f))
        assertFalse(WidgetPlan.motion(mapOf(WidgetPlan.MOTION to false), 1f))
        assertFalse(WidgetPlan.motion(on, 0f))
    }

    @Test
    fun headerDotsOnlyWhenTheyFitBesideFlameAndCount() {
        assertFalse(WidgetPlan.dots(WidgetPlan.SINGLE, 110f, 3))
        assertTrue(WidgetPlan.dots(WidgetPlan.SINGLE, 170f, 5))
        assertFalse(WidgetPlan.dots(WidgetPlan.SINGLE, 170f, 9))
        assertTrue(WidgetPlan.dots(WidgetPlan.SHORT, 250f, 10))
        assertFalse(WidgetPlan.dots(WidgetPlan.SHORT, 400f, 11))
        assertFalse(WidgetPlan.dots(WidgetPlan.TALL, 400f, 3))
        assertFalse(WidgetPlan.dots(WidgetPlan.SHORT, 250f, 0))
    }

    @Test
    fun dayCountsMustMatchItsRows() {
        assertTrue(WidgetPlan.counts(2, 5, 2, 5, 0))
        assertTrue(WidgetPlan.counts(3, 30, 2, 25, 5))
        assertFalse(WidgetPlan.counts(1, 5, 2, 5, 0))
        assertFalse(WidgetPlan.counts(2, 6, 2, 5, 0))
        assertFalse(WidgetPlan.counts(2, 4, 2, 5, 0))
        assertFalse(WidgetPlan.counts(6, 5, 2, 5, 0))
    }
}
