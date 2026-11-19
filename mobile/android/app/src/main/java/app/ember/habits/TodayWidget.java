package app.ember.habits;

import android.app.AlarmManager;
import android.app.PendingIntent;
import android.appwidget.AppWidgetManager;
import android.appwidget.AppWidgetProvider;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.os.Bundle;
import android.os.SystemClock;
import android.view.View;
import android.widget.RemoteViews;
import com.getcapacitor.JSArray;
import com.getcapacitor.JSObject;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;
import java.text.SimpleDateFormat;
import java.util.Calendar;
import java.util.Date;
import java.util.Locale;
import java.util.Arrays;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TimeZone;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;
import java.util.concurrent.atomic.AtomicReference;
import java.util.regex.Pattern;
import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

/**
 * The "Today in Ember" home-screen widget. It shows only the snapshot the app last pushed (app-private
 * SharedPreferences), and only while that snapshot (or the next day it carries) is for today in the account's zone,
 * with as many rows as the widget's height fits. Every row tap carries the habit, date, user and a per-snapshot
 * token in an explicit, immutable intent. With a widget key stored, an open or partial row is a broadcast to this
 * receiver, which checks in at once and posts it with the key; without one it opens {@link MainActivity}, which
 * hands the check-in to JS. A done or rest row opens Today at that habit; anywhere else opens Today.
 */
public class TodayWidget extends AppWidgetProvider {

    static final String ACTION_CHECK_IN = "app.ember.habits.WIDGET_CHECK_IN";
    static final String ACTION_OPEN_TODAY = "app.ember.habits.WIDGET_OPEN_TODAY";
    static final String ACTION_OPEN_HABIT = "app.ember.habits.WIDGET_OPEN_HABIT";
    private static final String ACTION_MIDNIGHT = "app.ember.habits.WIDGET_MIDNIGHT";
    static final int MAX_ROWS = 4;

    private static final String PREFS = "ember_widget";
    private static final String SNAPSHOT = "snapshot";
    private static final String PENDING = "pending";
    private static final int TIMEOUT_MS = 4000;
    private static final long BUDGET_MS = 8000;
    private static final long MIN_REQUEST_MS = 1000;
    private static final int MAX_ANSWER = 4096;
    private static final ExecutorService NETWORK = Executors.newSingleThreadExecutor();
    private static final ExecutorService CALLS = Executors.newCachedThreadPool();
    private static final Map<String, Integer> IN_FLIGHT = new HashMap<>();
    private static final Pattern ISO_DATE = Pattern.compile("^\\d{4}-\\d{2}-\\d{2}$");
    private static final List<String> STATES = Arrays.asList("done", "partial", "open", "rest");
    private static final int MAX_NAME = 100;
    private static final int ROW_DP = 48;
    private static final int CHROME_DP = 92;

    private static final int[] ROWS = {
        R.id.widget_row_0, R.id.widget_row_1, R.id.widget_row_2, R.id.widget_row_3,
    };
    private static final int[] NAMES = {
        R.id.widget_name_0, R.id.widget_name_1, R.id.widget_name_2, R.id.widget_name_3,
    };
    private static final int[] STATE_TEXTS = {
        R.id.widget_state_0, R.id.widget_state_1, R.id.widget_state_2, R.id.widget_state_3,
    };

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
     * pending entry for that count is saved before the POST, so a failed or killed request is replayed by JS. The
     * request must finish within {@link #BUDGET_MS} of the tap, or it is dropped and stays pending. Any other tap
     * only redraws.
     */
    private void checkIn(Context context, Intent intent) {
        long deadline = SystemClock.elapsedRealtime() + BUDGET_MS;
        JSONObject snapshot = current(context);
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
        synchronized (TodayWidget.class) {
            tapped = editRow(context, token, date, habitId, row -> {
                String state = row.optString("state");
                if (!"open".equals(state) && !"partial".equals(state)) return false;
                int timesPerDay = row.optInt("timesPerDay");
                target[0] = Math.min(row.optInt("count") + 1, timesPerDay);
                row.put("count", target[0]).put("state", target[0] >= timesPerDay ? "done" : "partial");
                return true;
            });
            if (tapped) {
                addPending(context, userId, habitId, date, target[0]);
                inFlight(userId, habitId, date, 1);
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
            NETWORK.execute(() -> {
                try {
                    try {
                        send(app, apiBase, key, token, userId, habitId, date, deadline);
                    } finally {
                        inFlight(userId, habitId, date, -1);
                    }
                    refresh(app);
                } finally {
                    async.finish();
                }
            });
        } catch (RuntimeException e) {
            inFlight(userId, habitId, date, -1);
            if (result != null) result.finish();
            throw e;
        }
    }

    /**
     * Runs {@link #post} on its own thread and waits for it until {@code deadline}, disconnecting on timeout; skips
     * it when less than {@link #MIN_REQUEST_MS} is left. A valid answer settles the pending entry and updates the row
     * while the snapshot is unchanged and no other request for that row is in flight or queued. Callers hold this
     * request's in-flight mark.
     */
    private static void send(
        Context context,
        String apiBase,
        String key,
        String token,
        String userId,
        long habitId,
        String date,
        long deadline
    ) {
        long remaining = deadline - SystemClock.elapsedRealtime();
        if (remaining < MIN_REQUEST_MS) return;
        AtomicReference<HttpURLConnection> open = new AtomicReference<>();
        Future<JSONObject> call = CALLS.submit(() -> post(context, apiBase, key, habitId, date, deadline, open));
        JSONObject answer;
        try {
            answer = call.get(remaining, TimeUnit.MILLISECONDS);
        } catch (ExecutionException | TimeoutException | InterruptedException e) {
            call.cancel(true);
            HttpURLConnection connection = open.get();
            if (connection != null) connection.disconnect();
            if (e instanceof InterruptedException) Thread.currentThread().interrupt();
            return;
        }
        if (answer == null) return;
        String state = answer.optString("state");
        int count = answer.optInt("count");
        int timesPerDay = answer.optInt("timesPerDay");
        synchronized (TodayWidget.class) {
            if (IN_FLIGHT.getOrDefault(pendingKey(userId, habitId, date), 0) <= 1) {
                editRow(context, token, date, habitId, row -> {
                    row.put("state", state).put("count", Math.min(count, timesPerDay)).put("timesPerDay", timesPerDay);
                    return true;
                });
            }
            settlePending(context, userId, habitId, date, count);
        }
    }

    /**
     * POSTs one check-in with the widget key, each timeout capped by what is left until {@code deadline}, and
     * publishes the connection in {@code open} so a timed-out caller can disconnect it. Returns a validated 200
     * answer, else null; a 401 erases the key.
     */
    private static JSONObject post(
        Context context,
        String apiBase,
        String key,
        long habitId,
        String date,
        long deadline,
        AtomicReference<HttpURLConnection> open
    ) {
        HttpURLConnection connection = null;
        try {
            byte[] body = new JSONObject().put("habitId", habitId).put("date", date).toString().getBytes(StandardCharsets.UTF_8);
            connection = (HttpURLConnection) new URL(apiBase + "/api/widget/check-in").openConnection();
            open.set(connection);
            int timeout = (int) Math.max(1, Math.min(TIMEOUT_MS, deadline - SystemClock.elapsedRealtime()));
            connection.setConnectTimeout(timeout);
            connection.setReadTimeout(timeout);
            connection.setInstanceFollowRedirects(false);
            connection.setUseCaches(false);
            connection.setRequestMethod("POST");
            connection.setDoOutput(true);
            connection.setFixedLengthStreamingMode(body.length);
            connection.setRequestProperty("Authorization", "Widget " + key);
            connection.setRequestProperty("Content-Type", "application/json");
            connection.setRequestProperty("Accept", "application/json");
            try (OutputStream out = connection.getOutputStream()) {
                out.write(body);
            }
            int status = connection.getResponseCode();
            if (status == HttpURLConnection.HTTP_UNAUTHORIZED) WidgetKey.erase(context, key);
            if (status != HttpURLConnection.HTTP_OK) return null;
            JSONObject answer = new JSONObject(read(connection.getInputStream()));
            String state = answer.getString("state");
            int count = answer.getInt("count");
            int timesPerDay = answer.getInt("timesPerDay");
            if (!STATES.contains(state) || count < 0 || timesPerDay < 1) return null;
            return answer;
        } catch (IOException | JSONException | RuntimeException e) {
            return null;
        } finally {
            if (connection != null) connection.disconnect();
        }
    }

    private static String read(InputStream in) throws IOException {
        try (InputStream body = in) {
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            byte[] buffer = new byte[1024];
            int n;
            while ((n = body.read(buffer)) != -1) {
                out.write(buffer, 0, n);
                if (out.size() > MAX_ANSWER) throw new IOException("answer too large");
            }
            return out.toString("UTF-8");
        }
    }

    private interface RowEdit {
        boolean apply(JSONObject row) throws JSONException;
    }

    /**
     * Edits the stored row of {@code habitId} on {@code date} (the snapshot's day or its {@code next} day) while the
     * stored token is still {@code token}; true when {@code edit} changed it. Callers hold the class lock.
     */
    private static boolean editRow(Context context, String token, String date, long habitId, RowEdit edit) {
        String stored = prefs(context).getString(SNAPSHOT, null);
        if (stored == null) return false;
        try {
            JSONObject snapshot = new JSONObject(stored);
            if (!snapshot.optString("token").equals(token)) return false;
            JSONObject next = snapshot.optJSONObject("next");
            JSONObject day = date.equals(snapshot.optString("date"))
                ? snapshot
                : next != null && date.equals(next.optString("date")) ? next : null;
            JSONArray habits = day == null ? null : day.optJSONArray("habits");
            for (int i = 0; habits != null && i < habits.length(); i++) {
                JSONObject row = habits.optJSONObject(i);
                if (row == null || row.optLong("id") != habitId) continue;
                if (!edit.apply(row)) return false;
                prefs(context).edit().putString(SNAPSHOT, snapshot.toString()).commit();
                return true;
            }
            return false;
        } catch (JSONException e) {
            return false;
        }
    }

    private static JSONArray pending(Context context) {
        try {
            return new JSONArray(prefs(context).getString(PENDING, "[]"));
        } catch (JSONException e) {
            return new JSONArray();
        }
    }

    private static boolean samePending(JSONObject entry, String userId, long habitId, String date) {
        return entry != null
            && userId.equals(entry.optString("userId"))
            && entry.optLong("habitId") == habitId
            && date.equals(entry.optString("date"));
    }

    /** Records a pending check-in, one per user, habit and date, keeping the highest count. */
    private static void addPending(Context context, String userId, long habitId, String date, int count) {
        synchronized (TodayWidget.class) {
            JSONArray entries = pending(context);
            try {
                for (int i = 0; i < entries.length(); i++) {
                    JSONObject entry = entries.optJSONObject(i);
                    if (!samePending(entry, userId, habitId, date)) continue;
                    entry.put("count", Math.max(count, entry.optInt("count")));
                    prefs(context).edit().putString(PENDING, entries.toString()).commit();
                    return;
                }
                entries.put(
                    new JSONObject().put("userId", userId).put("habitId", habitId).put("date", date).put("count", count)
                );
            } catch (JSONException e) {
                return;
            }
            prefs(context).edit().putString(PENDING, entries.toString()).commit();
        }
    }

    /** Drops the pending entry once the server's {@code count} reached its target. */
    private static void settlePending(Context context, String userId, long habitId, String date, int count) {
        synchronized (TodayWidget.class) {
            JSONArray entries = pending(context);
            JSONArray kept = new JSONArray();
            for (int i = 0; i < entries.length(); i++) {
                JSONObject entry = entries.optJSONObject(i);
                if (samePending(entry, userId, habitId, date) && count >= entry.optInt("count")) continue;
                if (entry != null) kept.put(entry);
            }
            prefs(context).edit().putString(PENDING, kept.toString()).commit();
        }
    }

    /**
     * Returns and removes the pending widget check-ins ({@code userId}, {@code habitId}, {@code date} and the
     * absolute {@code count} the tap aimed for), then redraws. Entries with a request still in flight or queued stay
     * stored, so the widget's own POST cannot land on top of the count JS sets.
     */
    static JSArray takePending(Context context) {
        JSArray result = new JSArray();
        synchronized (TodayWidget.class) {
            JSONArray entries = pending(context);
            JSONArray kept = new JSONArray();
            for (int i = 0; i < entries.length(); i++) {
                JSONObject entry = entries.optJSONObject(i);
                if (entry == null) continue;
                String key = pendingKey(entry.optString("userId"), entry.optLong("habitId"), entry.optString("date"));
                if (IN_FLIGHT.containsKey(key)) kept.put(entry);
                else result.put(entry);
            }
            prefs(context).edit().putString(PENDING, kept.toString()).commit();
        }
        refresh(context);
        return result;
    }

    private static String pendingKey(String userId, long habitId, String date) {
        return userId + "\n" + habitId + "\n" + date;
    }

    private static void inFlight(String userId, long habitId, String date, int change) {
        synchronized (TodayWidget.class) {
            IN_FLIGHT.merge(pendingKey(userId, habitId, date), change, (a, b) -> a + b > 0 ? a + b : null);
        }
    }

    /** Keys of pending check-ins with no request in flight: a failed request or one a killed process lost. */
    private static Set<String> unsaved(Context context) {
        Set<String> keys = new HashSet<>();
        synchronized (TodayWidget.class) {
            JSONArray entries = pending(context);
            for (int i = 0; i < entries.length(); i++) {
                JSONObject entry = entries.optJSONObject(i);
                if (entry == null) continue;
                String key = pendingKey(entry.optString("userId"), entry.optLong("habitId"), entry.optString("date"));
                if (!IN_FLIGHT.containsKey(key)) keys.add(key);
            }
        }
        return keys;
    }

    /**
     * Validates and stores a snapshot with a fresh tap token, then redraws every widget. {@code next} (optional,
     * {@code date}, {@code habits}, {@code more}) is the following day, shown once the zone reaches its date.
     */
    static void save(
        Context context,
        String userId,
        String date,
        String timeZone,
        JSONArray habits,
        int more,
        JSONObject next
    ) throws JSONException {
        if (userId == null || userId.isEmpty() || userId.length() > 100) throw new IllegalArgumentException("userId");
        if (timeZone == null || timeZone.isEmpty()) throw new IllegalArgumentException("timeZone");
        byte[] random = new byte[16];
        new SecureRandom().nextBytes(random);
        StringBuilder token = new StringBuilder();
        for (byte b : random) token.append(String.format(Locale.US, "%02x", b));
        JSONObject snapshot = day(date, habits, more)
            .put("userId", userId)
            .put("timeZone", timeZone)
            .put("token", token.toString());
        if (next != null) {
            String nextDate = next.optString("date");
            if (nextDate.compareTo(date) <= 0) throw new IllegalArgumentException("next");
            snapshot.put("next", day(nextDate, next.getJSONArray("habits"), next.getInt("more")));
        }
        synchronized (TodayWidget.class) {
            prefs(context).edit().putString(SNAPSHOT, snapshot.toString()).commit();
        }
        refresh(context);
    }

    private static JSONObject day(String date, JSONArray habits, int more) throws JSONException {
        if (date == null || !ISO_DATE.matcher(date).matches()) throw new IllegalArgumentException("date");
        if (habits == null || habits.length() > MAX_ROWS || more < 0) throw new IllegalArgumentException("habits");
        JSONArray rows = new JSONArray();
        for (int i = 0; i < habits.length(); i++) {
            JSONObject habit = habits.getJSONObject(i);
            long id = habit.getLong("id");
            String name = habit.getString("name").trim();
            String state = habit.getString("state");
            int count = habit.getInt("count");
            int timesPerDay = habit.getInt("timesPerDay");
            if (id <= 0 || name.isEmpty() || !STATES.contains(state) || timesPerDay < 1 || count < 0) {
                throw new IllegalArgumentException("habit");
            }
            rows.put(
                new JSONObject()
                    .put("id", id)
                    .put("name", name.length() > MAX_NAME ? name.substring(0, MAX_NAME) : name)
                    .put("state", state)
                    .put("count", Math.min(count, timesPerDay))
                    .put("timesPerDay", timesPerDay)
            );
        }
        return new JSONObject().put("date", date).put("habits", rows).put("more", more);
    }

    /**
     * Forgets the snapshot, widget key, API origin and pending check-ins (sign-out, session end, account deletion)
     * and redraws the signed-out state.
     */
    static void clear(Context context) {
        synchronized (TodayWidget.class) {
            prefs(context).edit().clear().commit();
        }
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
        JSONObject habit = listed(current(context), intent);
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
        if (habitId <= 0 || date == null || !ISO_DATE.matcher(date).matches()) return null;
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

    static SharedPreferences prefs(Context context) {
        return context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
    }

    /** The stored snapshot when it, or its {@code next} day, is for today in its zone, else null. */
    private static JSONObject current(Context context) {
        String stored = prefs(context).getString(SNAPSHOT, null);
        if (stored == null) return null;
        try {
            JSONObject snapshot = new JSONObject(stored);
            SimpleDateFormat format = new SimpleDateFormat("yyyy-MM-dd", Locale.US);
            format.setTimeZone(TimeZone.getTimeZone(snapshot.getString("timeZone")));
            String today = format.format(new Date());
            if (today.equals(snapshot.getString("date"))) return snapshot;
            JSONObject next = snapshot.optJSONObject("next");
            if (next == null || !today.equals(next.optString("date"))) return null;
            return snapshot
                .put("date", today)
                .put("habits", next.getJSONArray("habits"))
                .put("more", next.getInt("more"));
        } catch (JSONException e) {
            return null;
        }
    }

    private static void render(Context context, AppWidgetManager manager, int[] ids) {
        if (ids == null || ids.length == 0) return;
        JSONObject snapshot = current(context);
        if (snapshot != null) scheduleMidnight(context, snapshot.optString("timeZone"));
        for (int id : ids) manager.updateAppWidget(id, views(context, snapshot, rows(manager, id)));
    }

    /** Rows that fit the widget's smallest reported height (landscape on phones), from 1 to {@link #MAX_ROWS}. */
    private static int rows(AppWidgetManager manager, int id) {
        Bundle options = manager.getAppWidgetOptions(id);
        int height = options == null ? 0 : options.getInt(AppWidgetManager.OPTION_APPWIDGET_MIN_HEIGHT);
        if (height <= 0) return MAX_ROWS;
        return Math.max(1, Math.min(MAX_ROWS, (height - CHROME_DP) / ROW_DP));
    }

    private static RemoteViews views(Context context, JSONObject snapshot, int rows) {
        RemoteViews views = new RemoteViews(context.getPackageName(), R.layout.widget_today);
        Context text = AppLanguage.localized(context);
        views.setTextViewText(R.id.widget_title, text.getString(R.string.widget_name));
        Intent today = new Intent(context, MainActivity.class).setAction(ACTION_OPEN_TODAY);
        views.setOnClickPendingIntent(R.id.widget_root, open(context, 0, today));
        boolean keyed = snapshot != null && WidgetKey.get(context) != null && WidgetKey.apiBase(context) != null;
        Set<String> unsaved = snapshot == null ? new HashSet<>() : unsaved(context);
        JSONArray habits = snapshot == null ? null : snapshot.optJSONArray("habits");
        int shown = habits == null ? 0 : Math.min(habits.length(), rows);
        int more = snapshot == null ? 0 : Math.max(0, snapshot.optInt("more")) + (habits == null ? 0 : habits.length() - shown);
        views.setTextViewText(
            R.id.widget_empty,
            text.getString(
                snapshot != null
                    ? R.string.widget_nothing_today
                    : prefs(context).contains(SNAPSHOT) ? R.string.widget_stale : R.string.widget_signed_out
            )
        );
        views.setViewVisibility(R.id.widget_empty, shown == 0 && more == 0 ? View.VISIBLE : View.GONE);
        for (int i = 0; i < MAX_ROWS; i++) {
            JSONObject habit = i < shown ? habits.optJSONObject(i) : null;
            if (habit == null) {
                views.setViewVisibility(ROWS[i], View.GONE);
                continue;
            }
            String name = habit.optString("name");
            String state = stateLabel(text, habit);
            long habitId = habit.optLong("id");
            String userId = snapshot.optString("userId");
            String date = snapshot.optString("date");
            if (unsaved.contains(pendingKey(userId, habitId, date))) state = text.getString(R.string.widget_not_saved, state);
            views.setViewVisibility(ROWS[i], View.VISIBLE);
            views.setTextViewText(NAMES[i], name);
            views.setTextViewText(STATE_TEXTS[i], state);
            boolean checkIn = "open".equals(habit.optString("state")) || "partial".equals(habit.optString("state"));
            views.setContentDescription(
                ROWS[i],
                text.getString(checkIn ? R.string.widget_row_description : R.string.widget_row_open_description, name, state)
            );
            boolean background = checkIn && keyed;
            Intent tap = new Intent(context, background ? TodayWidget.class : MainActivity.class)
                .setAction(checkIn ? ACTION_CHECK_IN : ACTION_OPEN_HABIT)
                .putExtra("habitId", habitId)
                .putExtra("date", date)
                .putExtra("userId", userId)
                .putExtra("token", snapshot.optString("token"));
            views.setOnClickPendingIntent(ROWS[i], background ? broadcast(context, i + 1, tap) : open(context, i + 1, tap));
        }
        views.setViewVisibility(R.id.widget_more, more > 0 ? View.VISIBLE : View.GONE);
        views.setTextViewText(R.id.widget_more, text.getString(R.string.widget_more, more));
        return views;
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
        PendingIntent pending = PendingIntent.getBroadcast(
            context,
            0,
            intent,
            PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT
        );
        alarms.set(AlarmManager.RTC, midnight.getTimeInMillis(), pending);
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

    private static PendingIntent broadcast(Context context, int requestCode, Intent intent) {
        return PendingIntent.getBroadcast(
            context,
            requestCode,
            intent,
            PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT
        );
    }

    static PendingIntent open(Context context, int requestCode, Intent intent) {
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        return PendingIntent.getActivity(
            context,
            requestCode,
            intent,
            PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT
        );
    }
}
