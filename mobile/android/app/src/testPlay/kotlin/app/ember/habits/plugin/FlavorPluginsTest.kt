package app.ember.habits.plugin

import org.junit.Assert.assertTrue
import org.junit.Test

/** The web bundle takes a missing updater for the Play build: there it hides the tip link and the self-update. */
class FlavorPluginsTest {

    @Test
    fun thePlayBuildRegistersNoUpdater() {
        assertTrue(FlavorPlugins.all().none { it.simpleName == "EmberUpdaterPlugin" })
    }
}
