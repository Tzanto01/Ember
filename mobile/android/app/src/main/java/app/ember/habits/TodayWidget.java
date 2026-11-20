package app.ember.habits;

import android.app.AlarmManager;
import android.app.PendingIntent;
import android.appwidget.AppWidgetManager;
import android.appwidget.AppWidgetProvider;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.res.ColorStateList;
import android.content.res.Configuration;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.SystemClock;
import android.provider.Settings;
import android.util.SizeF;
import android.view.View;
import android.widget.RemoteViews;
import com.getcapacitor.JSArray;
import com.getcapacitor.JSObject;
import java.util.Calendar;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TimeZone;
import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

/**
 * The "Today in Ember" home-screen widget. It shows only the snapshot the app last pushed (app-private
 * SharedPreferences), and only while that snapshot (or the next day it carries) is for today in the account's zone,
 * laid out for the widget's size by {@link WidgetPlan} in the style the app last set (theme, colour scheme, motion).
 * Every row action carries the habit, date, user and a per-snapshot token in an explicit, immutable intent with its
 * own data Uri. With a widget key stored, checking an open or partial row (a CheckBox on API 31+, the row tap below)
 * is a broadcast to this receiver, which checks in at once and posts it with the key; without one it opens
 * {@link MainActivity}, which hands the check-in to JS. A done or rest row's circle opens Today at that habit, never
 * undoing it; anywhere else opens Today.
 */
public class TodayWidget extends AppWidgetProvider {

    static final String ACTION_CHECK_IN = "app.ember.habits.WIDGET_CHECK_IN";
    static final String ACTION_OPEN_TODAY = "app.ember.habits.WIDGET_OPEN_TODAY";
    static final String ACTION_OPEN_HABIT = "app.ember.habits.WIDGET_OPEN_HABIT";
    private static final String ACTION_MIDNIGHT = "app.ember.habits.WIDGET_MIDNIGHT";
    private static final long BUDGET_MS = 8000;
    private static final float DEFAULT_WIDTH = 250;
    private static final float DEFAULT_HEIGHT = 392;

    @Override
    public void onUpdate(Context context, AppWidgetManager manager, int[] ids) {
        render(context, manager, ids);
    }

    @Override
    public void onAppWidgetOptionsChanged(Context context, AppWidgetManager manager, int id, Bundle options) {
        render(context, manager, new int[] { id });
    }

    /**
     * Redraws at the alarm set for the snapshot zone's next midnight, so yesterday's states never linger, and
     * handles a row's background check-in.
     */
    @Override
    public void onReceive(Context context, Intent intent) {
        if (ACTION_MIDNIGHT.equals(intent.getAction())) refresh(context);
        else if (ACTION_CHECK_IN.equals(intent.getAction())) checkIn(context, intent);
        else super.onReceive(context, intent);
    }

    /**
     * Checks in a validated open or partial row: the stored row moves to {@code min(count + 1, timesPerDay)} and a
     * pending entry for that count is saved before the POST ({@link CheckInClient#send}), so a failed or killed request
     * is replayed by JS. The request must finish within {@link #BUDGET_MS} of the tap, or it is dropped and stays
     * pending. Any other tap only redraws.
     */
    private void checkIn(Context context, Intent intent) {
        long deadline = SystemClock.elapsedRealtime() + BUDGET_MS;
        JSONObject snapshot = WidgetStore.current(context);
        String key = WidgetKey.get(context);
        String apiBase = WidgetKey.apiBase(context);
        JSONObject habit = ours(context, intent, TodayWidget.class) ? listed(snapshot, intent) : null;
        if (habit == null || key == null || apiBase == null) {
            refresh(context);
            return;
        }
        String token = snapshot.optString("token");
        String userId = snapshot.optString("userId");
        String date = snapshot.optString("date");
        long habitId = habit.optLong("id");
        int[] target = { 0 };
        boolean tapped;
        synchronized (WidgetStore.LOCK) {
            tapped = WidgetStore.editRow(context, token, date, habitId, row -> {
                String state = row.optString("state");
                if (!"open".equals(state) && !"partial".equals(state)) return false;
                int timesPerDay = row.optInt("timesPerDay");
                target[0] = Math.min(row.optInt("count") + 1, timesPerDay);
                row.put("count", target[0]).put("state", target[0] >= timesPerDay ? "done" : "partial");
                return true;
            });
            if (tapped) {
                WidgetStore.addPending(context, userId, habitId, date, target[0]);
                WidgetStore.inFlight(userId, habitId, date, 1);
            }
        }
        if (!tapped) {
            refresh(context);
            return;
        }
        Context app = context.getApplicationContext();
        PendingResult result = null;
        try {
            refresh(context);
            result = goAsync();
            PendingResult async = result;
            CheckInClient.queue(() -> {
                try {
                    try {
                        CheckInClient.send(app, apiBase, key, token, userId, habitId, date, deadline);
                    } finally {
                        WidgetStore.inFlight(userId, habitId, date, -1);
                    }
                    refresh(app);
                } finally {
                    async.finish();
                }
            });
        } catch (RuntimeException e) {
            WidgetStore.inFlight(userId, habitId, date, -1);
            if (result != null) result.finish();
            throw e;
        }
    }

    /**
     * Validates and stores a snapshot with a fresh tap token ({@link WidgetStore#save}), then redraws every widget.
     */
    static void save(
        Context context,
        String userId,
        String date,
        String timeZone,
        JSONArray habits,
        int more,
        Integer done,
        Integer total,
        JSONObject next
    ) throws JSONException {
        WidgetStore.save(context, userId, date, timeZone, habits, more, done, total, next);
        refresh(context);
    }

    /** Returns and removes the pending widget check-ins JS may replay ({@link WidgetStore#takePending}), then redraws. */
    static JSArray takePending(Context context) {
        JSArray result = WidgetStore.takePending(context);
        refresh(context);
        return result;
    }

    /**
     * Forgets the snapshot, widget key, API origin and pending check-ins (sign-out, session end, account deletion),
     * keeping the style, and redraws the signed-out state.
     */
    static void clear(Context context) {
        WidgetStore.clear(context);
        refresh(context);
    }

    /**
     * The JS payload for a widget intent, or null to ignore it. "Open Today" yields an empty object. A check-in
     * or an open-habit tap needs our component and a row {@link #listed} in the current snapshot, so a stale row
     * or an intent from another app does nothing. An open-habit tap carries {@code open: true}.
     */
    static JSObject validTap(Context context, Intent intent) {
        if (!ours(context, intent, MainActivity.class)) return null;
        if (ACTION_OPEN_TODAY.equals(intent.getAction())) return new JSObject();
        boolean open = ACTION_OPEN_HABIT.equals(intent.getAction());
        if (!open && !ACTION_CHECK_IN.equals(intent.getAction())) return null;
        JSONObject habit = listed(WidgetStore.current(context), intent);
        if (habit == null) return null;
        JSObject tap = new JSObject();
        tap.put("habitId", habit.optLong("id"));
        tap.put("date", intent.getStringExtra("date"));
        tap.put("userId", intent.getStringExtra("userId"));
        if (open) tap.put("open", true);
        return tap;
    }

    private static boolean ours(Context context, Intent intent, Class<?> target) {
        ComponentName component = intent.getComponent();
        if (component == null || !component.getPackageName().equals(context.getPackageName())) return false;
        return target.getName().equals(component.getClassName());
    }

    /**
     * The snapshot row a tap names, or null: a habit id above 0 that is in {@code snapshot}, the snapshot's date
     * (ISO form) and user, and the snapshot's tap token.
     */
    private static JSONObject listed(JSONObject snapshot, Intent intent) {
        if (snapshot == null) return null;
        long habitId = intent.getLongExtra("habitId", 0);
        String date = intent.getStringExtra("date");
        String userId = intent.getStringExtra("userId");
        String token = intent.getStringExtra("token");
        if (habitId <= 0 || date == null || !WidgetStore.ISO_DATE.matcher(date).matches()) return null;
        if (!snapshot.optString("date").equals(date) || !snapshot.optString("userId").equals(userId)) return null;
        if (token == null || !snapshot.optString("token").equals(token)) return null;
        JSONArray habits = snapshot.optJSONArray("habits");
        for (int i = 0; habits != null && i < habits.length(); i++) {
            JSONObject habit = habits.optJSONObject(i);
            if (habit != null && habit.optLong("id") == habitId) return habit;
        }
        return null;
    }

    static void refresh(Context context) {
        AppWidgetManager manager = AppWidgetManager.getInstance(context);
        if (manager == null) return;
        render(context, manager, manager.getAppWidgetIds(new ComponentName(context, TodayWidget.class)));
    }

    /**
     * Stores the app's widget style ({@code theme}, {@code colorScheme}, {@code motion}) and redraws. Until the first
     * call the widget is Hearthlight, follows the system scheme and stands still.
     */
    static void setStyle(Context context, String theme, boolean motion, String colorScheme) {
        WidgetStore.setStyle(context, theme, motion, colorScheme);
        refresh(context);
    }

    /**
     * What every size of one render shares: language, key, unsaved rows, colors (with night ones on 31+, or none to
     * keep the layouts' own day and night resource colors) and motion.
     */
    private static final class Look {
        Context text;
        boolean keyed;
        Set<String> unsaved;
        int[] colors;
        int[] night;
        boolean resources;
        boolean motion;
    }

    private static Look look(Context context, JSONObject snapshot) {
        Look look = new Look();
        look.text = AppLanguage.localized(context);
        look.keyed = snapshot != null && WidgetKey.get(context) != null && WidgetKey.apiBase(context) != null;
        look.unsaved = snapshot == null ? new HashSet<>() : WidgetStore.unsaved(context);
        SharedPreferences prefs = WidgetStore.prefs(context);
        String theme = prefs.getString(WidgetStore.THEME, null);
        String scheme = prefs.getString(WidgetStore.COLOR_SCHEME, null);
        int uiMode = context.getResources().getConfiguration().uiMode & Configuration.UI_MODE_NIGHT_MASK;
        boolean system = !"light".equals(scheme) && !"dark".equals(scheme);
        look.resources = Build.VERSION.SDK_INT < 31 && system && WidgetPalette.hearthlight(theme);
        if (Build.VERSION.SDK_INT >= 31 && system) {
            look.colors = WidgetPalette.of(theme, false);
            look.night = WidgetPalette.of(theme, true);
        } else {
            look.colors = WidgetPalette.of(theme, WidgetPalette.dark(scheme, uiMode == Configuration.UI_MODE_NIGHT_YES));
        }
        float scale = Settings.Global.getFloat(context.getContentResolver(), Settings.Global.ANIMATOR_DURATION_SCALE, 1f);
        look.motion = WidgetPlan.motion(prefs.getAll(), scale);
        return look;
    }

    private static void render(Context context, AppWidgetManager manager, int[] ids) {
        if (ids == null || ids.length == 0) return;
        JSONObject snapshot = WidgetStore.current(context);
        if (snapshot != null) scheduleMidnight(context, snapshot.optString("timeZone"));
        Look look = look(context, snapshot);
        for (int id : ids) manager.updateAppWidget(id, views(context, look, snapshot, manager.getAppWidgetOptions(id)));
    }

    /**
     * On API 31+ one layout per {@link WidgetPlan#SIZES} breakpoint, for the launcher to pick; below, the layout for
     * the smallest reported size (landscape height on phones), or the biggest when the launcher reports none.
     */
    private static RemoteViews views(Context context, Look look, JSONObject snapshot, Bundle options) {
        if (Build.VERSION.SDK_INT >= 31) {
            Map<SizeF, RemoteViews> sized = new HashMap<>();
            for (float[] size : WidgetPlan.SIZES) {
                sized.put(new SizeF(size[0], size[1]), views(context, look, snapshot, size[0], size[1]));
            }
            return new RemoteViews(sized);
        }
        int width = options == null ? 0 : options.getInt(AppWidgetManager.OPTION_APPWIDGET_MIN_WIDTH);
        int height = options == null ? 0 : options.getInt(AppWidgetManager.OPTION_APPWIDGET_MIN_HEIGHT);
        if (width <= 0 || height <= 0) return views(context, look, snapshot, DEFAULT_WIDTH, DEFAULT_HEIGHT);
        return views(context, look, snapshot, width, height);
    }

    private static RemoteViews views(Context context, Look look, JSONObject snapshot, float width, float height) {
        RemoteViews views = new RemoteViews(context.getPackageName(), R.layout.widget_today);
        Context text = look.text;
        Intent openToday = new Intent(context, MainActivity.class).setAction(ACTION_OPEN_TODAY);
        PendingIntent today = WidgetIntents.open(context, 0, openToday);
        views.setOnClickPendingIntent(R.id.widget_root, today);
        views.setOnClickPendingIntent(R.id.widget_flame_box, today);
        views.setOnClickPendingIntent(R.id.widget_count, today);
        views.setContentDescription(R.id.widget_flame_box, text.getString(R.string.widget_flame_description));
        tint(views, R.id.widget_fill, "setColorFilter", look, WidgetPalette.BACKGROUND);
        tint(views, R.id.widget_border, "setColorFilter", look, WidgetPalette.BORDER);
        tint(views, R.id.widget_title, "setTextColor", look, WidgetPalette.TEXT);
        tint(views, R.id.widget_count, "setTextColor", look, WidgetPalette.ACCENT);
        tint(views, R.id.widget_dots_done, "setTextColor", look, WidgetPalette.ACCENT);
        tint(views, R.id.widget_dots_open, "setTextColor", look, WidgetPalette.BORDER);
        tint(views, R.id.widget_empty, "setTextColor", look, WidgetPalette.MUTED);
        tint(views, R.id.widget_single_name, "setTextColor", look, WidgetPalette.TEXT);
        tint(views, R.id.widget_more, "setTextColor", look, WidgetPalette.MUTED);
        views.setViewVisibility(R.id.widget_flame, look.motion ? View.GONE : View.VISIBLE);
        views.removeAllViews(R.id.widget_flicker_box);
        if (look.motion) {
            views.addView(R.id.widget_flicker_box, new RemoteViews(context.getPackageName(), R.layout.widget_today_flicker));
        }

        JSONArray habits = snapshot == null ? null : snapshot.optJSONArray("habits");
        if (habits == null) habits = new JSONArray();
        List<String> states = new ArrayList<>();
        int doneRows = 0;
        for (int i = 0; i < habits.length(); i++) {
            JSONObject habit = habits.optJSONObject(i);
            String state = habit == null ? "" : habit.optString("state");
            if ("done".equals(state)) doneRows++;
            states.add(state);
        }
        int more = snapshot == null ? 0 : Math.max(0, snapshot.optInt("more"));
        int total = snapshot == null ? 0 : Math.max(0, snapshot.optInt("total", habits.length() + more));
        int done = snapshot == null ? 0 : Math.max(0, Math.min(total, snapshot.optInt("done", doneRows)));
        WidgetPlan plan = WidgetPlan.of(width, height, states, more);
        boolean single = plan.layout == WidgetPlan.SINGLE;
        boolean empty = snapshot == null || states.isEmpty() && more == 0;

        views.setTextViewText(R.id.widget_title, text.getString(R.string.widget_today));
        views.setViewVisibility(R.id.widget_title, single ? View.GONE : View.VISIBLE);
        views.setViewVisibility(R.id.widget_header_space, single ? View.VISIBLE : View.GONE);
        views.setViewVisibility(R.id.widget_count, total > 0 ? View.VISIBLE : View.GONE);
        views.setTextViewText(R.id.widget_count, text.getString(R.string.widget_state_partial, done, total));
        boolean dots = WidgetPlan.dots(plan.layout, width, total);
        views.setViewVisibility(R.id.widget_dots_done, dots && done > 0 ? View.VISIBLE : View.GONE);
        views.setViewVisibility(R.id.widget_dots_open, dots && done < total ? View.VISIBLE : View.GONE);
        views.setTextViewText(R.id.widget_dots_done, dots(done));
        views.setTextViewText(R.id.widget_dots_open, dots(total - done));
        views.setTextViewText(
            R.id.widget_empty,
            text.getString(
                snapshot != null
                    ? R.string.widget_nothing_today
                    : WidgetStore.hasSnapshot(context) ? R.string.widget_stale : R.string.widget_signed_out
            )
        );
        views.setViewVisibility(R.id.widget_empty, empty ? View.VISIBLE : View.GONE);

        views.removeAllViews(R.id.widget_rows);
        views.removeAllViews(R.id.widget_single_circle);
        boolean list = !empty && !plan.allDone;
        views.setViewVisibility(R.id.widget_rows, list && !single ? View.VISIBLE : View.GONE);
        views.setViewVisibility(
            R.id.widget_single,
            plan.allDone || list && single && plan.shown > 0 ? View.VISIBLE : View.GONE
        );
        if (plan.allDone) {
            RemoteViews circle = new RemoteViews(context.getPackageName(), R.layout.widget_today_big);
            circle.setImageViewResource(R.id.widget_row_circle, R.drawable.widget_done);
            tint(circle, R.id.widget_row_circle, "setColorFilter", look, WidgetPalette.DONE);
            views.addView(R.id.widget_single_circle, circle);
            views.setTextViewText(R.id.widget_single_name, text.getString(R.string.widget_all_done));
        }
        for (int i = plan.start; list && i < plan.start + plan.shown; i++) {
            JSONObject habit = habits.optJSONObject(i);
            if (habit == null) continue;
            if (single) {
                views.addView(R.id.widget_single_circle, row(context, look, snapshot, habit, true));
                views.setTextViewText(R.id.widget_single_name, habit.optString("name"));
            } else {
                views.addView(R.id.widget_rows, row(context, look, snapshot, habit, false));
            }
        }
        views.setViewVisibility(R.id.widget_more, list && plan.more > 0 ? View.VISIBLE : View.GONE);
        views.setTextViewText(R.id.widget_more, text.getString(R.string.widget_more, plan.more));
        return views;
    }

    /**
     * One habit: a row with its name, or (for {@link WidgetPlan#SINGLE}, {@code big}) its circle alone. On API 31+
     * with a widget key, an open or partial habit's circle is a CheckBox that checks in, unchecked on every render;
     * without a key it is a plain circle that opens the app to check in. A done or rest circle opens the habit. Below
     * 31 the whole row (or the big circle) does what today's row taps do.
     */
    private static RemoteViews row(Context context, Look look, JSONObject snapshot, JSONObject habit, boolean big) {
        Context text = look.text;
        String state = habit.optString("state");
        boolean checkIn = WidgetPlan.checkIn(state);
        boolean check = checkIn && look.keyed && Build.VERSION.SDK_INT >= 31;
        int layout = big
            ? check ? R.layout.widget_today_big_check : R.layout.widget_today_big
            : check ? R.layout.widget_today_row_check : R.layout.widget_today_row;
        RemoteViews row = new RemoteViews(context.getPackageName(), layout);
        String name = habit.optString("name");
        long habitId = habit.optLong("id");
        String userId = snapshot.optString("userId");
        String date = snapshot.optString("date");
        String label = stateLabel(text, habit);
        boolean unsaved = look.unsaved.contains(WidgetStore.pendingKey(userId, habitId, date));
        if (unsaved) label = text.getString(R.string.widget_not_saved, label);
        if (!big) {
            row.setTextViewText(R.id.widget_row_name, name);
            int nameColor = "done".equals(state) ? WidgetPalette.MUTED : WidgetPalette.TEXT;
            tint(row, R.id.widget_row_name, "setTextColor", look, nameColor);
            row.setTextViewText(R.id.widget_row_state, label);
            tint(row, R.id.widget_row_state, "setTextColor", look, WidgetPalette.MUTED);
            boolean labelled = unsaved || "partial".equals(state) || "rest".equals(state);
            row.setViewVisibility(R.id.widget_row_state, labelled ? View.VISIBLE : View.GONE);
        }
        boolean background = checkIn && look.keyed;
        Intent tap = new Intent(context, background ? TodayWidget.class : MainActivity.class)
            .setAction(checkIn ? ACTION_CHECK_IN : ACTION_OPEN_HABIT)
            .setData(Uri.parse(WidgetPlan.uri(checkIn, habitId, date)))
            .putExtra("habitId", habitId)
            .putExtra("date", date)
            .putExtra("userId", userId)
            .putExtra("token", snapshot.optString("token"));
        PendingIntent pending = background ? WidgetIntents.broadcast(context, 1, tap) : WidgetIntents.open(context, 1, tap);
        int target = big || Build.VERSION.SDK_INT >= 31 ? R.id.widget_row_circle : R.id.widget_row;
        row.setContentDescription(
            target,
            text.getString(checkIn ? R.string.widget_row_description : R.string.widget_row_open_description, name, label)
        );
        if (check) {
            row.setCompoundButtonChecked(R.id.widget_row_circle, false);
            if ("partial".equals(state)) {
                row.setInt(R.id.widget_row_circle, "setBackgroundResource", R.drawable.widget_check_partial);
            }
            if (look.night == null) {
                row.setColorStateList(R.id.widget_row_circle, "setBackgroundTintList", checkTint(look.colors));
            } else {
                row.setColorStateList(
                    R.id.widget_row_circle,
                    "setBackgroundTintList",
                    checkTint(look.colors),
                    checkTint(look.night)
                );
            }
            row.setOnCheckedChangeResponse(R.id.widget_row_circle, RemoteViews.RemoteResponse.fromPendingIntent(pending));
            return row;
        }
        row.setImageViewResource(
            R.id.widget_row_circle,
            "done".equals(state)
                ? R.drawable.widget_done
                : "partial".equals(state) ? R.drawable.widget_ring_partial : R.drawable.widget_ring
        );
        tint(
            row,
            R.id.widget_row_circle,
            "setColorFilter",
            look,
            "done".equals(state) ? WidgetPalette.DONE : "rest".equals(state) ? WidgetPalette.MUTED : WidgetPalette.ACCENT
        );
        row.setOnClickPendingIntent(target, pending);
        return row;
    }

    /**
     * Sets a color through {@code method}: as a day and night pair on API 31+ when following the system, none when
     * keeping resource colors, else fixed.
     */
    private static void tint(RemoteViews views, int id, String method, Look look, int color) {
        if (look.resources) return;
        if (look.night != null && Build.VERSION.SDK_INT >= 31) {
            views.setColorInt(id, method, look.colors[color], look.night[color]);
        } else {
            views.setInt(id, method, look.colors[color]);
        }
    }

    private static ColorStateList checkTint(int[] colors) {
        return new ColorStateList(
            new int[][] { { android.R.attr.state_checked }, {} },
            new int[] { colors[WidgetPalette.DONE], colors[WidgetPalette.ACCENT] }
        );
    }

    private static String dots(int n) {
        StringBuilder dots = new StringBuilder();
        for (int i = 0; i < n; i++) dots.append('●');
        return dots.toString();
    }

    private static void scheduleMidnight(Context context, String timeZone) {
        AlarmManager alarms = context.getSystemService(AlarmManager.class);
        if (alarms == null) return;
        Calendar midnight = Calendar.getInstance(TimeZone.getTimeZone(timeZone));
        midnight.add(Calendar.DAY_OF_MONTH, 1);
        midnight.set(Calendar.HOUR_OF_DAY, 0);
        midnight.set(Calendar.MINUTE, 0);
        midnight.set(Calendar.SECOND, 5);
        midnight.set(Calendar.MILLISECOND, 0);
        Intent intent = new Intent(context, TodayWidget.class).setAction(ACTION_MIDNIGHT);
        alarms.set(AlarmManager.RTC, midnight.getTimeInMillis(), WidgetIntents.broadcast(context, 0, intent));
    }

    private static String stateLabel(Context context, JSONObject habit) {
        switch (habit.optString("state")) {
            case "done":
                return context.getString(R.string.widget_state_done);
            case "partial":
                return context.getString(R.string.widget_state_partial, habit.optInt("count"), habit.optInt("timesPerDay"));
            case "rest":
                return context.getString(R.string.widget_state_rest);
            default:
                return context.getString(R.string.widget_state_open);
        }
    }
}
