package app.ember.habits

import android.app.AlarmManager
import android.content.ComponentName
import android.content.Intent
import android.os.Looper
import android.view.View
import android.widget.CompoundButton
import app.ember.habits.widget.CheckInClient
import app.ember.habits.widget.WidgetStore
import java.time.ZoneId
import java.time.ZonedDateTime
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import org.robolectric.shadows.ShadowAlarmManager

/** The widget receiver: background check-ins from a keyed tap, tap validation for the app, and the midnight redraw. */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [30])
class TodayWidgetTest {

    private lateinit var host: TodayHost

    @Before
    fun setUp() {
        host = TodayHost()
    }

    private fun idle() = shadowOf(Looper.getMainLooper()).idle()

    /** Waits until the check-ins queued so far, their redraw included, have finished. */
    private fun awaitCheckIns() {
        val done = CountDownLatch(1)
        CheckInClient.queue { done.countDown() }
        assertTrue(done.await(5, TimeUnit.SECONDS))
    }

    private fun row(habitId: Long) = WidgetStore.current(host.context)!!.getJSONArray("habits").let { habits ->
        (0 until habits.length()).map { habits.getJSONObject(it) }.first { it.getLong("id") == habitId }
    }

    private fun tap(
        action: String = TodayWidget.ACTION_CHECK_IN,
        target: Class<*> = MainActivity::class.java,
        habitId: Long = 2,
        date: String? = host.today,
        userId: String? = "user-1",
        token: String? = host.token,
    ) = Intent(action)
        .setComponent(ComponentName(host.context, target))
        .putExtra("habitId", habitId)
        .putExtra("date", date)
        .putExtra("userId", userId)
        .putExtra("token", token)

    @Test
    fun aKeyedTapChecksInAtOnceAndKeepsTheCheckInPendingUntilTheServerConfirmsIt() {
        host.key()
        host.today(host.habit(1, "Water", "done", 1), host.habit(2, "Walk", "open", 0, 2))

        host.view(250f, 296f).all(R.id.widget_row)[1].performClick()
        idle()
        awaitCheckIns()

        assertEquals("partial", row(2).getString("state"))
        assertEquals(1, row(2).getInt("count"))
        val label = host.view(250f, 296f).all(R.id.widget_row)[1].text(R.id.widget_row_state)
        assertEquals("1 of 2 · not saved yet", label)
        val pending = WidgetStore.takePending(host.context)
        assertEquals(1, pending.length())
        assertEquals(2L, pending.getJSONObject(0).getLong("habitId"))
        assertEquals(1, pending.getJSONObject(0).getInt("count"))
    }

    @Test
    @Config(sdk = [36])
    fun checkingAKeyedCheckBoxOnApi31BroadcastsTheRowsCheckInToTheWidget() {
        host.key()
        host.today(host.habit(1, "Water", "done", 1), host.habit(2, "Walk", "open", 0, 2))
        val token = host.token

        val walk = host.view(250f, 296f).all(R.id.widget_row_circle).filterIsInstance<CompoundButton>().single()
        walk.performClick()

        val tap = shadowOf(RuntimeEnvironment.getApplication()).broadcastIntents.single()
        assertEquals(TodayWidget.ACTION_CHECK_IN, tap.action)
        assertEquals(TodayWidget::class.java.name, tap.component!!.className)
        assertEquals("ember://widget/check-in/2/${host.today}", tap.dataString)
        assertEquals(2L, tap.getLongExtra("habitId", 0))
        assertEquals(host.today, tap.getStringExtra("date"))
        assertEquals("user-1", tap.getStringExtra("userId"))
        assertEquals(token, tap.getStringExtra("token"))
        idle()
        awaitCheckIns()
        assertEquals("partial", row(2).getString("state"))
    }

    @Test
    fun aBackgroundTapThatDoesNotNameTodaysRowChangesNothing() {
        host.key()
        host.today(host.habit(2, "Walk"))
        val token = host.token

        val taps = listOf(
            tap(target = TodayWidget::class.java, token = "0".repeat(32)),
            tap(target = TodayWidget::class.java, token = null),
            tap(target = TodayWidget::class.java, token = token, userId = "user-2"),
            tap(target = TodayWidget::class.java, token = token, date = "2020-01-01"),
            tap(target = TodayWidget::class.java, token = token, habitId = 3),
        )
        for (intent in taps) host.context.sendBroadcast(intent)
        idle()

        assertEquals("open", row(2).getString("state"))
        assertEquals(0, WidgetStore.takePending(host.context).length())
        assertEquals(0, WidgetStore.inFlightCount("user-1", 2, host.today))
    }

    @Test
    fun aTapIsHandedToTheAppOnlyForTodaysRowsFromOurWidget() {
        host.today(host.habit(2, "Walk"))

        val checkIn = TodayWidget.validTap(host.context, tap())!!
        assertEquals(2L, checkIn.getLong("habitId"))
        assertEquals(host.today, checkIn.getString("date"))
        assertEquals("user-1", checkIn.getString("userId"))
        assertTrue(!checkIn.has("open"))
        assertEquals(true, TodayWidget.validTap(host.context, tap(TodayWidget.ACTION_OPEN_HABIT))!!.getBoolean("open"))
        assertEquals(0, TodayWidget.validTap(host.context, tap(TodayWidget.ACTION_OPEN_TODAY))!!.length())

        val rejected = listOf(
            tap(target = TodayWidget::class.java),
            Intent(TodayWidget.ACTION_OPEN_TODAY).setComponent(ComponentName("com.example.other", MainActivity::class.java.name)),
            tap(action = Intent.ACTION_VIEW),
            tap(token = "0".repeat(32)),
            tap(token = null),
            tap(userId = "user-2"),
            tap(date = "2020-01-01"),
            tap(date = null),
            tap(habitId = 3),
            tap(habitId = 0),
        )
        for (intent in rejected) assertNull(intent.toString(), TodayWidget.validTap(host.context, intent))
    }

    /** Asserts the widget's next redraw is set for just after the coming midnight in [zone], and returns it. */
    @Suppress("DEPRECATION")
    private fun assertMidnightAlarm(zone: ZoneId): ShadowAlarmManager.ScheduledAlarm {
        val alarm = shadowOf(host.context.getSystemService(AlarmManager::class.java)).nextScheduledAlarm
        assertNotNull(alarm)
        val midnight = ZonedDateTime.now(zone).toLocalDate().plusDays(1).atTime(0, 0, 5).atZone(zone)
        assertEquals(AlarmManager.RTC, alarm!!.getType())
        assertEquals(midnight.toInstant().toEpochMilli(), alarm.getTriggerAtMs())
        assertEquals(TodayWidget.MIDNIGHT_WINDOW_MS, alarm.getWindowLengthMs())
        return alarm
    }

    @Test
    @Suppress("DEPRECATION")
    fun theWidgetRedrawsJustAfterMidnightInTheAccountsZone() {
        host.today(host.habit(1, "Water"))
        host.view(250f, 296f)

        val alarm = assertMidnightAlarm(ZoneId.of(host.zone))
        val intent = shadowOf(alarm.operation!!).savedIntent
        assertEquals("app.ember.habits.WIDGET_MIDNIGHT", intent.action)
        assertEquals(TodayWidget::class.java.name, intent.component!!.className)

        WidgetStore.save(host.context, "user-1", host.today, host.zone, org.json.JSONArray().put(host.habit(2, "Walk")), 0, null, null, null)
        alarm.operation!!.send()
        idle()
        val manager = android.appwidget.AppWidgetManager.getInstance(host.context)
        val view: View = shadowOf(manager).getViewFor(host.id)
        assertEquals(listOf("Walk"), view.names())
    }

    @Test
    fun aZoneThisDeviceDoesNotKnowRedrawsJustAfterMidnightInTheDevicesOwnZone() {
        val device = DeviceZone()
        device.use {
            host.today(host.habit(1, "Water"), date = device.today, zone = "Mars/Olympus_Mons")

            assertEquals(listOf("Water"), host.view(250f, 296f).names())
            assertMidnightAlarm(ZoneId.of(device.id))
        }
    }

    @Test
    fun aUtcAccountRedrawsJustAfterUtcMidnightWhateverTheDevicesZone() {
        val device = DeviceZone()
        device.use {
            host.today(host.habit(1, "Water"), date = device.utcToday, zone = "UTC")
            host.view(250f, 296f)

            assertMidnightAlarm(ZoneId.of("UTC"))
        }
    }
}
