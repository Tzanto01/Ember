package app.ember.habits

import android.app.AlarmManager
import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.os.SystemClock
import app.ember.habits.widget.CheckInClient
import app.ember.habits.widget.TodayWidgetViews
import app.ember.habits.widget.WidgetIntents
import app.ember.habits.widget.WidgetKey
import app.ember.habits.widget.WidgetStore
import com.getcapacitor.JSObject
import java.util.Calendar
import kotlin.math.min
import org.json.JSONObject

/**
 * The "Today in Ember" home-screen widget. It shows the stored snapshot ([WidgetStore]) only while that snapshot (or
 * the next day it carries) is for today in the account's zone, drawn by [TodayWidgetViews]. The app pushes the
 * snapshot; with a widget key stored the widget also fetches its day itself ([TodayRefreshJob]) after midnight, on a
 * widget update and when it finds the snapshot stale, the last two at most once per [REQUEST_GAP_MS].
 * Every row action carries the habit, date, user and a per-snapshot token in an explicit, immutable intent with its
 * own data Uri. With a widget key stored, checking an open or partial row (a CheckBox on API 31+, the row tap below)
 * is a broadcast to this receiver, which checks in at once and posts it with the key; without one it opens
 * [MainActivity], which hands the check-in to JS. A done or rest row's circle opens Today at that habit, never
 * undoing it; anywhere else opens Today.
 */
class TodayWidget : AppWidgetProvider() {

    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) {
        render(context, manager, ids, update = true)
    }

    override fun onAppWidgetOptionsChanged(context: Context, manager: AppWidgetManager, id: Int, options: Bundle?) {
        render(context, manager, intArrayOf(id))
    }

    /**
     * Redraws at the alarm set for the snapshot zone's next midnight, so yesterday's states never linger, asking for
     * the new day there when a key is stored, and handles a row's background check-in.
     */
    override fun onReceive(context: Context, intent: Intent) {
        when (intent.action) {
            ACTION_MIDNIGHT -> {
                if (WidgetKey.get(context) != null) requestDay(context, 0)
                refresh(context)
            }
            ACTION_CHECK_IN -> checkIn(context, intent)
            else -> super.onReceive(context, intent)
        }
    }

    /**
     * Checks in a validated open or partial row: the stored row moves to `min(count + 1, timesPerDay)` and a pending
     * entry for that count is saved before the POST ([CheckInClient.send]), so a failed or killed request is replayed
     * by JS. The request must finish within [BUDGET_MS] of the tap, or it is dropped and stays pending. Any other tap
     * only redraws.
     */
    private fun checkIn(context: Context, intent: Intent) {
        val deadline = SystemClock.elapsedRealtime() + BUDGET_MS
        val snapshot = WidgetStore.current(context)
        val key = WidgetKey.get(context)
        val apiBase = WidgetKey.apiBase(context)
        val habit = if (ours(context, intent, TodayWidget::class.java)) listed(snapshot, intent) else null
        if (snapshot == null || habit == null || key == null || apiBase == null) {
            refresh(context)
            return
        }
        val token = snapshot.optString("token")
        val userId = snapshot.optString("userId")
        val date = snapshot.optString("date")
        val habitId = habit.optLong("id")
        var target = 0
        val tapped = synchronized(WidgetStore.LOCK) {
            val edited = WidgetStore.editRow(context, token, date, habitId) { row ->
                val state = row.optString("state")
                if (state != "open" && state != "partial") return@editRow false
                val timesPerDay = row.optInt("timesPerDay")
                target = min(row.optInt("count") + 1, timesPerDay)
                row.put("count", target).put("state", if (target >= timesPerDay) "done" else "partial")
                true
            }
            if (edited) {
                WidgetStore.addPending(context, userId, habitId, date, target)
                WidgetStore.inFlight(userId, habitId, date, 1)
            }
            edited
        }
        if (!tapped) {
            refresh(context)
            return
        }
        val app = context.applicationContext
        var result: PendingResult? = null
        try {
            refresh(context)
            val async = goAsync()
            result = async
            CheckInClient.queue {
                try {
                    try {
                        CheckInClient.send(app, apiBase, key, token, userId, habitId, date, deadline)
                    } finally {
                        WidgetStore.inFlight(userId, habitId, date, -1)
                    }
                    refresh(app)
                } finally {
                    async.finish()
                }
            }
        } catch (e: RuntimeException) {
            WidgetStore.inFlight(userId, habitId, date, -1)
            result?.finish()
            throw e
        }
    }

    companion object {
        internal const val ACTION_CHECK_IN = "app.ember.habits.WIDGET_CHECK_IN"
        internal const val ACTION_OPEN_TODAY = "app.ember.habits.WIDGET_OPEN_TODAY"
        internal const val ACTION_OPEN_HABIT = "app.ember.habits.WIDGET_OPEN_HABIT"
        internal const val ACTION_MIDNIGHT = "app.ember.habits.WIDGET_MIDNIGHT"
        private const val BUDGET_MS = 8000L

        /** The least time between two day requests from a widget update or a stale snapshot. */
        internal const val REQUEST_GAP_MS = 15 * 60 * 1000L

        /** The actions of the intents a widget tap sends to [MainActivity]. */
        internal val TAP_ACTIONS = setOf(ACTION_CHECK_IN, ACTION_OPEN_TODAY, ACTION_OPEN_HABIT)

        /**
         * The JS payload for a widget intent, or null to ignore it. "Open Today" yields an empty object. A check-in
         * or an open-habit tap needs our component and a row [listed] in the current snapshot, so a stale row or an
         * intent from another app does nothing. An open-habit tap carries `open: true`.
         */
        internal fun validTap(context: Context, intent: Intent): JSObject? {
            if (!ours(context, intent, MainActivity::class.java)) return null
            if (intent.action == ACTION_OPEN_TODAY) return JSObject()
            val open = intent.action == ACTION_OPEN_HABIT
            if (!open && intent.action != ACTION_CHECK_IN) return null
            val habit = listed(WidgetStore.current(context), intent) ?: return null
            val tap = JSObject()
            tap.put("habitId", habit.optLong("id"))
            tap.put("date", intent.getStringExtra("date"))
            tap.put("userId", intent.getStringExtra("userId"))
            if (open) tap.put("open", true)
            return tap
        }

        private fun ours(context: Context, intent: Intent, target: Class<*>): Boolean {
            val component = intent.component ?: return false
            return component.packageName == context.packageName && target.name == component.className
        }

        /**
         * The snapshot row a tap names, or null: a habit id above 0 that is in [snapshot], the snapshot's date (ISO
         * form) and user, and the snapshot's tap token.
         */
        private fun listed(snapshot: JSONObject?, intent: Intent): JSONObject? {
            if (snapshot == null) return null
            val habitId = intent.getLongExtra("habitId", 0)
            val date = intent.getStringExtra("date")
            val userId = intent.getStringExtra("userId")
            val token = intent.getStringExtra("token")
            if (habitId <= 0 || date == null || !WidgetStore.ISO_DATE.matcher(date).matches()) return null
            if (snapshot.optString("date") != date || snapshot.optString("userId") != userId) return null
            if (token == null || snapshot.optString("token") != token) return null
            val habits = snapshot.optJSONArray("habits") ?: return null
            for (i in 0 until habits.length()) {
                val habit = habits.optJSONObject(i)
                if (habit != null && habit.optLong("id") == habitId) return habit
            }
            return null
        }

        /** Redraws every Today widget from the stored snapshot. */
        internal fun refresh(context: Context) {
            val manager = AppWidgetManager.getInstance(context) ?: return
            render(context, manager, manager.getAppWidgetIds(ComponentName(context, TodayWidget::class.java)))
        }

        /**
         * Draws [ids] and sets the midnight alarm from the current snapshot's zone, or with a key stored from the
         * stale snapshot's. With a key, an [update] or a stale snapshot also asks for the day.
         */
        private fun render(context: Context, manager: AppWidgetManager, ids: IntArray?, update: Boolean = false) {
            if (ids == null || ids.isEmpty()) return
            val snapshot = WidgetStore.current(context)
            val stale = snapshot == null && WidgetStore.hasSnapshot(context)
            val ask = (update || stale) && WidgetKey.get(context) != null
            val stored = if (snapshot == null && ask) WidgetStore.stored(context) else snapshot
            if (stored != null) scheduleMidnight(context, stored.optString("timeZone"))
            if (ask) requestDay(context, REQUEST_GAP_MS)
            TodayWidgetViews.update(context, manager, ids, snapshot)
        }

        private fun requestDay(context: Context, gap: Long) {
            if (WidgetStore.claimDayRequest(context, System.currentTimeMillis(), gap)) TodayRefreshJob.schedule(context)
        }

        private fun scheduleMidnight(context: Context, timeZone: String) {
            val alarms = context.getSystemService(AlarmManager::class.java) ?: return
            val midnight = Calendar.getInstance(WidgetStore.zone(timeZone))
            midnight.add(Calendar.DAY_OF_MONTH, 1)
            midnight.set(Calendar.HOUR_OF_DAY, 0)
            midnight.set(Calendar.MINUTE, 0)
            midnight.set(Calendar.SECOND, 5)
            midnight.set(Calendar.MILLISECOND, 0)
            val intent = Intent(context, TodayWidget::class.java).setAction(ACTION_MIDNIGHT)
            alarms.set(AlarmManager.RTC, midnight.timeInMillis, WidgetIntents.broadcast(context, 0, intent))
        }
    }
}
