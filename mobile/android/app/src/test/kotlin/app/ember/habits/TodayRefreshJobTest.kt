package app.ember.habits

import android.app.AlarmManager
import android.app.job.JobInfo
import android.app.job.JobScheduler
import android.appwidget.AppWidgetManager
import android.content.Intent
import app.ember.habits.widget.WidgetKey
import app.ember.habits.widget.WidgetStore
import java.time.LocalDate
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config

/**
 * When the Today widget asks for its own day: at midnight at most every minute, else at most every 15 minutes, only
 * with a key, and never two fetches at once.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [36])
class TodayRefreshJobTest {

    private lateinit var host: TodayHost
    private val scheduler get() = host.context.getSystemService(JobScheduler::class.java)

    @Before
    fun setUp() {
        host = TodayHost()
    }

    private fun jobs(): List<JobInfo> = scheduler.allPendingJobs

    private fun midnight() = TodayWidget().onReceive(host.context, Intent(TodayWidget.ACTION_MIDNIGHT))

    private fun update() =
        TodayWidget().onUpdate(host.context, AppWidgetManager.getInstance(host.context), intArrayOf(host.id))

    private fun lastRequestAt(at: Long) =
        WidgetStore.prefs(host.context).edit().putLong(WidgetStore.DAY_REQUESTED, at).commit()

    private fun stale() = host.today(host.habit(1, "Water"), date = LocalDate.parse(host.today).minusDays(2).toString())

    @Suppress("DEPRECATION")
    private fun alarm() = shadowOf(host.context.getSystemService(AlarmManager::class.java)).nextScheduledAlarm

    @Test
    fun midnightAsksForTheDayAMinuteAfterAnotherRequestButNotSooner() {
        host.key()
        host.today(host.habit(1, "Water"))
        update()
        scheduler.cancelAll()

        midnight()
        assertEquals(0, jobs().size)

        lastRequestAt(System.currentTimeMillis() - 61 * 1000)
        midnight()

        val job = jobs().single()
        assertEquals(TodayRefreshJob::class.java.name, job.service.className)
        assertEquals(JobInfo.NETWORK_TYPE_ANY, job.networkType)
        assertFalse(job.isPersisted)
    }

    @Test
    fun aWidgetUpdateAsksOnlyFifteenMinutesAfterTheLastRequest() {
        host.key()
        host.today(host.habit(1, "Water"))

        update()
        assertEquals(1, jobs().size)
        scheduler.cancelAll()

        lastRequestAt(System.currentTimeMillis() - 14 * 60 * 1000)
        update()
        assertEquals(0, jobs().size)

        lastRequestAt(System.currentTimeMillis() - 16 * 60 * 1000)
        update()
        assertEquals(1, jobs().size)
        scheduler.cancelAll()

        lastRequestAt(System.currentTimeMillis() + 60 * 60 * 1000)
        update()
        assertEquals(1, jobs().size)
    }

    @Test
    fun aRedrawAsksOnlyWhenTheSnapshotIsStaleAndKeepsTheMidnightAlarmFromItsZone() {
        host.key()
        host.today(host.habit(1, "Water"))
        TodayWidget.refresh(host.context)
        assertEquals(0, jobs().size)

        stale()
        lastRequestAt(System.currentTimeMillis() - 16 * 60 * 1000)
        host.context.getSystemService(AlarmManager::class.java).cancel(alarm()!!.operation!!)
        TodayWidget.refresh(host.context)

        assertEquals(1, jobs().size)
        assertNotNull(alarm())
        scheduler.cancelAll()
        TodayWidget.refresh(host.context)
        assertEquals(0, jobs().size)
    }

    @Test
    fun jobsStartedDuringAFetchShareOneMoreFetchAfterIt() {
        HangingOrigin().use { origin ->
            FakeAndroidKeyStore.install()
            WidgetKey.set(host.context, "k".repeat(43), origin.base)
            host.today(host.habit(1, "Water"))
            val finished = CountDownLatch(3)

            TodayRefreshJob.run(host.context) { finished.countDown() }
            assertTrue(origin.accepted.tryAcquire(2, TimeUnit.SECONDS))
            TodayRefreshJob.run(host.context) { finished.countDown() }
            TodayRefreshJob.run(host.context) { finished.countDown() }
            assertFalse(origin.accepted.tryAcquire(500, TimeUnit.MILLISECONDS))
            origin.release()

            assertTrue(finished.await(5, TimeUnit.SECONDS))
            assertEquals(1, origin.accepted.availablePermits())
        }
    }

    @Test
    fun aJobThatThrowsOnFinishingNeitherStopsItsBatchNorLaterFetches() {
        HangingOrigin().use { origin ->
            FakeAndroidKeyStore.install()
            WidgetKey.set(host.context, "k".repeat(43), origin.base)
            host.today(host.habit(1, "Water"))
            val finished = CountDownLatch(2)

            TodayRefreshJob.run(host.context) { throw IllegalStateException("finished twice") }
            assertTrue(origin.accepted.tryAcquire(2, TimeUnit.SECONDS))
            TodayRefreshJob.run(host.context) { finished.countDown() }
            origin.release()
            assertTrue(origin.accepted.tryAcquire(2, TimeUnit.SECONDS))
            TodayRefreshJob.run(host.context) { finished.countDown() }

            assertTrue(finished.await(5, TimeUnit.SECONDS))
        }
    }

    @Test
    fun withoutAKeyNothingAsksAndAStaleSnapshotSetsNoAlarm() {
        stale()

        midnight()
        update()
        TodayWidget.refresh(host.context)

        assertEquals(0, jobs().size)
        assertNull(alarm())
    }
}
