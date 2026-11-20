package app.ember.habits;

import android.app.AlarmManager;
import android.appwidget.AppWidgetManager;
import android.appwidget.AppWidgetProvider;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.os.Bundle;
import android.os.SystemClock;
import com.getcapacitor.JSArray;
import com.getcapacitor.JSObject;
import java.util.Calendar;
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

    private static void render(Context context, AppWidgetManager manager, int[] ids) {
        if (ids == null || ids.length == 0) return;
        JSONObject snapshot = WidgetStore.current(context);
        if (snapshot != null) scheduleMidnight(context, snapshot.optString("timeZone"));
        TodayWidgetViews.update(context, manager, ids, snapshot);
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
}
