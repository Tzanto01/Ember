package app.ember.habits;

import static org.junit.Assert.assertArrayEquals;
import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertNotEquals;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public class WidgetPaletteTest {

    private static final String[] THEMES = { "hearthlight", "fieldnotes", "tidepool", "marigold" };

    @Test
    public void hearthlightKeepsTheEmberWidgetColors() {
        int[] light = WidgetPalette.of("hearthlight", false);
        assertEquals(0xFFFFFCF8, light[WidgetPalette.BACKGROUND]);
        assertEquals(0xFF2C2019, light[WidgetPalette.TEXT]);
        assertEquals(0xFF944E12, light[WidgetPalette.ACCENT]);
        int[] dark = WidgetPalette.of("hearthlight", true);
        assertEquals(0xFF2A1F19, dark[WidgetPalette.BACKGROUND]);
        assertEquals(0xFFF1AF64, dark[WidgetPalette.ACCENT]);
    }

    @Test
    public void unknownOrMissingThemeIsHearthlight() {
        assertArrayEquals(WidgetPalette.of("hearthlight", false), WidgetPalette.of("neon", false));
        assertArrayEquals(WidgetPalette.of("hearthlight", true), WidgetPalette.of(null, true));
    }

    @Test
    public void everyThemeHasItsOwnReadableLightAndDarkColors() {
        for (String theme : THEMES) {
            int[] light = WidgetPalette.of(theme, false);
            int[] dark = WidgetPalette.of(theme, true);
            assertNotEquals(theme, light[WidgetPalette.BACKGROUND], dark[WidgetPalette.BACKGROUND]);
            assertTrue(theme, luminance(light[WidgetPalette.BACKGROUND]) > luminance(light[WidgetPalette.TEXT]));
            assertTrue(theme, luminance(dark[WidgetPalette.BACKGROUND]) < luminance(dark[WidgetPalette.TEXT]));
            if (!"hearthlight".equals(theme)) {
                int hearthlight = WidgetPalette.of("hearthlight", false)[WidgetPalette.ACCENT];
                assertNotEquals(theme, hearthlight, light[WidgetPalette.ACCENT]);
            }
        }
    }

    @Test
    public void colorSchemeOverridesTheSystemUnlessSetToSystem() {
        assertTrue(WidgetPalette.dark("dark", false));
        assertFalse(WidgetPalette.dark("light", true));
        assertTrue(WidgetPalette.dark("system", true));
        assertFalse(WidgetPalette.dark("system", false));
        assertTrue(WidgetPalette.dark(null, true));
    }

    private static int luminance(int color) {
        return ((color >> 16) & 0xFF) * 3 + ((color >> 8) & 0xFF) * 6 + (color & 0xFF);
    }
}
