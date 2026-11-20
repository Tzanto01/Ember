package app.ember.habits

/**
 * The widget's colors per Ember theme, light and dark, from the web theme tokens (card, foreground,
 * muted-foreground, primary, done, border) as ARGB ints. Hearthlight matches `ember_colors`. Marigold light
 * takes a lighter border than its token, so open dots stand apart from done ones.
 */
internal object WidgetPalette {

    const val BACKGROUND = 0
    const val TEXT = 1
    const val MUTED = 2
    const val ACCENT = 3
    const val DONE = 4
    const val BORDER = 5

    private val HEARTHLIGHT = argb(0xFFFFFCF8, 0xFF2C2019, 0xFF665448, 0xFF944E12, 0xFFAE5C01, 0xFFE7DCD0)
    private val HEARTHLIGHT_DARK = argb(0xFF2A1F19, 0xFFF1EAE0, 0xFFC1B1A0, 0xFFF1AF64, 0xFFE79647, 0xFF3F322B)
    private val FIELDNOTES = argb(0xFFFCFAF3, 0xFF1A222E, 0xFF4B5763, 0xFF345B35, 0xFF427044, 0xFFD5D1C3)
    private val FIELDNOTES_DARK = argb(0xFF1C241E, 0xFFECE8DC, 0xFFB4B2A3, 0xFFA9D29C, 0xFF89BB7E, 0xFF2E3831)
    private val TIDEPOOL = argb(0xFFFBFEFF, 0xFF182933, 0xFF4B5E6A, 0xFF00646D, 0xFF087479, 0xFFD5E0E5)
    private val TIDEPOOL_DARK = argb(0xFF12212B, 0xFFE4EDF0, 0xFFA4B8C0, 0xFF7DD5D8, 0xFF5BC1C0, 0xFF243540)
    private val MARIGOLD = argb(0xFFFFFEF5, 0xFF211910, 0xFF584A3B, 0xFF292016, 0xFF2E2419, 0xFFD3C9A9)
    private val MARIGOLD_DARK = argb(0xFF251E15, 0xFFF7EFD1, 0xFFC1B79B, 0xFFF6C755, 0xFFF6C343, 0xFF937D4E)

    private fun argb(vararg colors: Long) = IntArray(colors.size) { colors[it].toInt() }

    /** The colors of [theme] (unknown or missing is Hearthlight), indexed by the constants above. */
    @JvmStatic
    fun of(theme: String?, dark: Boolean): IntArray {
        val colors = when (theme) {
            "fieldnotes" -> if (dark) FIELDNOTES_DARK else FIELDNOTES
            "tidepool" -> if (dark) TIDEPOOL_DARK else TIDEPOOL
            "marigold" -> if (dark) MARIGOLD_DARK else MARIGOLD
            else -> if (dark) HEARTHLIGHT_DARK else HEARTHLIGHT
        }
        return colors.clone()
    }

    /** True for Hearthlight, the theme of the `ember_colors` resources (unknown or missing included). */
    @JvmStatic
    fun hearthlight(theme: String?) = theme != "fieldnotes" && theme != "tidepool" && theme != "marigold"

    /** Dark for `"dark"`, light for `"light"`, else (system or missing) [systemDark]. */
    @JvmStatic
    fun dark(colorScheme: String?, systemDark: Boolean) = when (colorScheme) {
        "dark" -> true
        "light" -> false
        else -> systemDark
    }
}
