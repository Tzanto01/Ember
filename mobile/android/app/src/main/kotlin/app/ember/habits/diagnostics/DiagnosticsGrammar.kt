package app.ember.habits.diagnostics

import java.text.SimpleDateFormat
import java.util.Locale
import java.util.TimeZone

/**
 * The exact shapes a diagnostics upload may carry (Lookout tickets contract v1.1, `PUT diagnostics/{key}`). Every
 * string Ember sends is built from parsed structure and then kept only if it matches one of these; anything else is
 * dropped on the device. Every class is spelled out in ASCII, since Android's ICU `\w` and `\d` also match
 * non-ASCII letters and digits that Ember's API refuses.
 */
internal object DiagnosticsGrammar {

    /** How long launch events and crash records are kept on the device. */
    const val KEEP_MS = 7L * 24 * 60 * 60 * 1000

    /**
     * How far back a report reaches: [KEEP_MS] less an hour, so a report sheet left open for up to about an hour still
     * fits the server's window of 7 days before its own time.
     */
    const val REPORT_MS = KEEP_MS - 60L * 60 * 1000

    val EVENT = Regex("""^[a-z_]{1,40}( [a-z_]{1,20}=[A-Za-z0-9_.:-]{1,40}){0,8}$""")
    val EXCEPTION = Regex("""^[A-Za-z0-9_$.]{1,200}$""")
    val JAVA_FRAME = Regex("""^[A-Za-z0-9_$.<>/-]{1,300}\([A-Za-z0-9_$.: -]{0,200}\)$""")
    val NATIVE_FRAME = Regex("""^#[0-9]+ pc [0-9a-f]+ [A-Za-z0-9_./-]+( \([A-Za-z0-9_:~<>,. -]+\))?$""")
    val VERSION_VALUE = Regex("""^[0-9A-Za-z._-]{1,40}$""")

    /** `ApplicationExitInfo.REASON_*` names without the prefix, indexed by the reason's value. */
    val REASONS = listOf(
        "UNKNOWN", "EXIT_SELF", "SIGNALED", "LOW_MEMORY", "CRASH", "CRASH_NATIVE", "ANR", "INITIALIZATION_FAILURE",
        "PERMISSION_CHANGE", "EXCESSIVE_RESOURCE_USAGE", "USER_REQUESTED", "USER_STOPPED", "DEPENDENCY_DIED", "OTHER",
        "FREEZER", "PACKAGE_STATE_CHANGE", "PACKAGE_UPDATED",
    )

    /** Linux signal names for the numbers 1 to 31, in order. */
    val SIGNALS = listOf(
        "SIGHUP", "SIGINT", "SIGQUIT", "SIGILL", "SIGTRAP", "SIGABRT", "SIGBUS", "SIGFPE", "SIGKILL", "SIGUSR1",
        "SIGSEGV", "SIGUSR2", "SIGPIPE", "SIGALRM", "SIGTERM", "SIGSTKFLT", "SIGCHLD", "SIGCONT", "SIGSTOP", "SIGTSTP",
        "SIGTTIN", "SIGTTOU", "SIGURG", "SIGXCPU", "SIGXFSZ", "SIGVTALRM", "SIGPROF", "SIGWINCH", "SIGIO", "SIGPWR",
        "SIGSYS",
    )

    /** The reason name for an `ApplicationExitInfo` reason value; a value this list doesn't know is `UNKNOWN`. */
    fun reason(value: Int): String = REASONS.getOrNull(value) ?: REASONS[0]

    /** The signal name for a status of 1 to 31, else null. */
    fun signal(status: Int): String? = SIGNALS.getOrNull(status - 1)

    /** True for a Java or a native frame. */
    fun isFrame(frame: String) = JAVA_FRAME.matches(frame) || NATIVE_FRAME.matches(frame)

    /** [epochMs] in UTC cut to the minute, as `yyyy-MM-ddTHH:mm:00Z`. */
    fun minute(epochMs: Long): String {
        val format = SimpleDateFormat("yyyy-MM-dd'T'HH:mm':00Z'", Locale.US)
        format.timeZone = TimeZone.getTimeZone("UTC")
        return format.format(epochMs)
    }
}
