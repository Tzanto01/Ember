package app.ember.habits

import android.appwidget.AppWidgetManager
import android.content.Context
import android.content.Intent
import android.content.res.ColorStateList
import android.content.res.Configuration
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.util.SizeF
import android.view.View
import android.widget.RemoteViews
import androidx.core.net.toUri
import kotlin.math.max
import kotlin.math.min
import org.json.JSONArray
import org.json.JSONObject

/**
 * The "Today in Ember" widget's RemoteViews: the snapshot laid out for each size by [WidgetPlan], in the language and
 * style (theme, colour scheme, motion) the app last set. Until the app sets a style the widget is Hearthlight, follows
 * the system scheme and stands still.
 */
internal object TodayWidgetViews {

    private const val DEFAULT_WIDTH = 250f
    private const val DEFAULT_HEIGHT = 392f

    /**
     * What every size of one render shares: language, key, unsaved rows, colors (with night ones on 31+, or none to
     * keep the layouts' own day and night resource colors) and motion.
     */
    private class Look(
        val text: Context,
        val keyed: Boolean,
        val unsaved: Set<String>,
        val colors: IntArray,
        val night: IntArray?,
        val resources: Boolean,
        val motion: Boolean,
    )

    /** Draws [snapshot], today's or null when there is none, into the widgets [ids]. */
    @JvmStatic
    fun update(context: Context, manager: AppWidgetManager, ids: IntArray, snapshot: JSONObject?) {
        val look = look(context, snapshot)
        for (id in ids) manager.updateAppWidget(id, views(context, look, snapshot, manager.getAppWidgetOptions(id)))
    }

    private fun look(context: Context, snapshot: JSONObject?): Look {
        val text = AppLanguage.localized(context)
        val keyed = snapshot != null && WidgetKey.get(context) != null && WidgetKey.apiBase(context) != null
        val unsaved = if (snapshot == null) emptySet() else WidgetStore.unsaved(context)
        val prefs = WidgetStore.prefs(context)
        val theme = prefs.getString(WidgetStore.THEME, null)
        val scheme = prefs.getString(WidgetStore.COLOR_SCHEME, null)
        val uiMode = context.resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK
        val system = scheme != "light" && scheme != "dark"
        val resources = Build.VERSION.SDK_INT < 31 && system && WidgetPalette.hearthlight(theme)
        val dayAndNight = Build.VERSION.SDK_INT >= 31 && system
        val colors = if (dayAndNight) {
            WidgetPalette.of(theme, false)
        } else {
            WidgetPalette.of(theme, WidgetPalette.dark(scheme, uiMode == Configuration.UI_MODE_NIGHT_YES))
        }
        val night = if (dayAndNight) WidgetPalette.of(theme, true) else null
        val scale = Settings.Global.getFloat(context.contentResolver, Settings.Global.ANIMATOR_DURATION_SCALE, 1f)
        return Look(text, keyed, unsaved, colors, night, resources, WidgetPlan.motion(prefs.all, scale))
    }

    /**
     * On API 31+ one layout per [WidgetPlan.SIZES] breakpoint, for the launcher to pick; below, the layout for the
     * smallest reported size (landscape height on phones), or the biggest when the launcher reports none.
     */
    private fun views(context: Context, look: Look, snapshot: JSONObject?, options: Bundle?): RemoteViews {
        if (Build.VERSION.SDK_INT >= 31) {
            val sized = HashMap<SizeF, RemoteViews>()
            for (size in WidgetPlan.SIZES) sized[SizeF(size[0], size[1])] = views(context, look, snapshot, size[0], size[1])
            return RemoteViews(sized)
        }
        val width = options?.getInt(AppWidgetManager.OPTION_APPWIDGET_MIN_WIDTH) ?: 0
        val height = options?.getInt(AppWidgetManager.OPTION_APPWIDGET_MIN_HEIGHT) ?: 0
        if (width <= 0 || height <= 0) return views(context, look, snapshot, DEFAULT_WIDTH, DEFAULT_HEIGHT)
        return views(context, look, snapshot, width.toFloat(), height.toFloat())
    }

    private fun views(context: Context, look: Look, snapshot: JSONObject?, width: Float, height: Float): RemoteViews {
        val views = RemoteViews(context.packageName, R.layout.widget_today)
        val text = look.text
        val openToday = Intent(context, MainActivity::class.java).setAction(TodayWidget.ACTION_OPEN_TODAY)
        val today = WidgetIntents.open(context, 0, openToday)
        views.setOnClickPendingIntent(R.id.widget_root, today)
        views.setOnClickPendingIntent(R.id.widget_flame_box, today)
        views.setOnClickPendingIntent(R.id.widget_count, today)
        views.setContentDescription(R.id.widget_flame_box, text.getString(R.string.widget_flame_description))
        tint(views, R.id.widget_fill, "setColorFilter", look, WidgetPalette.BACKGROUND)
        tint(views, R.id.widget_border, "setColorFilter", look, WidgetPalette.BORDER)
        tint(views, R.id.widget_title, "setTextColor", look, WidgetPalette.TEXT)
        tint(views, R.id.widget_count, "setTextColor", look, WidgetPalette.ACCENT)
        tint(views, R.id.widget_dots_done, "setTextColor", look, WidgetPalette.ACCENT)
        tint(views, R.id.widget_dots_open, "setTextColor", look, WidgetPalette.BORDER)
        tint(views, R.id.widget_empty, "setTextColor", look, WidgetPalette.MUTED)
        tint(views, R.id.widget_single_name, "setTextColor", look, WidgetPalette.TEXT)
        tint(views, R.id.widget_more, "setTextColor", look, WidgetPalette.MUTED)
        views.setViewVisibility(R.id.widget_flame, if (look.motion) View.GONE else View.VISIBLE)
        views.removeAllViews(R.id.widget_flicker_box)
        if (look.motion) {
            views.addView(R.id.widget_flicker_box, RemoteViews(context.packageName, R.layout.widget_today_flicker))
        }

        val habits = snapshot?.optJSONArray("habits") ?: JSONArray()
        val states = ArrayList<String>()
        var doneRows = 0
        for (i in 0 until habits.length()) {
            val state = habits.optJSONObject(i)?.optString("state") ?: ""
            if (state == "done") doneRows++
            states.add(state)
        }
        val more = if (snapshot == null) 0 else max(0, snapshot.optInt("more"))
        val total = if (snapshot == null) 0 else max(0, snapshot.optInt("total", habits.length() + more))
        val done = if (snapshot == null) 0 else max(0, min(total, snapshot.optInt("done", doneRows)))
        val plan = WidgetPlan.of(width, height, states, more)
        val single = plan.layout == WidgetPlan.SINGLE
        val empty = snapshot == null || states.isEmpty() && more == 0

        views.setTextViewText(R.id.widget_title, text.getString(R.string.widget_today))
        views.setViewVisibility(R.id.widget_title, if (single) View.GONE else View.VISIBLE)
        views.setViewVisibility(R.id.widget_header_space, if (single) View.VISIBLE else View.GONE)
        views.setViewVisibility(R.id.widget_count, if (total > 0) View.VISIBLE else View.GONE)
        views.setTextViewText(R.id.widget_count, text.getString(R.string.widget_state_partial, done, total))
        val dots = WidgetPlan.dots(plan.layout, width, total)
        views.setViewVisibility(R.id.widget_dots_done, if (dots && done > 0) View.VISIBLE else View.GONE)
        views.setViewVisibility(R.id.widget_dots_open, if (dots && done < total) View.VISIBLE else View.GONE)
        views.setTextViewText(R.id.widget_dots_done, dots(done))
        views.setTextViewText(R.id.widget_dots_open, dots(total - done))
        val message = when {
            snapshot != null -> R.string.widget_nothing_today
            WidgetStore.hasSnapshot(context) -> R.string.widget_stale
            else -> R.string.widget_signed_out
        }
        views.setTextViewText(R.id.widget_empty, text.getString(message))
        views.setViewVisibility(R.id.widget_empty, if (empty) View.VISIBLE else View.GONE)

        views.removeAllViews(R.id.widget_rows)
        views.removeAllViews(R.id.widget_single_circle)
        val list = !empty && !plan.allDone
        views.setViewVisibility(R.id.widget_rows, if (list && !single) View.VISIBLE else View.GONE)
        val singleShown = plan.allDone || list && single && plan.shown > 0
        views.setViewVisibility(R.id.widget_single, if (singleShown) View.VISIBLE else View.GONE)
        if (plan.allDone) {
            val circle = RemoteViews(context.packageName, R.layout.widget_today_big)
            circle.setImageViewResource(R.id.widget_row_circle, R.drawable.widget_done)
            tint(circle, R.id.widget_row_circle, "setColorFilter", look, WidgetPalette.DONE)
            views.addView(R.id.widget_single_circle, circle)
            views.setTextViewText(R.id.widget_single_name, text.getString(R.string.widget_all_done))
        }
        if (list) {
            for (i in plan.start until plan.start + plan.shown) {
                val habit = habits.optJSONObject(i) ?: continue
                if (single) {
                    views.addView(R.id.widget_single_circle, row(context, look, snapshot, habit, true))
                    views.setTextViewText(R.id.widget_single_name, habit.optString("name"))
                } else {
                    views.addView(R.id.widget_rows, row(context, look, snapshot, habit, false))
                }
            }
        }
        views.setViewVisibility(R.id.widget_more, if (list && plan.more > 0) View.VISIBLE else View.GONE)
        views.setTextViewText(R.id.widget_more, text.getString(R.string.widget_more, plan.more))
        return views
    }

    /**
     * One habit: a row with its name, or (for [WidgetPlan.SINGLE], [big]) its circle alone. On API 31+ with a widget
     * key, an open or partial habit's circle is a CheckBox that checks in, unchecked on every render; without a key
     * it is a plain circle that opens the app to check in. A done or rest circle opens the habit. Below 31 the whole
     * row (or the big circle) does what today's row taps do.
     */
    private fun row(context: Context, look: Look, snapshot: JSONObject, habit: JSONObject, big: Boolean): RemoteViews {
        val text = look.text
        val state = habit.optString("state")
        val checkIn = WidgetPlan.checkIn(state)
        val check = checkIn && look.keyed && Build.VERSION.SDK_INT >= 31
        val layout = when {
            big && check -> R.layout.widget_today_big_check
            big -> R.layout.widget_today_big
            check -> R.layout.widget_today_row_check
            else -> R.layout.widget_today_row
        }
        val row = RemoteViews(context.packageName, layout)
        val name = habit.optString("name")
        val habitId = habit.optLong("id")
        val userId = snapshot.optString("userId")
        val date = snapshot.optString("date")
        var label = stateLabel(text, habit)
        val unsaved = WidgetStore.pendingKey(userId, habitId, date) in look.unsaved
        if (unsaved) label = text.getString(R.string.widget_not_saved, label)
        if (!big) {
            row.setTextViewText(R.id.widget_row_name, name)
            val nameColor = if (state == "done") WidgetPalette.MUTED else WidgetPalette.TEXT
            tint(row, R.id.widget_row_name, "setTextColor", look, nameColor)
            row.setTextViewText(R.id.widget_row_state, label)
            tint(row, R.id.widget_row_state, "setTextColor", look, WidgetPalette.MUTED)
            val labelled = unsaved || state == "partial" || state == "rest"
            row.setViewVisibility(R.id.widget_row_state, if (labelled) View.VISIBLE else View.GONE)
        }
        val background = checkIn && look.keyed
        val tap = Intent(context, if (background) TodayWidget::class.java else MainActivity::class.java)
            .setAction(if (checkIn) TodayWidget.ACTION_CHECK_IN else TodayWidget.ACTION_OPEN_HABIT)
            .setData(WidgetPlan.uri(checkIn, habitId, date).toUri())
            .putExtra("habitId", habitId)
            .putExtra("date", date)
            .putExtra("userId", userId)
            .putExtra("token", snapshot.optString("token"))
        val pending = if (background) WidgetIntents.broadcast(context, 1, tap) else WidgetIntents.open(context, 1, tap)
        val target = if (big || Build.VERSION.SDK_INT >= 31) R.id.widget_row_circle else R.id.widget_row
        val description = if (checkIn) R.string.widget_row_description else R.string.widget_row_open_description
        row.setContentDescription(target, text.getString(description, name, label))
        if (check) {
            row.setCompoundButtonChecked(R.id.widget_row_circle, false)
            if (state == "partial") {
                row.setInt(R.id.widget_row_circle, "setBackgroundResource", R.drawable.widget_check_partial)
            }
            val night = look.night
            if (night == null) {
                row.setColorStateList(R.id.widget_row_circle, "setBackgroundTintList", checkTint(look.colors))
            } else {
                row.setColorStateList(
                    R.id.widget_row_circle,
                    "setBackgroundTintList",
                    checkTint(look.colors),
                    checkTint(night),
                )
            }
            row.setOnCheckedChangeResponse(R.id.widget_row_circle, RemoteViews.RemoteResponse.fromPendingIntent(pending))
            return row
        }
        val circle = when (state) {
            "done" -> R.drawable.widget_done
            "partial" -> R.drawable.widget_ring_partial
            else -> R.drawable.widget_ring
        }
        row.setImageViewResource(R.id.widget_row_circle, circle)
        val circleColor = when (state) {
            "done" -> WidgetPalette.DONE
            "rest" -> WidgetPalette.MUTED
            else -> WidgetPalette.ACCENT
        }
        tint(row, R.id.widget_row_circle, "setColorFilter", look, circleColor)
        row.setOnClickPendingIntent(target, pending)
        return row
    }

    /**
     * Sets a color through [method]: as a day and night pair on API 31+ when following the system, none when keeping
     * resource colors, else fixed.
     */
    private fun tint(views: RemoteViews, id: Int, method: String, look: Look, color: Int) {
        if (look.resources) return
        val night = look.night
        if (night != null && Build.VERSION.SDK_INT >= 31) {
            views.setColorInt(id, method, look.colors[color], night[color])
        } else {
            views.setInt(id, method, look.colors[color])
        }
    }

    private fun checkTint(colors: IntArray) = ColorStateList(
        arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()),
        intArrayOf(colors[WidgetPalette.DONE], colors[WidgetPalette.ACCENT]),
    )

    private fun dots(n: Int) = "●".repeat(n)

    private fun stateLabel(context: Context, habit: JSONObject): String = when (habit.optString("state")) {
        "done" -> context.getString(R.string.widget_state_done)
        "partial" -> context.getString(R.string.widget_state_partial, habit.optInt("count"), habit.optInt("timesPerDay"))
        "rest" -> context.getString(R.string.widget_state_rest)
        else -> context.getString(R.string.widget_state_open)
    }
}
