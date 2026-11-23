package app.ember.habits.widget

import android.content.Context
import android.os.SystemClock
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.io.InputStream
import java.net.HttpURLConnection
import java.net.URL
import java.util.concurrent.Callable
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
 * The widget's own check-ins: one POST per tap with the widget key. Taps queue on one network thread, apart from the
 * day fetches ([DayClient]), and each request runs on a thread of its own ([call]), so one that outlives its deadline
 * can be cut off.
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
     * POSTs one check-in, waiting for it until [deadline] ([call]). A valid answer settles the pending entry and
     * updates the row while the snapshot is unchanged and no other request for that row is in flight or queued; a
     * `done` answer also removes that habit's delivered reminders for [date] ([DeliveredReminders]). Callers hold this
     * request's in-flight mark.
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
        val answer = call(deadline) { open -> post(context, apiBase, key, habitId, date, deadline, open) } ?: return
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

    /**
     * Runs [request] on its own thread and waits for its result until [deadline] ([SystemClock.elapsedRealtime]
     * time), disconnecting the connection it publishes in its argument on timeout; null on failure or timeout, or
     * without a request when less than [MIN_REQUEST_MS] is left.
     */
    fun <T : Any> call(deadline: Long, request: (AtomicReference<HttpURLConnection?>) -> T?): T? {
        val remaining = deadline - SystemClock.elapsedRealtime()
        if (remaining < MIN_REQUEST_MS) return null
        val open = AtomicReference<HttpURLConnection?>()
        val call = CALLS.submit(Callable { request(open) })
        return try {
            call.get(remaining, TimeUnit.MILLISECONDS)
        } catch (e: ExecutionException) {
            abandon(call, open)
            null
        } catch (e: TimeoutException) {
            abandon(call, open)
            null
        } catch (e: InterruptedException) {
            abandon(call, open)
            Thread.currentThread().interrupt()
            null
        }
    }

    /**
     * Opens a request to [path] under [apiBase] with the widget key and publishes it in [open]: each timeout capped
     * by what is left until [deadline], no redirects, no caches, JSON accepted.
     */
    @Throws(IOException::class)
    fun connect(
        apiBase: String,
        path: String,
        key: String,
        deadline: Long,
        open: AtomicReference<HttpURLConnection?>,
    ): HttpURLConnection {
        val request = URL("$apiBase$path").openConnection() as HttpURLConnection
        open.set(request)
        val timeout = max(1L, min(TIMEOUT_MS, deadline - SystemClock.elapsedRealtime())).toInt()
        request.connectTimeout = timeout
        request.readTimeout = timeout
        request.instanceFollowRedirects = false
        request.useCaches = false
        request.setRequestProperty("Authorization", "Widget $key")
        request.setRequestProperty("Accept", "application/json")
        return request
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
            val request = connect(apiBase, "/api/widget/check-in", key, deadline, open)
            connection = request
            request.requestMethod = "POST"
            request.doOutput = true
            request.setFixedLengthStreamingMode(body.size)
            request.setRequestProperty("Content-Type", "application/json")
            request.outputStream.use { it.write(body) }
            val status = request.responseCode
            if (status == HttpURLConnection.HTTP_UNAUTHORIZED) WidgetKey.erase(context, key)
            if (status != HttpURLConnection.HTTP_OK) return null
            val answer = JSONObject(read(request.inputStream, MAX_ANSWER))
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

    /** Reads [input] as UTF-8, throwing IOException once it passes [limit] bytes. */
    @Throws(IOException::class)
    fun read(input: InputStream, limit: Int): String = input.use { body ->
        val out = ByteArrayOutputStream()
        val buffer = ByteArray(1024)
        while (true) {
            val n = body.read(buffer)
            if (n == -1) break
            out.write(buffer, 0, n)
            if (out.size() > limit) throw IOException("answer too large")
        }
        out.toString("UTF-8")
    }
}
