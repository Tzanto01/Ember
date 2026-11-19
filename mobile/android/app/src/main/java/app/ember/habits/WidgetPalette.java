package app.ember.habits;

/**
 * The widget's colors per Ember theme, light and dark, from the web theme tokens (card, foreground,
 * muted-foreground, primary, done, border) as ARGB ints. Hearthlight matches {@code ember_colors}. Marigold light
 * takes a lighter border than its token, so open dots stand apart from done ones.
 */
final class WidgetPalette {

    static final int BACKGROUND = 0;
    static final int TEXT = 1;
    static final int MUTED = 2;
    static final int ACCENT = 3;
    static final int DONE = 4;
    static final int BORDER = 5;

    private static final int[] HEARTHLIGHT = { 0xFFFFFCF8, 0xFF2C2019, 0xFF665448, 0xFF944E12, 0xFFAE5C01, 0xFFE7DCD0 };
    private static final int[] HEARTHLIGHT_DARK = { 0xFF2A1F19, 0xFFF1EAE0, 0xFFC1B1A0, 0xFFF1AF64, 0xFFE79647, 0xFF3F322B };
    private static final int[] FIELDNOTES = { 0xFFFCFAF3, 0xFF1A222E, 0xFF4B5763, 0xFF345B35, 0xFF427044, 0xFFD5D1C3 };
    private static final int[] FIELDNOTES_DARK = { 0xFF1C241E, 0xFFECE8DC, 0xFFB4B2A3, 0xFFA9D29C, 0xFF89BB7E, 0xFF2E3831 };
    private static final int[] TIDEPOOL = { 0xFFFBFEFF, 0xFF182933, 0xFF4B5E6A, 0xFF00646D, 0xFF087479, 0xFFD5E0E5 };
    private static final int[] TIDEPOOL_DARK = { 0xFF12212B, 0xFFE4EDF0, 0xFFA4B8C0, 0xFF7DD5D8, 0xFF5BC1C0, 0xFF243540 };
    private static final int[] MARIGOLD = { 0xFFFFFEF5, 0xFF211910, 0xFF584A3B, 0xFF292016, 0xFF2E2419, 0xFFD3C9A9 };
    private static final int[] MARIGOLD_DARK = { 0xFF251E15, 0xFFF7EFD1, 0xFFC1B79B, 0xFFF6C755, 0xFFF6C343, 0xFF937D4E };

    private WidgetPalette() {}

    /** The colors of {@code theme} (unknown or missing is Hearthlight), indexed by the constants above. */
    static int[] of(String theme, boolean dark) {
        int[] colors;
        if ("fieldnotes".equals(theme)) colors = dark ? FIELDNOTES_DARK : FIELDNOTES;
        else if ("tidepool".equals(theme)) colors = dark ? TIDEPOOL_DARK : TIDEPOOL;
        else if ("marigold".equals(theme)) colors = dark ? MARIGOLD_DARK : MARIGOLD;
        else colors = dark ? HEARTHLIGHT_DARK : HEARTHLIGHT;
        return colors.clone();
    }

    /** True for Hearthlight, the theme of the {@code ember_colors} resources (unknown or missing included). */
    static boolean hearthlight(String theme) {
        return !"fieldnotes".equals(theme) && !"tidepool".equals(theme) && !"marigold".equals(theme);
    }

    /** Dark for {@code "dark"}, light for {@code "light"}, else (system or missing) {@code systemDark}. */
    static boolean dark(String colorScheme, boolean systemDark) {
        if ("dark".equals(colorScheme)) return true;
        if ("light".equals(colorScheme)) return false;
        return systemDark;
    }
}
