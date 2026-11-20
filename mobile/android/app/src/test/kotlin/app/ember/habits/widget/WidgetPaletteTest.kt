package app.ember.habits.widget

import java.io.File
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class WidgetPaletteTest {

    private val themes = listOf("hearthlight", "fieldnotes", "tidepool", "marigold")

    /** The `ember_widget_*` colors of one `ember_colors.xml` as opaque ARGB ints, by name. */
    private fun widgetColors(values: String): Map<String, Int> =
        Regex("""<color name="ember_widget_(\w+)">#([0-9A-Fa-f]{6})</color>""")
            .findAll(File("src/main/res/$values/ember_colors.xml").readText())
            .associate { it.groupValues[1] to (0xFF000000 or it.groupValues[2].toLong(16)).toInt() }

    @Test
    fun hearthlightMatchesTheEmberColorResourcesInLightAndDark() {
        for ((values, dark) in listOf("values" to false, "values-night" to true)) {
            val resources = widgetColors(values)
            val palette = WidgetPalette.of("hearthlight", dark)
            assertEquals(values, 5, resources.size)
            assertEquals(values, resources["background"], palette[WidgetPalette.BACKGROUND])
            assertEquals(values, resources["text"], palette[WidgetPalette.TEXT])
            assertEquals(values, resources["muted"], palette[WidgetPalette.MUTED])
            assertEquals(values, resources["accent"], palette[WidgetPalette.ACCENT])
            assertEquals(values, resources["border"], palette[WidgetPalette.BORDER])
        }
    }

    @Test
    fun unknownOrMissingThemeIsHearthlight() {
        assertArrayEquals(WidgetPalette.of("hearthlight", false), WidgetPalette.of("neon", false))
        assertArrayEquals(WidgetPalette.of("hearthlight", true), WidgetPalette.of(null, true))
        assertTrue(WidgetPalette.hearthlight(null))
    }

    @Test
    fun everyThemeHasItsOwnReadableLightAndDarkColors() {
        val hearthlightAccent = WidgetPalette.of("hearthlight", false)[WidgetPalette.ACCENT]
        for (theme in themes) {
            val light = WidgetPalette.of(theme, false)
            val dark = WidgetPalette.of(theme, true)
            assertNotEquals(theme, light[WidgetPalette.BACKGROUND], dark[WidgetPalette.BACKGROUND])
            assertNotEquals(theme, light[WidgetPalette.ACCENT], light[WidgetPalette.BORDER])
            assertNotEquals(theme, dark[WidgetPalette.ACCENT], dark[WidgetPalette.BORDER])
            assertEquals(theme, theme == "hearthlight", WidgetPalette.hearthlight(theme))
            assertTrue(theme, luminance(light[WidgetPalette.BACKGROUND]) > luminance(light[WidgetPalette.TEXT]))
            assertTrue(theme, luminance(dark[WidgetPalette.BACKGROUND]) < luminance(dark[WidgetPalette.TEXT]))
            if (theme != "hearthlight") assertNotEquals(theme, hearthlightAccent, light[WidgetPalette.ACCENT])
        }
    }

    @Test
    fun colorSchemeOverridesTheSystemUnlessSetToSystem() {
        assertTrue(WidgetPalette.dark("dark", false))
        assertFalse(WidgetPalette.dark("light", true))
        assertTrue(WidgetPalette.dark("system", true))
        assertFalse(WidgetPalette.dark("system", false))
        assertTrue(WidgetPalette.dark(null, true))
    }

    private fun luminance(color: Int) = (color shr 16 and 0xFF) * 3 + (color shr 8 and 0xFF) * 6 + (color and 0xFF)
}
