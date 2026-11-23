package app.ember.habits.widget

import android.content.Context
import android.content.SharedPreferences
import com.getcapacitor.JSArray
import java.security.SecureRandom
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone
import java.util.regex.Pattern
import kotlin.math.max
import kotlin.math.min
import org.json.JSONArray
import org.json.JSONException
import org.json.JSONObject

/**
 * The widgets' app-private store: the snapshot the app last pushed or the widget fetched with its key ([saveFetched]),
 * the check-ins JS has not taken yet, the time of the last day request and the style, in one SharedPreferences file
 * that [WidgetKey] shares for the sealed key and API origin. Every write of the file but [setStyle]'s, and every use of
 * the in-memory count of check-in requests in flight and of the snapshot revision ([version]), holds [LOCK].
 */
internal object WidgetStore {

    val LOCK = Any()

    const val MAX_HABITS = 25
    const val THEME = "theme"
    const val COLOR_SCHEME = "colorScheme"
    const val DAY_REQUESTED = "dayRequestedAt"

    val ISO_DATE: Pattern = Pattern.compile("^\\d{4}-\\d{2}-\\d{2}$")

    /** Every row state a snapshot or a check-in answer may carry. */
    val STATES: List<String> = listOf("done", "partial", "open", "rest")

    private const val PREFS = "ember_widget"
    private const val SNAPSHOT = "snapshot"
    private const val PENDING = "pending"
    private const val MAX_NAME = 100
    private val STYLE = listOf(THEME, COLOR_SCHEME, WidgetPlan.MOTION)
    private val requests = HashMap<String, Int>()
    private var revision = 0L

    /** The stored snapshot's tap token with the in-memory revision every snapshot write in this process bumps. */
    data class Version(val token: String, val revision: Long)

    /** The stored snapshot's [Version], read together under [LOCK], or null while none is stored. */
    fun version(context: Context): Version? =
        synchronized(LOCK) { stored(context)?.let { Version(it.optString("token"), revision) } }

    private fun unchanged(stored: JSONObject?, seen: Version) =
        stored != null && stored.optString("token") == seen.token && revision == seen.revision

    private fun write(context: Context, snapshot: JSONObject?) {
        val edit = prefs(context).edit()
        if (snapshot == null) edit.remove(SNAPSHOT) else edit.putString(SNAPSHOT, snapshot.toString())
        edit.commit()
        revision++
    }

    fun prefs(context: Context): SharedPreferences = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    /**
     * Validates and stores a snapshot with a fresh tap token; on invalid input it throws IllegalArgumentException or
     * JSONException and keeps the stored one. [done] and [total] (counted from the rows when missing) are the day's
     * "x of y". [held] (optional, at most [MAX_HABITS] ids above 0) names the rows the app holds check-ins for that
     * it has not saved yet. [next] (optional, `date`, `habits`, `more`, `done`, `total`, `held`) is the following day,
     * shown once the zone reaches its date.
     */
    @Throws(JSONException::class)
    fun save(
        context: Context,
        userId: String?,
        date: String?,
        timeZone: String?,
        habits: JSONArray?,
        more: Int,
        done: Int?,
        total: Int?,
        next: JSONObject?,
        held: JSONArray? = null,
    ) {
        val snapshot = snapshot(userId, date, timeZone, habits, more, done, total, next, held)
        synchronized(LOCK) {
            write(context, snapshot)
        }
    }

    /**
     * Stores a day the widget fetched, [answer] in [save]'s shape (`userId`, `date`, `timeZone`, `habits`, `more`,
     * `done`, `total`, `next`), with a fresh tap token; true when stored. It is stored only when it validates, the
     * stored snapshot is still at [seen] (taken before the request, so a push or a row edit since wins), is of the same
     * user, and the answer's date is not before its date. On a day the stored snapshot also has, each row it holds
     * (`held`, see [save]) keeps its stored state and count, and the held ids carry over. Then each of that user's
     * pending check-ins on one of its days raises that row to the pending count (capped at `timesPerDay`), never
     * lowering it. Both keep that day's `done` in step. Pending check-ins stay pending.
     */
    fun saveFetched(context: Context, answer: JSONObject, seen: Version): Boolean {
        val fetched = try {
            val next = if (answer.isNull("next")) null else answer.getJSONObject("next")
            snapshot(
                answer.getString("userId"),
                answer.getString("date"),
                answer.getString("timeZone"),
                answer.getJSONArray("habits"),
                answer.getInt("more"),
                answer.intOrNull("done"),
                answer.intOrNull("total"),
                next,
            )
        } catch (e: JSONException) {
            return false
        } catch (e: IllegalArgumentException) {
            return false
        }
        synchronized(LOCK) {
            val stored = stored(context) ?: return false
            if (!unchanged(stored, seen)) return false
            try {
                val userId = fetched.getString("userId")
                if (userId != stored.optString("userId")) return false
                if (fetched.getString("date") < stored.optString("date")) return false
                keepHeld(fetched, stored)
                raise(fetched, userId, pending(context))
            } catch (e: JSONException) {
                return false
            }
            write(context, fetched)
            return true
        }
    }

    @Throws(JSONException::class)
    private fun keepHeld(fetched: JSONObject, stored: JSONObject) {
        val storedDays = listOfNotNull(stored, stored.optJSONObject("next"))
        for (day in listOfNotNull(fetched, fetched.optJSONObject("next"))) {
            day.remove("held")
            val kept = storedDays.firstOrNull { it.optString("date") == day.getString("date") } ?: continue
            val held = kept.optJSONArray("held") ?: continue
            day.put("held", held)
            for (i in 0 until held.length()) {
                val was = kept.row(held.getLong(i)) ?: continue
                val row = day.row(held.getLong(i)) ?: continue
                val before = row.getString("state")
                row.put("state", was.getString("state"))
                row.put("count", min(was.getInt("count"), row.getInt("timesPerDay")))
                val done = WidgetPlan.done(day.getInt("done"), day.getInt("total"), before, row.getString("state"))
                day.put("done", done)
            }
        }
    }

    @Throws(JSONException::class)
    private fun JSONObject.row(id: Long): JSONObject? {
        val habits = getJSONArray("habits")
        return (0 until habits.length()).map { habits.getJSONObject(it) }.firstOrNull { it.getLong("id") == id }
    }

    @Throws(JSONException::class)
    private fun raise(snapshot: JSONObject, userId: String, entries: JSONArray) {
        val days = listOfNotNull(snapshot, snapshot.optJSONObject("next"))
        for (i in 0 until entries.length()) {
            val entry = entries.optJSONObject(i) ?: continue
            if (entry.optString("userId") != userId) continue
            val day = days.firstOrNull { it.getString("date") == entry.optString("date") } ?: continue
            val habits = day.getJSONArray("habits")
            for (j in 0 until habits.length()) {
                val row = habits.getJSONObject(j)
                if (row.getLong("id") != entry.optLong("habitId")) continue
                val timesPerDay = row.getInt("timesPerDay")
                val count = min(entry.optInt("count"), timesPerDay)
                if (count <= row.getInt("count")) break
                val before = row.getString("state")
                val after = if (count >= timesPerDay || before == "done") "done" else "partial"
                row.put("count", count).put("state", after)
                day.put("done", WidgetPlan.done(day.getInt("done"), day.getInt("total"), before, after))
                break
            }
        }
    }

    private fun JSONObject.intOrNull(name: String): Int? = if (has(name)) getInt(name) else null

    @Throws(JSONException::class)
    private fun snapshot(
        userId: String?,
        date: String?,
        timeZone: String?,
        habits: JSONArray?,
        more: Int,
        done: Int?,
        total: Int?,
        next: JSONObject?,
        held: JSONArray? = null,
    ): JSONObject {
        if (userId == null || userId.isEmpty() || userId.length > 100) throw IllegalArgumentException("userId")
        if (timeZone == null || timeZone.isEmpty()) throw IllegalArgumentException("timeZone")
        val random = ByteArray(16)
        SecureRandom().nextBytes(random)
        val token = StringBuilder()
        for (b in random) token.append(String.format(Locale.US, "%02x", b))
        val snapshot = day(date, habits, more, done, total, held)
            .put("userId", userId)
            .put("timeZone", timeZone)
            .put("token", token.toString())
        if (next != null) {
            val nextDate = next.optString("date")
            if (nextDate <= date!!) throw IllegalArgumentException("next")
            val nextDay = day(
                nextDate,
                next.getJSONArray("habits"),
                next.getInt("more"),
                next.intOrNull("done"),
                next.intOrNull("total"),
                next.optJSONArray("held"),
            )
            snapshot.put("next", nextDay)
        }
        return snapshot
    }

    @Throws(JSONException::class)
    private fun day(
        date: String?,
        habits: JSONArray?,
        more: Int,
        done: Int?,
        total: Int?,
        held: JSONArray?,
    ): JSONObject {
        if (date == null || !ISO_DATE.matcher(date).matches()) throw IllegalArgumentException("date")
        if (habits == null || habits.length() > MAX_HABITS || more < 0) throw IllegalArgumentException("habits")
        val rows = JSONArray()
        var doneRows = 0
        for (i in 0 until habits.length()) {
            val habit = habits.getJSONObject(i)
            val id = habit.getLong("id")
            val name = habit.getString("name").trim { it <= ' ' }
            val state = habit.getString("state")
            val count = habit.getInt("count")
            val timesPerDay = habit.getInt("timesPerDay")
            if (id <= 0 || name.isEmpty() || state !in STATES || timesPerDay < 1 || count < 0) {
                throw IllegalArgumentException("habit")
            }
            if (state == "done") doneRows++
            rows.put(
                JSONObject()
                    .put("id", id)
                    .put("name", name.take(MAX_NAME))
                    .put("state", state)
                    .put("count", min(count, timesPerDay))
                    .put("timesPerDay", timesPerDay),
            )
        }
        val dayTotal = total ?: (habits.length() + more)
        val dayDone = done ?: doneRows
        if (!WidgetPlan.counts(dayDone, dayTotal, doneRows, habits.length(), more)) throw IllegalArgumentException("done")
        val day = JSONObject()
            .put("date", date)
            .put("habits", rows)
            .put("more", more)
            .put("done", dayDone)
            .put("total", dayTotal)
        if (held != null) {
            if (held.length() > MAX_HABITS) throw IllegalArgumentException("held")
            val ids = JSONArray()
            for (i in 0 until held.length()) {
                val id = held.getLong(i)
                if (id <= 0) throw IllegalArgumentException("held")
                ids.put(id)
            }
            day.put("held", ids)
        }
        return day
    }

    /** The stored snapshot when it, or its `next` day, is for today in its zone, else null. */
    fun current(context: Context): JSONObject? {
        val snapshot = stored(context) ?: return null
        try {
            val format = SimpleDateFormat("yyyy-MM-dd", Locale.US)
            format.timeZone = zone(snapshot.getString("timeZone"))
            val today = format.format(Date())
            if (today == snapshot.getString("date")) return snapshot
            val next = snapshot.optJSONObject("next")
            if (next == null || today != next.optString("date")) return null
            return snapshot
                .put("date", today)
                .put("habits", next.getJSONArray("habits"))
                .put("more", next.getInt("more"))
                .put("done", next.optInt("done"))
                .put("total", next.optInt("total", next.getJSONArray("habits").length() + next.getInt("more")))
        } catch (e: JSONException) {
            return null
        }
    }

    /**
     * The zone [id] names when this device's zone data knows it, else the device's own zone, never the GMT that
     * [TimeZone.getTimeZone] silently gives an id it does not know.
     */
    fun zone(id: String): TimeZone {
        val zone = TimeZone.getTimeZone(id)
        return if (zone.id == "GMT" && id != "GMT") TimeZone.getDefault() else zone
    }

    /** The stored snapshot as stored, even for another day, or null. */
    fun stored(context: Context): JSONObject? {
        val stored = prefs(context).getString(SNAPSHOT, null) ?: return null
        return try {
            JSONObject(stored)
        } catch (e: JSONException) {
            null
        }
    }

    /** Whether any snapshot is stored, even one for another day. */
    fun hasSnapshot(context: Context) = prefs(context).contains(SNAPSHOT)

    /** Forgets the stored snapshot while it is still at [seen], keeping key, pending check-ins and style. */
    fun dropSnapshot(context: Context, seen: Version) {
        synchronized(LOCK) {
            if (!unchanged(stored(context), seen)) return
            write(context, null)
        }
    }

    /**
     * Records [now] (wall clock ms) as the last day request unless the last one was less than [gap] ms before it;
     * true when recorded. A last request after [now] counts as expired.
     */
    fun claimDayRequest(context: Context, now: Long, gap: Long): Boolean {
        synchronized(LOCK) {
            val prefs = prefs(context)
            val last = prefs.getLong(DAY_REQUESTED, 0)
            if (prefs.contains(DAY_REQUESTED) && last <= now && now - last < gap) return false
            prefs.edit().putLong(DAY_REQUESTED, now).commit()
            return true
        }
    }

    /** A change to one stored row; true when it changed the row. */
    fun interface RowEdit {
        @Throws(JSONException::class)
        fun apply(row: JSONObject): Boolean
    }

    /**
     * Edits the stored row of [habitId] on [date] (the snapshot's day or its `next` day) while the stored token is
     * still [token], keeping that day's `done` in step; true when [edit] changed it. Callers that pair it with other
     * store calls hold [LOCK] around them.
     */
    fun editRow(context: Context, token: String, date: String, habitId: Long, edit: RowEdit): Boolean {
        synchronized(LOCK) {
            val stored = prefs(context).getString(SNAPSHOT, null) ?: return false
            try {
                val snapshot = JSONObject(stored)
                if (snapshot.optString("token") != token) return false
                val next = snapshot.optJSONObject("next")
                val day = when {
                    date == snapshot.optString("date") -> snapshot
                    next != null && date == next.optString("date") -> next
                    else -> return false
                }
                val habits = day.optJSONArray("habits") ?: return false
                for (i in 0 until habits.length()) {
                    val row = habits.optJSONObject(i)
                    if (row == null || row.optLong("id") != habitId) continue
                    val before = row.optString("state")
                    if (!edit.apply(row)) return false
                    if (day.has("done")) {
                        val done = WidgetPlan.done(day.optInt("done"), day.optInt("total"), before, row.optString("state"))
                        day.put("done", done)
                    }
                    write(context, snapshot)
                    return true
                }
                return false
            } catch (e: JSONException) {
                return false
            }
        }
    }

    private fun pending(context: Context): JSONArray =
        try {
            JSONArray(prefs(context).getString(PENDING, "[]"))
        } catch (e: JSONException) {
            JSONArray()
        }

    private fun JSONObject.isFor(userId: String, habitId: Long, date: String) =
        userId == optString("userId") && optLong("habitId") == habitId && date == optString("date")

    private fun JSONObject.pendingKey() = pendingKey(optString("userId"), optLong("habitId"), optString("date"))

    /** Records a pending check-in, one per user, habit and date, keeping the highest count. */
    fun addPending(context: Context, userId: String, habitId: Long, date: String, count: Int) {
        synchronized(LOCK) {
            val entries = pending(context)
            try {
                for (i in 0 until entries.length()) {
                    val entry = entries.optJSONObject(i)
                    if (entry == null || !entry.isFor(userId, habitId, date)) continue
                    entry.put("count", max(count, entry.optInt("count")))
                    prefs(context).edit().putString(PENDING, entries.toString()).commit()
                    return
                }
                entries.put(JSONObject().put("userId", userId).put("habitId", habitId).put("date", date).put("count", count))
            } catch (e: JSONException) {
                return
            }
            prefs(context).edit().putString(PENDING, entries.toString()).commit()
        }
    }

    /** Drops the pending entry once the server's [count] reached its target. */
    fun settlePending(context: Context, userId: String, habitId: Long, date: String, count: Int) {
        synchronized(LOCK) {
            val entries = pending(context)
            val kept = JSONArray()
            for (i in 0 until entries.length()) {
                val entry = entries.optJSONObject(i) ?: continue
                if (entry.isFor(userId, habitId, date) && count >= entry.optInt("count")) continue
                kept.put(entry)
            }
            prefs(context).edit().putString(PENDING, kept.toString()).commit()
        }
    }

    /**
     * Returns and removes the pending check-ins (`userId`, `habitId`, `date` and the absolute `count` the tap aimed
     * for). Entries with a request still in flight or queued stay stored, so the widget's own POST cannot land on top
     * of the count JS sets.
     */
    fun takePending(context: Context): JSArray {
        val result = JSArray()
        synchronized(LOCK) {
            val entries = pending(context)
            val kept = JSONArray()
            for (i in 0 until entries.length()) {
                val entry = entries.optJSONObject(i) ?: continue
                if (requests.containsKey(entry.pendingKey())) kept.put(entry) else result.put(entry)
            }
            prefs(context).edit().putString(PENDING, kept.toString()).commit()
        }
        return result
    }

    /** [pendingKey]s of pending check-ins with no request in flight: a failed request or one a killed process lost. */
    fun unsaved(context: Context): Set<String> {
        val keys = HashSet<String>()
        synchronized(LOCK) {
            val entries = pending(context)
            for (i in 0 until entries.length()) {
                val key = entries.optJSONObject(i)?.pendingKey() ?: continue
                if (!requests.containsKey(key)) keys.add(key)
            }
        }
        return keys
    }

    /** Identifies one user's check-in on a habit and date. */
    fun pendingKey(userId: String, habitId: Long, date: String) = "$userId\n$habitId\n$date"

    /** Adds [change] to the check-in requests in flight or queued for one check-in; the entry goes at 0. */
    fun inFlight(userId: String, habitId: Long, date: String, change: Int) {
        synchronized(LOCK) {
            requests.merge(pendingKey(userId, habitId, date), change) { a, b -> if (a + b > 0) a + b else null }
        }
    }

    /** The check-in requests in flight or queued for one check-in. */
    fun inFlightCount(userId: String, habitId: Long, date: String): Int =
        synchronized(LOCK) { requests.getOrDefault(pendingKey(userId, habitId, date), 0) }

    /** Forgets the snapshot, widget key, API origin and pending check-ins, keeping the style. */
    fun clear(context: Context) {
        synchronized(LOCK) {
            val prefs = prefs(context)
            val edit = prefs.edit()
            for (key in prefs.all.keys) if (key !in STYLE) edit.remove(key)
            edit.commit()
        }
    }

    /** Stores the app's widget style ([THEME], [COLOR_SCHEME], [WidgetPlan.MOTION]). */
    fun setStyle(context: Context, theme: String?, motion: Boolean, colorScheme: String?) {
        prefs(context)
            .edit()
            .putString(THEME, theme)
            .putString(COLOR_SCHEME, colorScheme)
            .putBoolean(WidgetPlan.MOTION, motion)
            .commit()
    }
}
