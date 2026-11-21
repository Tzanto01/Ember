package app.ember.habits.diagnostics

import android.content.Intent
import app.ember.habits.MainActivity
import app.ember.habits.TodayWidget

/** What started a launch, as far as diagnostics may say: never a route, URI, extra value or content. */
internal enum class LaunchSource {
    USER,
    NOTIFICATION,
    SYSTEM,
    ;

    val code get() = name.lowercase()

    companion object {
        private val WIDGET_ACTIONS = setOf(
            TodayWidget.ACTION_CHECK_IN,
            TodayWidget.ACTION_OPEN_TODAY,
            TodayWidget.ACTION_OPEN_HABIT,
        )

        /** A launcher intent or a widget tap is [USER], a moment door [NOTIFICATION], anything else [SYSTEM]. */
        fun of(intent: Intent): LaunchSource = when {
            intent.action == Intent.ACTION_MAIN && intent.hasCategory(Intent.CATEGORY_LAUNCHER) -> USER
            intent.action in WIDGET_ACTIONS -> USER
            intent.action == MainActivity.ACTION_OPEN_MOMENT -> NOTIFICATION
            else -> SYSTEM
        }
    }
}

/**
 * Why [MainActivity] ignored a launch intent: [log] is its logcat wording, [code] its diagnostics value, where every
 * moment or widget rejection is `rejected`.
 */
internal enum class IgnoredRoute(val log: String, val code: String) {
    RESTORING("restoring", "restoring"),
    NO_BRIDGE("no bridge", "no_bridge"),
    FROM_HISTORY("from history", "from_history"),
    NO_WIDGET_PLUGIN("no widget plugin", "rejected"),
    MOMENT_WITHOUT_COMPONENT("moment without component", "rejected"),
    MOMENT_OTHER_PACKAGE("moment for another package", "rejected"),
    MOMENT_OTHER_ACTIVITY("moment for another activity", "rejected"),
    NO_VALID_TAP("no moment or valid widget tap", "rejected"),
}

/** What [MainActivity] does after its WebView renderer died. */
internal enum class RendererThen {
    RECREATE,
    FINISH,
    NONE,
    ;

    val code get() = name.lowercase()
}

/**
 * The fixed launch event vocabulary (spec section 2): `name key=value ...` with the keys in a fixed order and a key
 * whose value is unknown left out. Only these builders produce launch events.
 */
internal object LaunchEvents {

    private const val MAX_INT = 999_999_999L

    fun launch(cold: Boolean, saved: Boolean, source: LaunchSource, history: Boolean, webview: String?) = event(
        "launch",
        "cold" to cold.toString(),
        "saved" to saved.toString(),
        "source" to source.code,
        "history" to history.toString(),
        "webview" to webview?.takeIf(DiagnosticsGrammar.VERSION_VALUE::matches),
    )

    fun newIntent(source: LaunchSource) = event("new_intent", "source" to source.code)

    fun pageLoaded(ms: Long) = event("page_loaded", "ms" to int(ms))

    fun visualState(ms: Long) = event("visual_state", "ms" to int(ms))

    fun firstDraw(ms: Long) = event("first_draw", "ms" to int(ms))

    fun firstTouch(ms: Long) = event("first_touch", "ms" to int(ms))

    fun rendererGone(crashed: Boolean?, priority: Int?, then: RendererThen) = event(
        "renderer_gone",
        "crashed" to crashed?.toString(),
        "priority" to priority?.let { int(it.toLong()) },
        "then" to then.code,
    )

    fun routeIgnored(reason: IgnoredRoute) = event("route_ignored", "reason" to reason.code)

    fun destroy(finishing: Boolean, configChange: Boolean) =
        event("destroy", "finishing" to finishing.toString(), "config_change" to configChange.toString())

    private fun int(value: Long): String? = value.takeIf { it in 0..MAX_INT }?.toString()

    private fun event(name: String, vararg keys: Pair<String, String?>): String =
        keys.filter { it.second != null }.joinToString("") { " ${it.first}=${it.second}" }.let { name + it }
}
