package app.ember.habits.plugin

import android.content.Context
import android.content.Intent

/**
 * Points to the system's own screen-time screen. Ember only opens it and never reads usage data: no usage-stats
 * permission, only `<queries>` for the candidates below.
 */
internal object ScreenTime {

    /** In order of preference: {package, activity} of the system's screen-time app. */
    val CANDIDATES: List<Array<String>> = listOf(
        arrayOf("com.google.android.apps.wellbeing", "com.google.android.apps.wellbeing.settings.TopLevelSettingsActivity"),
        arrayOf("com.samsung.android.forest", "com.samsung.android.forest.launcher.LauncherActivity"),
    )

    /** The first of [candidates] that [opens], or null. */
    fun first(candidates: List<Array<String>>, opens: (Array<String>) -> Boolean): Array<String>? = candidates.firstOrNull(opens)

    /** An intent for the first candidate with an exported activity on this device, or null. */
    fun intent(context: Context): Intent? {
        val candidate = first(CANDIDATES) {
            val info = context.packageManager.resolveActivity(Intent().setClassName(it[0], it[1]), 0)
            info?.activityInfo?.exported == true
        } ?: return null
        return Intent().setClassName(candidate[0], candidate[1])
    }
}
