package app.ember.habits.widget

import android.os.SystemClock
import app.ember.habits.FakeAndroidKeyStore
import app.ember.habits.TodayHost
import app.ember.habits.TodayRefreshJob
import java.time.LocalDate
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.json.JSONArray
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * The widget's own day fetch against a local server: what it sends, that a valid day replaces the snapshot, and that
 * a failure keeps the stored one, so yesterday's `next` day or the stale message stays.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [30])
class DayClientTest {

    private lateinit var host: TodayHost
    private val server = MockWebServer()
    private val key = "k".repeat(43)
    private lateinit var base: String

    @Before
    fun setUp() {
        host = TodayHost()
        base = server.url("/").toString().removeSuffix("/")
        host.today(host.habit(1, "Water"), date = yesterday)
    }

    @After
    fun tearDown() {
        server.shutdown()
    }

    private val yesterday get() = LocalDate.parse(host.today).minusDays(1).toString()

    private fun day(vararg habits: JSONObject, date: String = host.today) = JSONObject()
        .put("userId", "user-1")
        .put("timeZone", host.zone)
        .put("date", date)
        .put("habits", JSONArray(habits.toList()))
        .put("more", 0)
        .put("done", habits.count { it.getString("state") == "done" })
        .put("total", habits.size)

    private fun fetch() = DayClient.fetch(host.context, base, key, SystemClock.elapsedRealtime() + 8000)

    private fun names(): List<String> {
        val habits = WidgetStore.current(host.context)!!.getJSONArray("habits")
        return (0 until habits.length()).map { habits.getJSONObject(it).getString("name") }
    }

    @Test
    fun getsTheDayWithTheWidgetKeyAndStoresIt() {
        server.enqueue(MockResponse().setBody(day(host.habit(2, "Walk")).toString()))

        assertTrue(fetch())

        val request = server.takeRequest(1, TimeUnit.SECONDS)!!
        assertEquals("GET", request.method)
        assertEquals("/api/widget/today", request.path)
        assertEquals("Widget $key", request.getHeader("Authorization"))
        assertEquals("application/json", request.getHeader("Accept"))
        assertEquals(listOf("Walk"), names())
    }

    @Test
    fun aFailedOrInvalidAnswerKeepsTheStoredSnapshot() {
        val next = JSONObject().put("date", host.today).put("habits", JSONArray().put(host.habit(3, "Read"))).put("more", 0)
        WidgetStore.save(host.context, "user-1", yesterday, host.zone, JSONArray().put(host.habit(1, "Water")), 0, null, null, next)
        val stored = WidgetStore.stored(host.context).toString()
        val valid = day(host.habit(2, "Walk")).toString()
        val answers = listOf(
            MockResponse().setResponseCode(500).setBody(valid),
            MockResponse().setResponseCode(429).setBody(valid),
            MockResponse().setResponseCode(302).setHeader("Location", server.url("/elsewhere")),
            MockResponse().setBody("not json"),
            MockResponse().setBody(day(host.habit(2, "Walk", "skipped")).toString()),
            MockResponse().setBody(day(host.habit(2, "Walk")).put("pad", "x".repeat(33 * 1024)).toString()),
        )
        for (each in answers) {
            server.enqueue(each)
            assertFalse(each.toString(), fetch())
            assertEquals(each.toString(), stored, WidgetStore.stored(host.context).toString())
        }
        assertEquals(answers.size, server.requestCount)

        server.shutdown()
        assertFalse(fetch())
        assertEquals(stored, WidgetStore.stored(host.context).toString())
        assertEquals(listOf("Read"), names())
    }

    @Test
    fun withNoSnapshotStoredNothingIsSent() {
        WidgetStore.clear(host.context)

        assertFalse(fetch())

        assertEquals(0, server.requestCount)
    }

    @Test
    fun aRejectedKeyIsErasedAndTheSnapshotKept() {
        FakeAndroidKeyStore.install()
        WidgetKey.set(host.context, key, "https://ember.example")
        server.enqueue(MockResponse().setResponseCode(401))

        assertFalse(fetch())

        assertNull(WidgetKey.get(host.context))
        assertTrue(WidgetStore.hasSnapshot(host.context))
    }

    @Test
    fun aRefusedConsentDropsOnlyTheSnapshot() {
        FakeAndroidKeyStore.install()
        WidgetKey.set(host.context, key, "https://ember.example")
        WidgetStore.setStyle(host.context, "dark", true, "ember")
        WidgetStore.addPending(host.context, "user-1", 1, yesterday, 1)
        server.enqueue(MockResponse().setResponseCode(403))

        assertFalse(fetch())

        assertFalse(WidgetStore.hasSnapshot(host.context))
        assertNotNull(WidgetKey.get(host.context))
        assertEquals("dark", WidgetStore.prefs(host.context).getString(WidgetStore.THEME, null))
        assertEquals(1, WidgetStore.takePending(host.context).length())
    }

    @Test
    fun theJobsFetchWaitsForATapsCheckInQueuedBeforeIt() {
        host.key()
        val token = WidgetStore.stored(host.context)!!.getString("token")
        server.enqueue(
            MockResponse().setHeadersDelay(800, TimeUnit.MILLISECONDS).setBody("""{"state":"done","count":1,"timesPerDay":1}"""),
        )
        val posted = CountDownLatch(1)
        CheckInClient.queue {
            CheckInClient.send(host.context, base, key, token, "user-1", 1, yesterday, SystemClock.elapsedRealtime() + 8000)
            posted.countDown()
        }
        val postedFirst = AtomicBoolean(false)
        val finished = CountDownLatch(1)

        TodayRefreshJob.run(host.context) {
            postedFirst.set(posted.count == 0L)
            finished.countDown()
        }

        assertTrue(finished.await(5, TimeUnit.SECONDS))
        assertTrue(postedFirst.get())
        assertEquals(1, server.requestCount)
    }
}
