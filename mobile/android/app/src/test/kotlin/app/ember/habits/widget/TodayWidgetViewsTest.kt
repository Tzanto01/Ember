package app.ember.habits.widget

import android.content.Intent
import android.view.View
import android.widget.CompoundButton
import android.widget.ImageView
import app.ember.habits.AppLanguage
import app.ember.habits.MainActivity
import app.ember.habits.R
import app.ember.habits.TodayHost
import app.ember.habits.TodayWidget
import app.ember.habits.all
import app.ember.habits.names
import app.ember.habits.shows
import app.ember.habits.text
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config

/**
 * What the "Today in Ember" widget shows at each size and where its taps lead, rendered the way a launcher inflates
 * it. Robolectric, because the output is RemoteViews only a real view tree can show.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [30])
class TodayWidgetViewsTest {

    private lateinit var host: TodayHost

    @Before
    fun setUp() {
        host = TodayHost()
    }

    private class Row(val name: String, val label: String?)

    private fun View.rows() = all(R.id.widget_row).map {
        Row(it.text(R.id.widget_row_name), if (it.shows(R.id.widget_row_state)) it.text(R.id.widget_row_state) else null)
    }

    private fun View.tap(id: Int): Intent {
        findViewById<View>(id).performClick()
        return shadowOf(RuntimeEnvironment.getApplication()).nextStartedActivity
    }

    @Test
    fun withoutTodaysSnapshotTheWidgetAsksToOpenEmber() {
        val signedOut = host.view(250f, 296f)
        assertTrue(signedOut.shows(R.id.widget_empty))
        assertEquals("Open Ember to see today", signedOut.text(R.id.widget_empty))
        assertFalse(signedOut.shows(R.id.widget_count))
        assertEquals(emptyList<String>(), signedOut.names())

        host.today(host.habit(1, "Water"), date = "2020-01-01")
        val stale = host.view(250f, 296f)
        assertEquals("Open Ember to refresh", stale.text(R.id.widget_empty))
        assertEquals(emptyList<String>(), stale.names())
    }

    @Test
    fun anEmptyDaySaysNothingIsPlanned() {
        host.today()

        val view = host.view(250f, 296f)

        assertEquals("Nothing planned today. Enjoy the rest.", view.text(R.id.widget_empty))
        assertFalse(view.shows(R.id.widget_count))
        assertFalse(view.shows(R.id.widget_more))
    }

    @Test
    fun aSmallWidgetShowsTheNextOpenHabitAloneWithOneDotPerHabit() {
        host.today(host.habit(1, "Water", "done", 1), host.habit(2, "Walk"), host.habit(3, "Read"))

        val view = host.view(180f, 110f)

        assertEquals(listOf("Walk"), view.names())
        assertFalse(view.shows(R.id.widget_title))
        assertEquals("1 of 3", view.text(R.id.widget_count))
        assertEquals("●", view.text(R.id.widget_dots_done))
        assertEquals("●●", view.text(R.id.widget_dots_open))
        assertTrue(view.shows(R.id.widget_dots_done) && view.shows(R.id.widget_dots_open))
        assertFalse(host.view(110f, 110f).shows(R.id.widget_dots_open))
    }

    @Test
    fun aShortWidgetListsWhatFitsFromTheFirstOpenHabit() {
        host.today(host.habit(1, "Water", "done", 1), host.habit(2, "Walk"), host.habit(3, "Read"), host.habit(4, "Stretch"))

        val view = host.view(250f, 152f)

        assertTrue(view.shows(R.id.widget_title))
        assertEquals("Today", view.text(R.id.widget_title))
        assertEquals(listOf("Walk", "Read"), view.names())
        assertFalse(view.shows(R.id.widget_more))
    }

    @Test
    fun aTallWidgetListsTheWholeDayAndCountsWhatDoesNotFit() {
        val habits = (1L..6L).map { host.habit(it, "Habit $it", if (it == 1L) "done" else "open", if (it == 1L) 1 else 0) }
        host.today(*habits.toTypedArray(), more = 2)

        val view = host.view(250f, 296f)

        assertEquals(listOf("Habit 1", "Habit 2", "Habit 3", "Habit 4"), view.names())
        assertEquals("+4 more", view.text(R.id.widget_more))
        assertTrue(view.shows(R.id.widget_more))
        assertEquals("1 of 8", view.text(R.id.widget_count))
        assertFalse(view.shows(R.id.widget_dots_done))
    }

    @Test
    fun aDayWithNothingLeftToCheckInSaysAllDoneAtEverySize() {
        host.today(host.habit(1, "Water", "done", 1), host.habit(2, "Walk", "rest"))

        for (size in listOf(110f to 110f, 250f to 152f, 250f to 392f)) {
            val view = host.view(size.first, size.second)
            assertTrue(view.shows(R.id.widget_single))
            assertEquals("All done for today", view.text(R.id.widget_single_name))
            assertTrue(view.rows().isEmpty())
            assertFalse(view.shows(R.id.widget_empty))
        }
    }

    @Test
    fun rowsLabelProgressRestAndCheckInsNotSavedYet() {
        host.today(
            host.habit(1, "Water", "done", 1),
            host.habit(2, "Walk", "partial", 1, 3),
            host.habit(3, "Swim", "rest"),
            host.habit(4, "Read"),
            host.habit(5, "Stretch"),
        )
        WidgetStore.addPending(host.context, "user-1", 4, host.today, 1)

        val rows = host.view(250f, 344f).rows()

        assertEquals(listOf("Water", "Walk", "Swim", "Read", "Stretch"), rows.map { it.name })
        assertEquals(listOf(null, "1 of 3", "Rest", "Not done yet · not saved yet", null), rows.map { it.label })
    }

    @Test
    fun withoutAWidgetKeyTapsOpenTheAppAtTodayOrTheTappedHabit() {
        host.today(host.habit(1, "Water", "done", 1), host.habit(2, "Walk"))
        val view = host.view(250f, 296f)
        val rows = view.all(R.id.widget_row)

        val checkIn = rows[1].let { it.performClick(); shadowOf(RuntimeEnvironment.getApplication()).nextStartedActivity }
        assertEquals(TodayWidget.ACTION_CHECK_IN, checkIn.action)
        assertEquals(MainActivity::class.java.name, checkIn.component!!.className)
        assertEquals("ember://widget/check-in/2/${host.today}", checkIn.dataString)
        assertEquals(2L, checkIn.getLongExtra("habitId", 0))
        assertEquals(host.today, checkIn.getStringExtra("date"))
        assertEquals("user-1", checkIn.getStringExtra("userId"))
        assertEquals(host.token, checkIn.getStringExtra("token"))

        val open = rows[0].let { it.performClick(); shadowOf(RuntimeEnvironment.getApplication()).nextStartedActivity }
        assertEquals(TodayWidget.ACTION_OPEN_HABIT, open.action)
        assertEquals(1L, open.getLongExtra("habitId", 0))

        val today = view.tap(R.id.widget_root)
        assertEquals(TodayWidget.ACTION_OPEN_TODAY, today.action)
        assertEquals(MainActivity::class.java.name, today.component!!.className)
    }

    @Test
    fun textFollowsTheAppLanguage() {
        AppLanguage.set(host.context, "de")
        host.today(host.habit(1, "Water", "done", 1))

        val view = host.view(250f, 296f)

        assertEquals("Heute", view.text(R.id.widget_title))
        assertEquals("Für heute alles erledigt", view.text(R.id.widget_single_name))
    }

    @Test
    fun theFlameFlickersOnlyOnceTheAppTurnedMotionOn() {
        host.today(host.habit(1, "Water"))
        val still = host.view(250f, 296f)
        assertTrue(still.shows(R.id.widget_flame))
        assertEquals(0, still.findViewById<android.view.ViewGroup>(R.id.widget_flicker_box).childCount)

        WidgetStore.setStyle(host.context, "hearthlight", true, "system")
        val moving = host.view(250f, 296f)
        assertFalse(moving.shows(R.id.widget_flame))
        assertEquals(1, moving.findViewById<android.view.ViewGroup>(R.id.widget_flicker_box).childCount)
    }

    @Test
    @Config(sdk = [36])
    fun withAKeyOnApi31OpenHabitsAreUncheckedCheckBoxesAndDoneOnesOpenTheHabit() {
        host.key()
        host.today(host.habit(1, "Water", "done", 1), host.habit(2, "Walk", "partial", 1, 2))

        for (size in listOf(110f to 110f, 250f to 296f)) {
            val circles = host.view(size.first, size.second).all(R.id.widget_row_circle)
            val walk = circles.last() as CompoundButton
            assertFalse(walk.isChecked)
            assertEquals("Check in Walk, 1 of 2", walk.contentDescription)
        }
        val water = host.view(250f, 296f).all(R.id.widget_row_circle).first()
        assertFalse(water is CompoundButton)
        water.performClick()
        val open = shadowOf(RuntimeEnvironment.getApplication()).nextStartedActivity
        assertEquals(TodayWidget.ACTION_OPEN_HABIT, open.action)
    }

    @Test
    @Config(sdk = [36])
    fun withoutAKeyOnApi31AnOpenHabitIsAPlainCircleThatOpensTheApp() {
        host.today(host.habit(2, "Walk"))

        val circle = host.view(250f, 296f).all(R.id.widget_row_circle).single()

        assertTrue(circle is ImageView && circle !is CompoundButton)
        circle.performClick()
        val tap = shadowOf(RuntimeEnvironment.getApplication()).nextStartedActivity
        assertEquals(TodayWidget.ACTION_CHECK_IN, tap.action)
        assertEquals(MainActivity::class.java.name, tap.component!!.className)
        assertNull(shadowOf(RuntimeEnvironment.getApplication()).nextStartedActivity)
    }
}
