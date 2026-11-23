package app.ember.habits.widget

import android.content.Context
import java.io.IOException
import java.net.HttpURLConnection
import java.util.concurrent.atomic.AtomicReference
import org.json.JSONException
import org.json.JSONObject

/** The widget's own day: one GET of `/api/widget/today` with the widget key, never on [CheckInClient]'s network thread. */
internal object DayClient {

    private const val MAX_ANSWER = 32 * 1024
    private const val MAX_PROBLEM = 4096

    /**
     * Fetches the key's day, waiting for it until [deadline] ([CheckInClient.call]), and stores it
     * ([WidgetStore.saveFetched]); true when stored. Sends nothing while no snapshot is stored. A 401 erases the key
     * and a 403 whose problem `type` is `consent_required` drops the snapshot; any other failure keeps what is stored. The answer and the drop apply only while
     * the snapshot is unchanged since the request was sent.
     */
    fun fetch(context: Context, apiBase: String, key: String, deadline: Long): Boolean {
        val seen = WidgetStore.version(context) ?: return false
        val answer = CheckInClient.call(deadline) { open -> get(context, apiBase, key, seen, deadline, open) }
        return answer != null && WidgetStore.saveFetched(context, answer, seen)
    }

    private fun get(
        context: Context,
        apiBase: String,
        key: String,
        seen: WidgetStore.Version,
        deadline: Long,
        open: AtomicReference<HttpURLConnection?>,
    ): JSONObject? {
        var connection: HttpURLConnection? = null
        try {
            val request = CheckInClient.connect(apiBase, "/api/widget/today", key, deadline, open)
            connection = request
            request.requestMethod = "GET"
            when (request.responseCode) {
                HttpURLConnection.HTTP_OK -> return JSONObject(CheckInClient.read(request.inputStream, MAX_ANSWER))
                HttpURLConnection.HTTP_UNAUTHORIZED -> WidgetKey.erase(context, key)
                HttpURLConnection.HTTP_FORBIDDEN -> if (consentRequired(request)) WidgetStore.dropSnapshot(context, seen)
            }
            return null
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

    @Throws(IOException::class, JSONException::class)
    private fun consentRequired(request: HttpURLConnection): Boolean {
        val body = request.errorStream ?: return false
        return JSONObject(CheckInClient.read(body, MAX_PROBLEM)).optString("type") == "consent_required"
    }
}
