package app.ember.habits

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class ScreenTimeTest {

    private fun pick(vararg installed: String) = ScreenTime.first(ScreenTime.CANDIDATES) { it[0] in installed }

    @Test
    fun prefersGoogleDigitalWellbeing() {
        assertEquals("com.google.android.apps.wellbeing", pick("com.samsung.android.forest", "com.google.android.apps.wellbeing")!![0])
    }

    @Test
    fun fallsBackToSamsung() {
        assertEquals("com.samsung.android.forest", pick("com.samsung.android.forest")!![0])
    }

    @Test
    fun nothingWhenNoCandidateOpens() {
        assertNull(pick())
    }
}
