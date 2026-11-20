package app.ember.habits

import android.content.Context
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class WidgetKeyOriginTest {

    @Test
    fun acceptsHttpsOriginAndDropsTrailingSlash() {
        assertEquals("https://api.example.test", WidgetKey.origin("https://api.example.test"))
        assertEquals("https://api.example.test", WidgetKey.origin("https://api.example.test/"))
    }

    @Test
    fun keepsExplicitPort() {
        assertEquals("https://api.example.test:8443", WidgetKey.origin("https://api.example.test:8443"))
        assertEquals("https://api.example.test:8443", WidgetKey.origin("https://api.example.test:8443/"))
    }

    @Test
    fun rejectsPlainHttp() {
        assertNull(WidgetKey.origin("http://api.example.test"))
    }

    @Test
    fun rejectsUserInfo() {
        assertNull(WidgetKey.origin("https://user:secret@api.example.test"))
        assertNull(WidgetKey.origin("https://evil.example.test@api.example.test"))
    }

    @Test
    fun rejectsPath() {
        assertNull(WidgetKey.origin("https://api.example.test/api"))
        assertNull(WidgetKey.origin("https://api.example.test//"))
    }

    @Test
    fun rejectsQuery() {
        assertNull(WidgetKey.origin("https://api.example.test/?next=x"))
        assertNull(WidgetKey.origin("https://api.example.test?"))
    }

    @Test
    fun rejectsFragment() {
        assertNull(WidgetKey.origin("https://api.example.test/#x"))
        assertNull(WidgetKey.origin("https://api.example.test#"))
    }

    @Test
    fun rejectsEmptyMissingAndGarbage() {
        assertNull(WidgetKey.origin(null))
        assertNull(WidgetKey.origin(""))
        assertNull(WidgetKey.origin("https://"))
        assertNull(WidgetKey.origin("https:api.example.test"))
        assertNull(WidgetKey.origin("not a url"))
        assertNull(WidgetKey.origin("//api.example.test"))
    }

    @Test
    fun storedOriginIsCheckedAgainWhenRead() {
        val context = InMemoryContext()
        assertNull(WidgetKey.apiBase(context))
        val cases = listOf(
            "https://api.example.test/" to "https://api.example.test",
            "http://api.example.test" to null,
            "https://api.example.test/api" to null,
            "https://user@api.example.test" to null,
        )
        for ((stored, read) in cases) {
            context.getSharedPreferences("ember_widget", Context.MODE_PRIVATE).edit().putString("apiBase", stored).commit()
            assertEquals(stored, read, WidgetKey.apiBase(context))
        }
    }
}
