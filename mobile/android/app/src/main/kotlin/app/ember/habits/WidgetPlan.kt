package app.ember.habits

import kotlin.math.max
import kotlin.math.min

/**
 * What the "Today in Ember" widget shows at a size in dp, from its rows' states: the layout, the first row shown, how
 * many rows, the "+N more" count and whether the day is all done. Pure Kotlin, so it runs in plain unit tests.
 */
internal data class WidgetPlan(
    val layout: Int,
    val start: Int,
    val shown: Int,
    val more: Int,
    val allDone: Boolean,
) {
    companion object {
        /** Up to two rows high and two columns wide (2x2): the next open habit with one big circle. */
        const val SINGLE = 0
        /** Up to two rows high and three columns or wider: rows from the first open habit. */
        const val SHORT = 1
        /** Three rows or more: the whole day, done included. */
        const val TALL = 2

        const val MAX_ROWS = 7
        const val CHROME_DP = 56
        const val ROW_DP = 48
        /** From here on a widget is three columns or more on real launchers (two are about 186 dp). */
        const val WIDE_DP = 250
        /** From here on a widget is three rows or more on real launchers (two stay below about 240 dp). */
        const val HIGH_DP = 260
        const val MAX_DOTS = 10
        const val DOT_DP = 7
        /** The prefs key of the app's "motion" style flag; it exists only once the app sent a style. */
        const val MOTION = "motion"

        /**
         * The {width, height} breakpoints for the sized RemoteViews on API 31+, at most 16. Each is the smallest size
         * of its layout and row count, so the launcher's closest fitting entry renders like [of] at the real size.
         */
        val SIZES: Array<FloatArray> = arrayOf(
            floatArrayOf(110f, 110f),
            floatArrayOf(180f, 110f),
            floatArrayOf(WIDE_DP.toFloat(), 110f),
            floatArrayOf(WIDE_DP.toFloat(), 152f),
            floatArrayOf(WIDE_DP.toFloat(), 200f),
            floatArrayOf(WIDE_DP.toFloat(), 248f),
            floatArrayOf(110f, HIGH_DP.toFloat()),
            floatArrayOf(110f, 296f),
            floatArrayOf(110f, 344f),
            floatArrayOf(110f, 392f),
            floatArrayOf(WIDE_DP.toFloat(), HIGH_DP.toFloat()),
            floatArrayOf(WIDE_DP.toFloat(), 296f),
            floatArrayOf(WIDE_DP.toFloat(), 344f),
            floatArrayOf(WIDE_DP.toFloat(), 392f),
        )

        fun layout(width: Float, height: Float) =
            if (height < HIGH_DP) { if (width < WIDE_DP) SINGLE else SHORT } else TALL

        /** Row slots at [height]: one for [SINGLE], else what fits after the chrome, 1 to [MAX_ROWS]. */
        fun rows(layout: Int, height: Float): Int {
            if (layout == SINGLE) return 1
            return max(1, min(MAX_ROWS, ((height - CHROME_DP) / ROW_DP).toInt()))
        }

        /**
         * The plan for [states] (the snapshot rows in order) plus [more] unlisted ones. [TALL] starts at the first
         * row and, when rows overflow, gives its last slot to "+N more"; the others start at the first open or
         * partial row and show no "+N more" unless no listed row is left.
         */
        fun of(width: Float, height: Float, states: List<String?>, more: Int): WidgetPlan {
            val layout = layout(width, height)
            val rows = rows(layout, height)
            val n = states.size
            val allDone = allDone(states, more)
            if (layout == TALL) {
                val shown = if (n + more > rows) min(n, rows - 1) else n
                return WidgetPlan(layout, 0, shown, n + more - shown, allDone)
            }
            val start = firstOpen(states)
            val shown = min(rows, n - start)
            return WidgetPlan(layout, start, shown, if (shown == 0) more else 0, allDone)
        }

        fun checkIn(state: String?) = state == "open" || state == "partial"

        /** The index of the first open or partial row, or `states.size` when none is. */
        fun firstOpen(states: List<String?>): Int {
            val index = states.indexOfFirst(::checkIn)
            return if (index < 0) states.size else index
        }

        /** True when there are rows, none is open or partial (a rest row doesn't count), and none is unlisted. */
        fun allDone(states: List<String?>, more: Int) = states.isNotEmpty() && more == 0 && firstOpen(states) == states.size

        /** [done] after a row went from [before] to [after]: up on reaching done, down on leaving it. */
        fun done(done: Int, total: Int, before: String?, after: String?): Int {
            val was = before == "done"
            val isDone = after == "done"
            val next = done + (if (isDone && !was) 1 else 0) - (if (was && !isDone) 1 else 0)
            return max(0, min(total, next))
        }

        /**
         * The data Uri of a row's PendingIntent, unique per action (check-in for an open or partial row, else open),
         * habit and date.
         */
        fun uri(checkIn: Boolean, habitId: Long, date: String?) =
            "ember://widget/${if (checkIn) "check-in" else "open"}/$habitId/$date"

        /**
         * The flame flickers only once the app sent a style ([style] holds [MOTION]), with motion on, and while
         * system animations run.
         */
        fun motion(style: Map<String, *>, animatorScale: Float) = style[MOTION] == true && animatorScale > 0

        /**
         * Whether the header has room for one dot per habit: never on [TALL], and only up to [MAX_DOTS] and what the
         * width leaves beside the flame, the count and (except on [SINGLE]) the title.
         */
        fun dots(layout: Int, width: Float, total: Int): Boolean {
            if (layout == TALL || total <= 0) return false
            val room = ((width - (if (layout == SINGLE) 110 else 160)) / DOT_DP).toInt()
            return total <= min(MAX_DOTS, room)
        }

        /**
         * Whether a day's counts fit its rows: [listed] rows ([doneRows] of them done) plus [more] unlisted make
         * [total], and [done] is at least the done rows and at most [total].
         */
        fun counts(done: Int, total: Int, doneRows: Int, listed: Int, more: Int) =
            total == listed + more && done >= doneRows && done <= total
    }
}
