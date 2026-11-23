package app.ember.habits.widget

import android.os.SystemClock
import app.ember.habits.FakeAndroidKeyStore
import app.ember.habits.HangingOrigin
import app.ember.habits.TodayHost
import app.ember.habits.TodayRefreshJob
import java.time.LocalDate
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import okhttp3.mockwebserver.Dispatcher
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.RecordedRequest
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
    fun aFullDayOfLongNamesTheServerEscapesIsStored() {
        val name = "\u00e4".repeat(100)
        val rows = (1L..25L).map { host.habit(it, name) }.toTypedArray()
        val next = day(*rows, date = LocalDate.parse(host.today).plusDays(1).toString())
        val body = day(*rows).put("next", next).toString().replace("\u00e4", "\\u00e4")
        assertTrue(body.length > 32 * 1024)
        server.enqueue(MockResponse().setBody(body))

        assertTrue(fetch())

        assertEquals(List(25) { name }, names())
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
            MockResponse().setBody(day(host.habit(2, "Walk")).put("pad", "x".repeat(65 * 1024)).toString()),
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
        server.enqueue(
            MockResponse().setResponseCode(403).setHeader("Content-Type", "application/problem+json")
                .setBody("""{"type":"consent_required","title":"Forbidden","status":403}"""),
        )

        assertFalse(fetch())

        assertFalse(WidgetStore.hasSnapshot(host.context))
        assertNotNull(WidgetKey.get(host.context))
        assertEquals("dark", WidgetStore.prefs(host.context).getString(WidgetStore.THEME, null))
        assertEquals(1, WidgetStore.takePending(host.context).length())
    }

    @Test
    fun aForbiddenAnswerWithoutTheConsentProblemKeepsTheSnapshot() {
        val stored = WidgetStore.stored(host.context).toString()
        val answers = listOf(
            MockResponse().setResponseCode(403),
            MockResponse().setResponseCode(403).setBody("<html>Sign in to this network</html>"),
            MockResponse().setResponseCode(403).setBody("""{"type":"forbidden","status":403}"""),
        )
        for (each in answers) {
            server.enqueue(each)
            assertFalse(fetch())
            assertEquals(each.toString(), stored, WidgetStore.stored(host.context).toString())
        }
    }

    @Test
    fun aTapsCheckInIsSentWhileTheJobsFetchHangs() {
        HangingOrigin().use { origin ->
            FakeAndroidKeyStore.install()
            WidgetKey.set(host.context, key, origin.base)
            val token = WidgetStore.stored(host.context)!!.getString("token")
            server.enqueue(MockResponse().setBody("""{"state":"done","count":1,"timesPerDay":1}"""))
            val finished = CountDownLatch(1)

            TodayRefreshJob.run(host.context) { finished.countDown() }
            assertTrue(origin.accepted.tryAcquire(2, TimeUnit.SECONDS))
            CheckInClient.queue {
                CheckInClient.send(host.context, base, key, token, "user-1", 1, yesterday, SystemClock.elapsedRealtime() + 8000)
            }

            assertEquals("/api/widget/check-in", server.takeRequest(2, TimeUnit.SECONDS)?.path)
            assertEquals(1L, finished.count)
            origin.release()
            assertTrue(finished.await(5, TimeUnit.SECONDS))
        }
    }

    @Test
    fun aRowEditOrAnAppPushDuringTheFetchDiscardsItsAnswer() {
        val walk = day(host.habit(2, "Walk")).toString()
        val changes = listOf(
            {
                val token = WidgetStore.stored(host.context)!!.getString("token")
                WidgetStore.editRow(host.context, token, yesterday, 1) { row ->
                    row.put("state", "done").put("count", 1)
                    true
                }
            },
            { host.today(host.habit(3, "Read")) },
        )
        for (change in changes) {
            host.today(host.habit(1, "Water"), date = yesterday)
            var changed: String? = null
            server.dispatcher = object : Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse {
                    change()
                    changed = WidgetStore.stored(host.context).toString()
                    return MockResponse().setBody(walk)
                }
            }

            assertFalse(fetch())

            assertEquals(changed, WidgetStore.stored(host.context).toString())
        }
    }
}
