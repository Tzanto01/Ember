package app.ember.habits;

import java.util.List;

/**
 * What the "Today in Ember" widget shows at a size in dp, from its rows' states: the layout, the first row shown, how
 * many rows, the "+N more" count and whether the day is all done. Pure Java, so it runs in plain unit tests.
 */
final class WidgetPlan {

    /** 2x2: the next open habit with one big circle. */
    static final int SINGLE = 0;
    /** Two rows high: rows from the first open habit. */
    static final int SHORT = 1;
    /** Three rows or more: the whole day, done included. */
    static final int TALL = 2;

    static final int MAX_ROWS = 7;
    static final int CHROME_DP = 56;
    static final int ROW_DP = 48;
    static final int WIDE_DP = 180;
    static final int HIGH_DP = 180;

    /**
     * The {width, height} breakpoints for the sized RemoteViews on API 31+, at most 16. Each is the smallest size of
     * its layout and row count, so the launcher's closest fitting entry renders like {@link #of} at the real size.
     */
    static final float[][] SIZES = {
        { 110, 110 },
        { 180, 110 },
        { 180, 152 },
        { 110, 180 },
        { 110, 200 },
        { 110, 248 },
        { 110, 296 },
        { 110, 344 },
        { 110, 392 },
        { 180, 180 },
        { 180, 200 },
        { 180, 248 },
        { 180, 296 },
        { 180, 344 },
        { 180, 392 },
    };

    final int layout;
    final int start;
    final int shown;
    final int more;
    final boolean allDone;

    private WidgetPlan(int layout, int start, int shown, int more, boolean allDone) {
        this.layout = layout;
        this.start = start;
        this.shown = shown;
        this.more = more;
        this.allDone = allDone;
    }

    static int layout(float width, float height) {
        if (height < HIGH_DP) return width < WIDE_DP ? SINGLE : SHORT;
        return TALL;
    }

    /** Row slots at {@code height}: one for {@link #SINGLE}, else what fits after the chrome, 1 to {@link #MAX_ROWS}. */
    static int rows(int layout, float height) {
        if (layout == SINGLE) return 1;
        return Math.max(1, Math.min(MAX_ROWS, (int) ((height - CHROME_DP) / ROW_DP)));
    }

    /**
     * The plan for {@code states} (the snapshot rows in order) plus {@code more} unlisted ones. {@link #TALL} starts
     * at the first row and, when rows overflow, gives its last slot to "+N more"; the others start at the first open
     * or partial row and show no "+N more" unless no listed row is left.
     */
    static WidgetPlan of(float width, float height, List<String> states, int more) {
        int layout = layout(width, height);
        int rows = rows(layout, height);
        int n = states.size();
        boolean allDone = allDone(states, more);
        if (layout == TALL) {
            int shown = n + more > rows ? rows - 1 : n;
            return new WidgetPlan(layout, 0, shown, n + more - shown, allDone);
        }
        int start = firstOpen(states);
        int shown = Math.min(rows, n - start);
        return new WidgetPlan(layout, start, shown, shown == 0 ? more : 0, allDone);
    }

    static boolean checkIn(String state) {
        return "open".equals(state) || "partial".equals(state);
    }

    /** The index of the first open or partial row, or {@code states.size()} when none is. */
    static int firstOpen(List<String> states) {
        for (int i = 0; i < states.size(); i++) if (checkIn(states.get(i))) return i;
        return states.size();
    }

    /** True when there are rows, none is open or partial (a rest row doesn't count), and none is unlisted. */
    static boolean allDone(List<String> states, int more) {
        return !states.isEmpty() && more == 0 && firstOpen(states) == states.size();
    }

    /** {@code done} after a row went from {@code before} to {@code after}: up on reaching done, down on leaving it. */
    static int done(int done, int total, String before, String after) {
        boolean was = "done".equals(before);
        boolean is = "done".equals(after);
        int next = done + (is && !was ? 1 : 0) - (was && !is ? 1 : 0);
        return Math.max(0, Math.min(total, next));
    }

    /** The data Uri that makes a widget PendingIntent unique per action, habit and date. */
    static String uri(String action, long habitId, String date) {
        return "ember://widget/" + action + "/" + habitId + "/" + date;
    }

    /** The flame flickers only once the app sent a style, with motion on, and while system animations run. */
    static boolean motion(boolean styled, boolean on, float animatorScale) {
        return styled && on && animatorScale > 0;
    }
}
