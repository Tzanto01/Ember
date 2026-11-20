package app.ember.habits

import org.junit.Assert.assertEquals
import org.junit.Test

/** The widget language the web app sets is German or English, never anything the widgets have no strings for. */
class AppLanguageTest {

    private val context = InMemoryContext()

    private fun stored(language: String?): String {
        AppLanguage.set(context, language)
        return AppLanguage.get(context)
    }

    @Test
    fun keepsGermanAndTurnsEverythingElseIntoEnglish() {
        assertEquals("de", stored("de"))
        assertEquals("en", stored("en"))
        assertEquals("en", stored("fr"))
        assertEquals("en", stored(null))
    }
}
