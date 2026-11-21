package app.ember.habits.diagnostics

/** One recorded launch event at [at] epoch milliseconds. */
internal data class Launch(val at: Long, val event: String)

/** One process exit at [at] epoch milliseconds; see [DiagnosticsPayload] for the rules it must meet. */
internal data class Exit(
    val at: Long,
    val reason: String,
    val signal: String?,
    val exception: List<String>,
    val frames: List<String>,
)

/** The raw device facts the payload is built from, as Android reports them. */
internal data class DeviceFacts(
    val versionName: String?,
    val release: String?,
    val sdk: Int,
    val manufacturer: String?,
    val model: String?,
)

/** The `EmberDiagnostics.collect()` result (spec section 1), with times already cut to the minute. */
internal data class DeviceDiagnostics(
    val installation: String,
    val version: String?,
    val os: String,
    val device: String?,
    val launches: List<Launch>,
    val exits: List<Exit>,
) {
    /** Compact JSON, as `JSON.stringify` would write it. */
    fun toJson(): String = buildString {
        append("{\"installation\":").append(quote(installation))
        append(",\"version\":").append(quote(version))
        append(",\"os\":").append(quote(os))
        append(",\"device\":").append(quote(device))
        append(',').append(timeline(launches, exits))
        append('}')
    }

    companion object {

        /** `"launches":[...],"exits":[...]`, the part the byte budget covers. */
        fun timeline(launches: List<Launch>, exits: List<Exit>): String = buildString {
            append("\"launches\":")
            append(launches.joinToString(",", "[", "]") { "{\"at\":${quote(minute(it.at))},\"event\":${quote(it.event)}}" })
            append(",\"exits\":")
            append(
                exits.joinToString(",", "[", "]") {
                    "{\"at\":${quote(minute(it.at))},\"reason\":${quote(it.reason)},\"signal\":${quote(it.signal)}," +
                        "\"exception\":${it.exception.joinToString(",", "[", "]", transform = ::quote)}," +
                        "\"frames\":${it.frames.joinToString(",", "[", "]", transform = ::quote)}}"
                },
            )
        }

        private fun minute(at: Long) = DiagnosticsGrammar.minute(at)

        private fun quote(text: String?): String {
            if (text == null) return "null"
            val out = StringBuilder(text.length + 2).append('"')
            for (char in text) {
                when {
                    char == '"' -> out.append("\\\"")
                    char == '\\' -> out.append("\\\\")
                    char < ' ' -> out.append("\\u").append(char.code.toString(16).padStart(4, '0'))
                    else -> out.append(char)
                }
            }
            return out.append('"').toString()
        }
    }
}

/**
 * Builds [DeviceDiagnostics] from recorded launches and exits: only the [DiagnosticsGrammar.REPORT_MS] up to `now`
 * (7 days less an hour), newest first, at most
 * [MAX_LAUNCHES] launches and [MAX_EXITS] exits, at most [Frames.MAX_FRAMES] frames and [MAX_FRAME_BYTES] bytes of
 * frames per exit, every string re-checked against [DiagnosticsGrammar], and the
 * `launches` + `exits` JSON kept under [BUDGET_BYTES] UTF-8 bytes by dropping frames from the end of the oldest exit
 * first, then the oldest exits, then the oldest launches.
 */
internal object DiagnosticsPayload {

    const val MAX_LAUNCHES = 50
    const val MAX_EXITS = 5
    const val BUDGET_BYTES = 56_000
    const val MAX_FRAME_BYTES = 8_000
    private const val MAX_VERSION = 32
    private const val MAX_RELEASE = 20
    private const val MAX_DEVICE = 64
    private val VERSION = Regex("""^[A-Za-z0-9.+_-]+$""")
    private val NOT_RELEASE = Regex("""[^A-Za-z0-9._]""")
    private val NOT_DEVICE = Regex("""[^A-Za-z0-9 ._+()-]""")
    private val SPACES = Regex(""" +""")

    fun build(installation: String, facts: DeviceFacts, launches: List<Launch>, exits: List<Exit>, now: Long): DeviceDiagnostics {
        val keptLaunches = recent(launches, now, Launch::at)
            .filter { DiagnosticsGrammar.EVENT.matches(it.event) }
            .take(MAX_LAUNCHES)
        val keptExits = recent(exits, now, Exit::at).mapNotNull(::clean).take(MAX_EXITS)
        val (trimmedLaunches, trimmedExits) = trim(keptLaunches, keptExits)
        return DeviceDiagnostics(
            installation = installation,
            version = version(facts.versionName),
            os = os(facts.release, facts.sdk),
            device = device(facts.manufacturer, facts.model),
            launches = trimmedLaunches,
            exits = trimmedExits,
        )
    }

    /** `versionName` if it is at most 32 characters of `[A-Za-z0-9.+_-]`, else null. */
    fun version(versionName: String?): String? =
        versionName?.takeIf { it.length <= MAX_VERSION && VERSION.matches(it) }

    /** `Android <release> (API <sdk>)` with the release reduced to `[A-Za-z0-9._]` and cut to 20, else the SDK int. */
    fun os(release: String?, sdk: Int): String {
        val name = release.orEmpty().replace(NOT_RELEASE, "").take(MAX_RELEASE).ifEmpty { sdk.toString() }
        return "Android $name (API $sdk)"
    }

    /** `<manufacturer> <model>` without characters outside `[A-Za-z0-9 ._+()-]`, spaces collapsed, cut to 64. */
    fun device(manufacturer: String?, model: String?): String? =
        "${manufacturer.orEmpty()} ${model.orEmpty()}".replace(NOT_DEVICE, "").replace(SPACES, " ").trim()
            .take(MAX_DEVICE).trim().ifEmpty { null }

    private fun <T> recent(items: List<T>, now: Long, at: (T) -> Long): List<T> =
        items.filter { at(it) in (now - DiagnosticsGrammar.REPORT_MS)..now }.sortedByDescending(at)

    private fun clean(exit: Exit): Exit? {
        if (exit.reason !in DiagnosticsGrammar.REASONS) return null
        return exit.copy(
            signal = exit.signal?.takeIf { it in DiagnosticsGrammar.SIGNALS },
            exception = exit.exception.filter(DiagnosticsGrammar.EXCEPTION::matches).take(Frames.MAX_EXCEPTIONS),
            frames = framesWithinBudget(exit.frames.filter(DiagnosticsGrammar::isFrame).take(Frames.MAX_FRAMES)),
        )
    }

    /** The leading [frames] whose UTF-8 bytes add up to at most [MAX_FRAME_BYTES]. */
    private fun framesWithinBudget(frames: List<String>): List<String> {
        var total = 0
        return frames.takeWhile {
            total += it.toByteArray(Charsets.UTF_8).size
            total <= MAX_FRAME_BYTES
        }
    }

    private fun trim(launches: List<Launch>, exits: List<Exit>): Pair<List<Launch>, List<Exit>> {
        val keptLaunches = launches.toMutableList()
        val keptExits = exits.toMutableList()
        while (bytes(keptLaunches, keptExits) >= BUDGET_BYTES) {
            val oldestWithFrames = keptExits.indexOfLast { it.frames.isNotEmpty() }
            when {
                oldestWithFrames >= 0 -> keptExits[oldestWithFrames].let {
                    keptExits[oldestWithFrames] = it.copy(frames = it.frames.dropLast(1))
                }
                keptExits.isNotEmpty() -> keptExits.removeAt(keptExits.lastIndex)
                keptLaunches.isNotEmpty() -> keptLaunches.removeAt(keptLaunches.lastIndex)
                else -> break
            }
        }
        return keptLaunches to keptExits
    }

    private fun bytes(launches: List<Launch>, exits: List<Exit>) =
        DeviceDiagnostics.timeline(launches, exits).toByteArray(Charsets.UTF_8).size
}
