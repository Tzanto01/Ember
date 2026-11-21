package app.ember.habits.diagnostics

import java.io.BufferedReader
import java.util.Collections
import java.util.IdentityHashMap

/**
 * The allowlist parser for crash and ANR detail: each frame is built from parsed structure and kept only if it matches
 * the contract's exact grammar ([DiagnosticsGrammar]). It never returns a message, a thread header, another thread,
 * a lock, a register, memory, an abort message, a fingerprint, a build id or a library path.
 */
internal object Frames {

    const val MAX_FRAMES = 40
    const val MAX_EXCEPTIONS = 10

    private val JAVA_LINE = Regex("""^\s*at ([^\s(]+)\((.*)\)$""")
    private val NATIVE_LINE = Regex("""^\s*(?:native: )?#(\d+) pc ([0-9a-f]+)\s+(\S+)(.*)$""")
    private val BUILD_ID = Regex("""\s*\(BuildId: [^)]*\)\s*$""")
    private val MAP_OFFSET = Regex("""^\s*\(offset 0x[0-9a-f]+\)""")
    private val SYMBOL = Regex("""^\s*\((.*)\)\s*$""")
    private val SYMBOL_OFFSET = Regex("""\+\d+$""")

    /** The type names of [throwable]'s cause chain, outermost first. */
    fun exceptionChain(throwable: Throwable): List<String> {
        val seen = Collections.newSetFromMap(IdentityHashMap<Throwable, Boolean>())
        val names = ArrayList<String>()
        var current: Throwable? = throwable
        while (current != null && seen.add(current) && names.size < MAX_EXCEPTIONS) {
            current.javaClass.name.takeIf(DiagnosticsGrammar.EXCEPTION::matches)?.let(names::add)
            current = current.cause
        }
        return names
    }

    /** [throwable]'s own stack (not its causes') as `class.method(File.java:line)` frames. */
    fun java(throwable: Throwable): List<String> =
        throwable.stackTrace.asSequence().mapNotNull(::java).take(MAX_FRAMES).toList()

    /** One stack element as a Java frame, or null when it doesn't fit the grammar. */
    fun java(element: StackTraceElement): String? {
        val file = element.fileName
        val location = when {
            element.isNativeMethod -> "Native Method"
            file != null && element.lineNumber >= 0 -> "$file:${element.lineNumber}"
            else -> file ?: "Unknown Source"
        }
        return "${element.className}.${element.methodName}($location)".takeIf(DiagnosticsGrammar.JAVA_FRAME::matches)
    }

    /**
     * The frames of the `"main"` thread in an ANR trace: its `at ...` lines as Java frames and its `native: #NN pc ...`
     * lines as native frames. Everything outside that thread's section, and every other line in it, is dropped.
     */
    fun anr(trace: BufferedReader): List<String> {
        val frames = ArrayList<String>()
        var inMain = false
        while (frames.size < MAX_FRAMES) {
            val line = trace.readLine() ?: break
            if (!inMain) {
                inMain = line.startsWith("\"main\" ")
                continue
            }
            if (line.isBlank() || line.startsWith("\"")) break
            (javaLine(line) ?: nativeLine(line))?.let(frames::add)
        }
        return frames
    }

    /** The frames of the first `backtrace:` block of a text tombstone, which is the crashing thread's. */
    fun nativeText(tombstone: BufferedReader): List<String> {
        val frames = ArrayList<String>()
        var inBacktrace = false
        while (frames.size < MAX_FRAMES) {
            val line = tombstone.readLine() ?: break
            if (!inBacktrace) {
                inBacktrace = line.trim() == "backtrace:"
                continue
            }
            if (!line.trimStart().startsWith("#")) break
            nativeLine(line)?.let(frames::add)
        }
        return frames
    }

    /** The crashing thread's frames from a protobuf tombstone (API 31+), see [Tombstone]. */
    fun tombstone(bytes: ByteArray): List<String> =
        Tombstone.crashingBacktrace(bytes).mapIndexedNotNull { index, frame ->
            val pc = java.lang.Long.toHexString(frame.relPc).padStart(16, '0')
            native(index.toString().padStart(2, '0'), pc, frame.fileName, frame.functionName)
        }.take(MAX_FRAMES)

    private fun javaLine(line: String): String? {
        val match = JAVA_LINE.matchEntire(line) ?: return null
        val (method, location) = match.destructured
        return "$method($location)".takeIf(DiagnosticsGrammar.JAVA_FRAME::matches)
    }

    private fun nativeLine(line: String): String? {
        val match = NATIVE_LINE.matchEntire(line) ?: return null
        val (number, pc, path, rest) = match.destructured
        val tail = rest.replace(BUILD_ID, "").replaceFirst(MAP_OFFSET, "")
        val symbol = SYMBOL.matchEntire(tail)?.groupValues?.get(1)
        return native(number, pc, path, symbol)
    }

    /**
     * A native frame naming the library by its file name only. A symbol outside the grammar is left out, keeping the
     * frame; a frame that still doesn't fit is dropped.
     */
    private fun native(number: String, pc: String, path: String, symbol: String?): String? {
        val library = path.substringAfterLast('/').substringAfterLast('!')
        val bare = "#$number pc $pc $library"
        val name = symbol?.replace(SYMBOL_OFFSET, "")?.takeIf { it.isNotEmpty() }
        return listOfNotNull(name?.let { "$bare ($it)" }, bare).firstOrNull(DiagnosticsGrammar.NATIVE_FRAME::matches)
    }
}

/**
 * A minimal reader of Android's tombstone protobuf (`tombstone.proto`): it reads only the crashing thread id (`tid`,
 * field 6), the `threads` map (field 16), and in the crashing thread's `current_backtrace` (field 4) each frame's
 * `rel_pc` (1), `function_name` (4) and `file_name` (6). Every other field is skipped unread. Malformed input gives
 * no frames.
 */
internal object Tombstone {

    /** One backtrace frame as the tombstone stores it. */
    data class Frame(val relPc: Long, val functionName: String?, val fileName: String)

    private const val VARINT = 0
    private const val FIXED64 = 1
    private const val LENGTH = 2
    private const val FIXED32 = 5

    fun crashingBacktrace(bytes: ByteArray): List<Frame> =
        try {
            val top = Reader(bytes, 0, bytes.size)
            var tid = 0L
            val threads = HashMap<Long, IntRange>()
            while (top.more()) {
                val (field, type) = top.tag()
                when {
                    field == 6 && type == VARINT -> tid = top.varint()
                    field == 16 && type == LENGTH -> {
                        val entry = top.message()
                        var key = 0L
                        var value: IntRange? = null
                        while (entry.more()) {
                            val (entryField, entryType) = entry.tag()
                            when {
                                entryField == 1 && entryType == VARINT -> key = entry.varint()
                                entryField == 2 && entryType == LENGTH -> value = entry.range()
                                else -> entry.skip(entryType)
                            }
                        }
                        if (value != null) threads[key] = value
                    }
                    else -> top.skip(type)
                }
            }
            threads[tid]?.let { backtrace(Reader(bytes, it.first, it.last + 1)) }.orEmpty()
        } catch (e: MalformedTombstone) {
            emptyList()
        }

    private fun backtrace(thread: Reader): List<Frame> {
        val frames = ArrayList<Frame>()
        while (thread.more()) {
            val (field, type) = thread.tag()
            if (field == 4 && type == LENGTH) frame(thread.message())?.let(frames::add) else thread.skip(type)
        }
        return frames
    }

    private fun frame(reader: Reader): Frame? {
        var relPc = 0L
        var functionName: String? = null
        var fileName: String? = null
        while (reader.more()) {
            val (field, type) = reader.tag()
            when {
                field == 1 && type == VARINT -> relPc = reader.varint()
                field == 4 && type == LENGTH -> functionName = reader.string()
                field == 6 && type == LENGTH -> fileName = reader.string()
                else -> reader.skip(type)
            }
        }
        return fileName?.let { Frame(relPc, functionName, it) }
    }

    private class MalformedTombstone : Exception()

    private class Reader(private val bytes: ByteArray, private var position: Int, private val end: Int) {

        fun more() = position < end

        fun tag(): Pair<Int, Int> {
            val tag = varint()
            return (tag ushr 3).toInt() to (tag and 7).toInt()
        }

        fun varint(): Long {
            var result = 0L
            var shift = 0
            while (shift < 64) {
                if (position >= end) throw MalformedTombstone()
                val byte = bytes[position++].toInt()
                result = result or ((byte and 0x7f).toLong() shl shift)
                if (byte and 0x80 == 0) return result
                shift += 7
            }
            throw MalformedTombstone()
        }

        fun range(): IntRange {
            val length = varint()
            if (length < 0 || length > end - position) throw MalformedTombstone()
            val start = position
            position += length.toInt()
            return start until position
        }

        fun message(): Reader = range().let { Reader(bytes, it.first, it.last + 1) }

        fun string(): String = range().let { String(bytes, it.first, it.last + 1 - it.first, Charsets.UTF_8) }

        fun skip(type: Int) {
            when (type) {
                VARINT -> varint()
                FIXED64 -> advance(8)
                LENGTH -> range()
                FIXED32 -> advance(4)
                else -> throw MalformedTombstone()
            }
        }

        private fun advance(count: Int) {
            if (count > end - position) throw MalformedTombstone()
            position += count
        }
    }
}
