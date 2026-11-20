package app.ember.habits

import android.os.SystemClock
import java.util.concurrent.TimeUnit
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * One widget check-in request against a local server: what it sends, which answers update the row and settle the
 * pending check-in, and that every failure leaves the check-in pending for JS to replay.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [30])
class CheckInClientTest {

    private lateinit var host: TodayHost
    private val server = MockWebServer()
    private val key = "k".repeat(43)
    private val confirmed = """{"state":"done","count":2,"timesPerDay":2}"""

    @Before
    fun setUp() {
        host = TodayHost()
        server.start()
        host.today(host.habit(5, "Walk", "partial", 1, 2))
        WidgetStore.addPending(host.context, "user-1", 5, host.today, 2)
    }

    @After
    fun tearDown() {
        server.shutdown()
    }

    private fun answer(status: Int, body: String = "") = server.enqueue(MockResponse().setResponseCode(status).setBody(body))

    private fun send(deadline: Long = SystemClock.elapsedRealtime() + 8000, alsoInFlight: Int = 0) {
        val base = server.url("/").toString().removeSuffix("/")
        WidgetStore.inFlight("user-1", 5, host.today, 1 + alsoInFlight)
        try {
            TodayWidget.send(host.context, base, key, host.token, "user-1", 5, host.today, deadline)
        } finally {
            WidgetStore.inFlight("user-1", 5, host.today, -1 - alsoInFlight)
        }
    }

    private fun row() = WidgetStore.current(host.context)!!.getJSONArray("habits").getJSONObject(0)

    private fun pendingCount(): Int? {
        val pending = WidgetStore.takePending(host.context)
        return if (pending.length() == 0) null else pending.getJSONObject(0).getInt("count")
    }

    @Test
    fun postsTheHabitAndDayWithTheWidgetKey() {
        answer(200, confirmed)

        send()

        val request = server.takeRequest(1, TimeUnit.SECONDS)!!
        assertEquals("POST", request.method)
        assertEquals("/api/widget/check-in", request.path)
        assertEquals("Widget $key", request.getHeader("Authorization"))
        assertEquals("application/json", request.getHeader("Content-Type"))
        assertEquals("application/json", request.getHeader("Accept"))
        val body = JSONObject(request.body.readUtf8())
        assertEquals(setOf("habitId", "date"), body.keys().asSequence().toSet())
        assertEquals(5L, body.getLong("habitId"))
        assertEquals(host.today, body.getString("date"))
    }

    @Test
    fun aConfirmedCheckInUpdatesTheRowAndLeavesNothingPending() {
        answer(200, confirmed)

        send()

        assertEquals("done", row().getString("state"))
        assertEquals(2, row().getInt("count"))
        assertNull(pendingCount())
    }

    @Test
    fun anAnswerBelowTheTappedCountUpdatesTheRowButKeepsTheCheckInPending() {
        answer(200, """{"state":"partial","count":1,"timesPerDay":3}""")

        send()

        assertEquals(3, row().getInt("timesPerDay"))
        assertEquals(2, pendingCount())
    }

    @Test
    fun anAnswerWhileAnotherTapOnTheRowIsInFlightLeavesTheRowToThatTap() {
        answer(200, confirmed)

        send(alsoInFlight = 1)

        assertEquals("partial", row().getString("state"))
        assertEquals(1, row().getInt("count"))
        assertNull(pendingCount())
    }

    @Test
    fun failedOrInvalidAnswersLeaveTheRowAndTheCheckInPending() {
        val answers = listOf(
            MockResponse().setResponseCode(500).setBody(confirmed),
            MockResponse().setResponseCode(401),
            MockResponse().setResponseCode(302).setHeader("Location", server.url("/elsewhere")),
            MockResponse().setBody("not json"),
            MockResponse().setBody("""{"state":"skipped","count":2,"timesPerDay":2}"""),
            MockResponse().setBody("""{"state":"done","count":-1,"timesPerDay":2}"""),
            MockResponse().setBody("""{"state":"done","count":2,"timesPerDay":0}"""),
            MockResponse().setBody("""{"state":"done","count":2}"""),
            MockResponse().setBody("""{"state":"done","count":2,"timesPerDay":2,"pad":"${"x".repeat(5000)}"}"""),
        )
        for (each in answers) {
            server.enqueue(each)
            send()
            assertEquals(each.toString(), "partial", row().getString("state"))
            assertEquals(1, row().getInt("count"))
            assertEquals(2, pendingCount())
            WidgetStore.addPending(host.context, "user-1", 5, host.today, 2)
        }
        assertEquals(answers.size, server.requestCount)
    }

    @Test
    fun aRejectedKeyIsErasedAndAnyOtherFailureKeepsIt() {
        FakeAndroidKeyStore.install()
        WidgetKey.set(host.context, key, "https://ember.example")

        answer(500)
        send()
        assertNotNull(WidgetKey.get(host.context))

        answer(401)
        send()
        assertNull(WidgetKey.get(host.context))
        assertEquals("https://ember.example", WidgetKey.apiBase(host.context))
    }

    @Test
    fun aServerSlowerThanTheTapsBudgetIsCutOffAndTheCheckInStaysPending() {
        server.enqueue(MockResponse().setBody(confirmed).setHeadersDelay(1500, TimeUnit.MILLISECONDS))

        val started = System.nanoTime()
        send(deadline = SystemClock.elapsedRealtime() + 1000)
        val tookMs = (System.nanoTime() - started) / 1_000_000

        assertTrue("took $tookMs ms", tookMs < 2500)
        assertEquals(1, row().getInt("count"))
        assertEquals(2, pendingCount())
    }

    @Test
    fun withLessThanASecondLeftNoRequestIsSent() {
        answer(200, confirmed)

        send(deadline = SystemClock.elapsedRealtime() + 999)

        assertEquals(0, server.requestCount)
        assertEquals(2, pendingCount())
    }
}
