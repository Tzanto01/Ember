package app.ember.habits;

import java.util.List;
import java.util.Map;

/**
 * What the "Today in Ember" widget shows at a size in dp, from its rows' states: the layout, the first row shown, how
 * many rows, the "+N more" count and whether the day is all done. Pure Java, so it runs in plain unit tests.
 */
final class WidgetPlan {

    /** Up to two rows high and two columns wide (2x2): the next open habit with one big circle. */
    static final int SINGLE = 0;
    /** Up to two rows high and three columns or wider: rows from the first open habit. */
    static final int SHORT = 1;
    /** Three rows or more: the whole day, done included. */
    static final int TALL = 2;

    static final int MAX_ROWS = 7;
    static final int CHROME_DP = 56;
    static final int ROW_DP = 48;
    /** From here on a widget is three columns or more on real launchers (two are about 186 dp). */
    static final int WIDE_DP = 250;
    /** From here on a widget is three rows or more on real launchers (two stay below about 240 dp). */
    static final int HIGH_DP = 260;
    static final int MAX_DOTS = 10;
    static final int DOT_DP = 7;
    /** The prefs key of the app's "motion" style flag; it exists only once the app sent a style. */
    static final String MOTION = "motion";

    /**
     * The {width, height} breakpoints for the sized RemoteViews on API 31+, at most 16. Each is the smallest size of
     * its layout and row count, so the launcher's closest fitting entry renders like {@link #of} at the real size.
     */
    static final float[][] SIZES = {
        { 110, 110 },
        { WIDE_DP, 110 },
        { WIDE_DP, 152 },
        { WIDE_DP, 200 },
        { WIDE_DP, 248 },
        { 110, HIGH_DP },
        { 110, 296 },
        { 110, 344 },
        { 110, 392 },
        { WIDE_DP, HIGH_DP },
        { WIDE_DP, 296 },
        { WIDE_DP, 344 },
        { WIDE_DP, 392 },
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
            int shown = n + more > rows ? Math.min(n, rows - 1) : n;
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

    /**
     * The data Uri of a row's PendingIntent, unique per action (check-in for an open or partial row, else open), habit
     * and date.
     */
    static String uri(boolean checkIn, long habitId, String date) {
        return "ember://widget/" + (checkIn ? "check-in" : "open") + "/" + habitId + "/" + date;
    }

    /**
     * The flame flickers only once the app sent a style ({@code style} holds {@link #MOTION}), with motion on, and
     * while system animations run.
     */
    static boolean motion(Map<String, ?> style, float animatorScale) {
        return Boolean.TRUE.equals(style.get(MOTION)) && animatorScale > 0;
    }

    /**
     * Whether the header has room for one dot per habit: never on {@link #TALL}, and only up to {@link #MAX_DOTS} and
     * what the width leaves beside the flame, the count and (except on {@link #SINGLE}) the title.
     */
    static boolean dots(int layout, float width, int total) {
        if (layout == TALL || total <= 0) return false;
        int room = (int) ((width - (layout == SINGLE ? 110 : 160)) / DOT_DP);
        return total <= Math.min(MAX_DOTS, room);
    }

    /**
     * Whether a day's counts fit its rows: {@code listed} rows ({@code doneRows} of them done) plus {@code more}
     * unlisted make {@code total}, and {@code done} is at least the done rows and at most {@code total}.
     */
    static boolean counts(int done, int total, int doneRows, int listed, int more) {
        return total == listed + more && done >= doneRows && done <= total;
    }
}
