package app.ember.habits.widget

import android.content.Context
import android.os.SystemClock
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.io.InputStream
import java.net.HttpURLConnection
import java.net.URL
import java.util.concurrent.ExecutionException
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.Future
import java.util.concurrent.TimeUnit
import java.util.concurrent.TimeoutException
import java.util.concurrent.atomic.AtomicReference
import kotlin.math.max
import kotlin.math.min
import org.json.JSONException
import org.json.JSONObject

/**
 * The widget's own check-ins: one POST per tap with the widget key. Taps queue on one network thread, and each POST
 * runs on a thread of its own, so a request that outlives its tap's deadline can be cut off.
 */
internal object CheckInClient {

    private const val TIMEOUT_MS = 4000L
    private const val MIN_REQUEST_MS = 1000L
    private const val MAX_ANSWER = 4096
    private val NETWORK: ExecutorService = Executors.newSingleThreadExecutor()
    private val CALLS: ExecutorService = Executors.newCachedThreadPool()

    /** Runs [task] on the network thread, after the check-ins queued before it. */
    fun queue(task: Runnable) = NETWORK.execute(task)

    /**
     * Runs [post] on its own thread and waits for it until [deadline] ([SystemClock.elapsedRealtime] time),
     * disconnecting on timeout; skips it when less than [MIN_REQUEST_MS] is left. A valid answer settles the pending
     * entry and updates the row while the snapshot is unchanged and no other request for that row is in flight or
     * queued; a `done` answer also removes that habit's delivered reminders for [date] ([DeliveredReminders]). Callers
     * hold this request's in-flight mark.
     */
    fun send(
        context: Context,
        apiBase: String,
        key: String,
        token: String,
        userId: String,
        habitId: Long,
        date: String,
        deadline: Long,
    ) {
        val remaining = deadline - SystemClock.elapsedRealtime()
        if (remaining < MIN_REQUEST_MS) return
        val open = AtomicReference<HttpURLConnection?>()
        val call = CALLS.submit<JSONObject?> { post(context, apiBase, key, habitId, date, deadline, open) }
        val answer = try {
            call.get(remaining, TimeUnit.MILLISECONDS)
        } catch (e: ExecutionException) {
            abandon(call, open)
            return
        } catch (e: TimeoutException) {
            abandon(call, open)
            return
        } catch (e: InterruptedException) {
            abandon(call, open)
            Thread.currentThread().interrupt()
            return
        } ?: return
        val state = answer.optString("state")
        val count = answer.optInt("count")
        val timesPerDay = answer.optInt("timesPerDay")
        synchronized(WidgetStore.LOCK) {
            if (WidgetStore.inFlightCount(userId, habitId, date) <= 1) {
                WidgetStore.editRow(context, token, date, habitId) { row ->
                    row.put("state", state).put("count", min(count, timesPerDay)).put("timesPerDay", timesPerDay)
                    true
                }
            }
            WidgetStore.settlePending(context, userId, habitId, date, count)
        }
        if (state == "done") DeliveredReminders.clear(context, userId, habitId, date)
    }

    private fun abandon(call: Future<*>, open: AtomicReference<HttpURLConnection?>) {
        call.cancel(true)
        open.get()?.disconnect()
    }

    /**
     * POSTs one check-in with the widget key, each timeout capped by what is left until [deadline], and publishes the
     * connection in [open] so a timed-out caller can disconnect it. Returns a validated 200 answer, else null; a 401
     * erases the key.
     */
    private fun post(
        context: Context,
        apiBase: String,
        key: String,
        habitId: Long,
        date: String,
        deadline: Long,
        open: AtomicReference<HttpURLConnection?>,
    ): JSONObject? {
        var connection: HttpURLConnection? = null
        try {
            val body = JSONObject().put("habitId", habitId).put("date", date).toString().toByteArray(Charsets.UTF_8)
            val request = URL("$apiBase/api/widget/check-in").openConnection() as HttpURLConnection
            connection = request
            open.set(request)
            val timeout = max(1L, min(TIMEOUT_MS, deadline - SystemClock.elapsedRealtime())).toInt()
            request.connectTimeout = timeout
            request.readTimeout = timeout
            request.instanceFollowRedirects = false
            request.useCaches = false
            request.requestMethod = "POST"
            request.doOutput = true
            request.setFixedLengthStreamingMode(body.size)
            request.setRequestProperty("Authorization", "Widget $key")
            request.setRequestProperty("Content-Type", "application/json")
            request.setRequestProperty("Accept", "application/json")
            request.outputStream.use { it.write(body) }
            val status = request.responseCode
            if (status == HttpURLConnection.HTTP_UNAUTHORIZED) WidgetKey.erase(context, key)
            if (status != HttpURLConnection.HTTP_OK) return null
            val answer = JSONObject(read(request.inputStream))
            val state = answer.getString("state")
            val count = answer.getInt("count")
            val timesPerDay = answer.getInt("timesPerDay")
            if (state !in WidgetStore.STATES || count < 0 || timesPerDay < 1) return null
            return answer
        } catch (e: IOException) {
            return null
        } catch (e: JSONException) {
            return null
        } catch (e: RuntimeException) {
            return null
        } finally {
            connection?.disconnect()
        }
    }

    @Throws(IOException::class)
    private fun read(input: InputStream): String = input.use { body ->
        val out = ByteArrayOutputStream()
        val buffer = ByteArray(1024)
        while (true) {
            val n = body.read(buffer)
            if (n == -1) break
            out.write(buffer, 0, n)
            if (out.size() > MAX_ANSWER) throw IOException("answer too large")
        }
        out.toString("UTF-8")
    }
}
