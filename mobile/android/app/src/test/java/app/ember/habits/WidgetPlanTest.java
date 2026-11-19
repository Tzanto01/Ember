package app.ember.habits;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertNotEquals;
import static org.junit.Assert.assertTrue;

import java.util.Arrays;
import java.util.Collections;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import org.junit.Test;

public class WidgetPlanTest {

    private static final List<String> DAY = Arrays.asList("done", "done", "open", "partial", "open", "done");

    @Test
    public void twoByTwoShowsOnlyTheNextOpenHabit() {
        WidgetPlan plan = WidgetPlan.of(110, 110, DAY, 0);
        assertEquals(WidgetPlan.SINGLE, plan.layout);
        assertEquals(2, plan.start);
        assertEquals(1, plan.shown);
        assertEquals(0, plan.more);
    }

    @Test
    public void twoByTwoMovesOnAfterACheckIn() {
        List<String> checked = Arrays.asList("done", "done", "done", "partial", "open", "done");
        assertEquals(3, WidgetPlan.of(110, 110, checked, 0).start);
    }

    @Test
    public void twoByTwoOnARealLauncherIsSingle() {
        WidgetPlan plan = WidgetPlan.of(186, 212, DAY, 0);
        assertEquals(WidgetPlan.SINGLE, plan.layout);
        assertEquals(2, plan.start);
        assertEquals(1, plan.shown);
        assertTrue(WidgetPlan.dots(plan.layout, 186, DAY.size()));
    }

    @Test
    public void wideAndShortStartsAtTheFirstOpenHabit() {
        WidgetPlan low = WidgetPlan.of(250, 110, DAY, 0);
        assertEquals(WidgetPlan.SHORT, low.layout);
        assertEquals(2, low.start);
        assertEquals(1, low.shown);

        for (int width : new int[] { 287, 400 }) {
            WidgetPlan plan = WidgetPlan.of(width, 212, DAY, 0);
            assertEquals(WidgetPlan.SHORT, plan.layout);
            assertEquals(2, plan.start);
            assertEquals(3, plan.shown);
            assertEquals(0, plan.more);
        }

        WidgetPlan cut = WidgetPlan.of(287, 212, Arrays.asList("done", "open", "open"), 0);
        assertEquals(1, cut.start);
        assertEquals(2, cut.shown);
    }

    @Test
    public void threeRowsHighShowsTheWholeDayFromTheTop() {
        WidgetPlan narrow = WidgetPlan.of(186, 325, DAY, 0);
        assertEquals(WidgetPlan.TALL, narrow.layout);
        assertEquals(0, narrow.start);
        assertEquals(4, narrow.shown);
        assertEquals(2, narrow.more);

        WidgetPlan big = WidgetPlan.of(287, 437, DAY, 0);
        assertEquals(WidgetPlan.TALL, big.layout);
        assertEquals(0, big.start);
        assertEquals(6, big.shown);
        assertEquals(0, big.more);
    }

    @Test
    public void tallNeverShowsMoreRowsThanItHas() {
        WidgetPlan plan = WidgetPlan.of(250, 300, Arrays.asList("open", "done"), 5);
        assertEquals(2, plan.shown);
        assertEquals(5, plan.more);
    }

    @Test
    public void tallShowsEveryRowWhenTheyFit() {
        List<String> five = DAY.subList(0, 5);
        WidgetPlan plan = WidgetPlan.of(250, 300, five, 0);
        assertEquals(WidgetPlan.TALL, plan.layout);
        assertEquals(0, plan.start);
        assertEquals(5, plan.shown);
        assertEquals(0, plan.more);
    }

    @Test
    public void tallCountsUnlistedHabitsInMore() {
        WidgetPlan plan = WidgetPlan.of(250, 300, DAY.subList(0, 5), 2);
        assertEquals(4, plan.shown);
        assertEquals(3, plan.more);
    }

    @Test
    public void allDoneIgnoresRestButNotOpenPartialOrUnlistedRows() {
        assertTrue(WidgetPlan.allDone(Arrays.asList("done", "rest", "done"), 0));
        assertTrue(WidgetPlan.of(250, 300, Arrays.asList("done", "rest"), 0).allDone);
        assertFalse(WidgetPlan.allDone(Arrays.asList("done", "partial"), 0));
        assertFalse(WidgetPlan.allDone(Arrays.asList("done", "open"), 0));
        assertFalse(WidgetPlan.allDone(Arrays.asList("done", "done"), 1));
        assertFalse(WidgetPlan.allDone(Collections.<String>emptyList(), 0));
    }

    @Test
    public void everyWidgetSizePicksTheBreakpointThatRendersLikeItsRealSize() {
        assertTrue(WidgetPlan.SIZES.length <= 16);
        for (int width = 110; width <= 420; width += 7) {
            for (int height = 110; height <= 420; height += 3) {
                float[] picked = launcherPick(width, height);
                int layout = WidgetPlan.layout(width, height);
                String at = width + "x" + height;
                assertEquals(at, layout, WidgetPlan.layout(picked[0], picked[1]));
                assertEquals(at, WidgetPlan.rows(layout, height), WidgetPlan.rows(layout, picked[1]));
            }
        }
    }

    /** The launcher's rule on API 31+: the closest breakpoint that fits, else the smallest. */
    private static float[] launcherPick(float width, float height) {
        float[] best = null;
        float bestDistance = Float.MAX_VALUE;
        for (float[] size : WidgetPlan.SIZES) {
            if (size[0] > width || size[1] > height) continue;
            float distance = (width - size[0]) * (width - size[0]) + (height - size[1]) * (height - size[1]);
            if (distance < bestDistance) {
                best = size;
                bestDistance = distance;
            }
        }
        return best != null ? best : WidgetPlan.SIZES[0];
    }

    @Test
    public void doneCountFollowsRowsReachingAndLeavingDone() {
        assertEquals(3, WidgetPlan.done(2, 5, "partial", "done"));
        assertEquals(3, WidgetPlan.done(2, 5, "open", "done"));
        assertEquals(2, WidgetPlan.done(2, 5, "open", "partial"));
        assertEquals(1, WidgetPlan.done(2, 5, "done", "partial"));
        assertEquals(2, WidgetPlan.done(2, 5, "done", "done"));
        assertEquals(5, WidgetPlan.done(5, 5, "open", "done"));
        assertEquals(0, WidgetPlan.done(0, 5, "done", "open"));
    }

    @Test
    public void intentUriIsUniquePerActionHabitAndDate() {
        assertEquals("ember://widget/check-in/42/2026-11-19", WidgetPlan.uri(true, 42, "2026-11-19"));
        assertEquals("ember://widget/open/42/2026-11-19", WidgetPlan.uri(false, 42, "2026-11-19"));
        assertNotEquals(WidgetPlan.uri(true, 42, "2026-11-19"), WidgetPlan.uri(true, 43, "2026-11-19"));
        assertNotEquals(WidgetPlan.uri(true, 42, "2026-11-19"), WidgetPlan.uri(true, 42, "2026-11-20"));
    }

    @Test
    public void motionNeedsAStyleTheSettingAndRunningAnimations() {
        Map<String, Object> on = new HashMap<>();
        on.put(WidgetPlan.MOTION, true);
        Map<String, Object> off = new HashMap<>();
        off.put(WidgetPlan.MOTION, false);
        Map<String, Object> unstyled = new HashMap<>();
        unstyled.put("snapshot", "{}");
        assertTrue(WidgetPlan.motion(on, 1f));
        assertTrue(WidgetPlan.motion(on, 0.5f));
        assertFalse(WidgetPlan.motion(unstyled, 1f));
        assertFalse(WidgetPlan.motion(off, 1f));
        assertFalse(WidgetPlan.motion(on, 0f));
    }

    @Test
    public void headerDotsOnlyWhenTheyFitBesideFlameAndCount() {
        assertFalse(WidgetPlan.dots(WidgetPlan.SINGLE, 110, 3));
        assertTrue(WidgetPlan.dots(WidgetPlan.SINGLE, 170, 5));
        assertFalse(WidgetPlan.dots(WidgetPlan.SINGLE, 170, 9));
        assertTrue(WidgetPlan.dots(WidgetPlan.SHORT, 250, 10));
        assertFalse(WidgetPlan.dots(WidgetPlan.SHORT, 400, 11));
        assertFalse(WidgetPlan.dots(WidgetPlan.TALL, 400, 3));
        assertFalse(WidgetPlan.dots(WidgetPlan.SHORT, 250, 0));
    }

    @Test
    public void dayCountsMustMatchItsRows() {
        assertTrue(WidgetPlan.counts(2, 5, 2, 5, 0));
        assertTrue(WidgetPlan.counts(3, 30, 2, 25, 5));
        assertFalse(WidgetPlan.counts(1, 5, 2, 5, 0));
        assertFalse(WidgetPlan.counts(2, 6, 2, 5, 0));
        assertFalse(WidgetPlan.counts(2, 4, 2, 5, 0));
        assertFalse(WidgetPlan.counts(6, 5, 2, 5, 0));
    }
}
