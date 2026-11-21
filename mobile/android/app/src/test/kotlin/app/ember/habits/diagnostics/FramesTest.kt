package app.ember.habits.diagnostics

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayOutputStream

/**
 * The allowlist parser keeps only frames it built from parsed structure that fit the contract's grammar: the main
 * thread of an ANR, the crashing thread of a tombstone, a throwable's own stack. Messages, headers, other threads,
 * locks, build ids, offsets and library paths never survive.
 */
class FramesTest {

    private fun assertAllFrames(frames: List<String>) =
        frames.forEach { assertTrue(it, DiagnosticsGrammar.isFrame(it)) }

    @Test
    fun anAnrKeepsOnlyTheMainThreadsFrames() {
        val trace = """
            ----- pid 4242 at 2026-11-20 18:40:01.123456789+0100 -----
            Cmd line: app.ember.habits
            Build fingerprint: 'google/panther/panther:14/AP1A/1234567:user/release-keys'
            ABI: 'arm64'

            DALVIK THREADS (3):
            "Signal Catcher" daemon prio=10 tid=2 Runnable
              native: #00 pc 0000000000555555  /apex/com.android.art/lib64/libart.so (art::DumpNativeStack+100)
              at app.ember.habits.Other.thread(Other.kt:9)

            "main" prio=5 tid=1 Blocked
              | group="main" sCount=1 ucsCount=0 flags=1 obj=0x72a3c5f8 self=0xb400007a1c2a7be0
              | sysTid=4242 nice=-10 cgrp=top-app sched=0/0 handle=0x7b5e1c84f8
              native: #00 pc 000000000004e1d4  /apex/com.android.runtime/lib64/bionic/libc.so (__epoll_pwait+4) (BuildId: 0a1b2c3d4e5f)
              native: #01 pc 0000000000012abc  /system/lib64/libutils.so (android::Looper::pollInner(int)+184) (BuildId: ffee)
              at java.lang.Object.wait(Native method)
              - waiting on <0x0a1b2c3d> (a java.lang.Object)
              at app.ember.habits.Sync.await(Sync.kt:42)
              - locked <0x0e0f1a2b> (a app.ember.habits.Sync)
              at app.ember.habits.Note.show(Note.kt:7) secret note text
              Caused by: java.lang.IllegalStateException: user@example.com
              at android.os.Handler.dispatchMessage(Handler.java:102)

            "Worker" prio=5 tid=3 Waiting
              at app.ember.habits.Worker.run(Worker.kt:1)
        """.trimIndent()

        val frames = Frames.anr(trace.reader().buffered())

        assertEquals(
            listOf(
                "#00 pc 000000000004e1d4 libc.so (__epoll_pwait)",
                "#01 pc 0000000000012abc libutils.so",
                "java.lang.Object.wait(Native method)",
                "app.ember.habits.Sync.await(Sync.kt:42)",
                "android.os.Handler.dispatchMessage(Handler.java:102)",
            ),
            frames,
        )
        assertAllFrames(frames)
    }

    @Test
    fun aTextTombstoneKeepsTheCrashingThreadsBacktraceAsFrameNames() {
        val tombstone = """
            *** *** *** *** *** *** *** *** *** *** *** *** *** *** *** ***
            Build fingerprint: 'google/panther/panther:11/RQ3A/1234567:user/release-keys'
            pid: 4242, tid: 4242, name: app.ember.habits  >>> app.ember.habits <<<
            signal 11 (SIGSEGV), code 1 (SEGV_MAPERR), fault addr 0x0
            Abort message: 'secret abort text'
                x0  0000000000000000  x1  0000007fe0a1b2c3
            backtrace:
                  #00 pc 000000000004e1d4  /apex/com.android.runtime/lib64/bionic/libc.so (abort+164) (BuildId: 0a1b2c3d)
                  #01 pc 0000000000001234  /data/app/~~Xy7Rand0m==/app.ember.habits-Q1w2e3==/base.apk!libember.so (offset 0x4000) (ember::crash()+8)
                  #02 pc 0000000000005678  /data/app/~~Xy7Rand0m==/app.ember.habits-Q1w2e3==/lib/arm64/libember.so (Java_app_ember_habits_Native_run+20)
                  #03 pc 0000000000009abc  <anonymous:7a1b2c3d>

            stack:
                 0000007fe0a1b2c0  0000000000000000
            --- --- --- --- --- --- --- --- --- --- --- --- --- --- --- ---
            pid: 4242, tid: 4243, name: Worker  >>> app.ember.habits <<<
            backtrace:
                  #00 pc 0000000000000042  /system/lib64/libother.so (other+4)
        """.trimIndent()

        val frames = Frames.nativeText(tombstone.reader().buffered())

        assertEquals(
            listOf(
                "#00 pc 000000000004e1d4 libc.so (abort)",
                "#01 pc 0000000000001234 libember.so",
                "#02 pc 0000000000005678 libember.so (Java_app_ember_habits_Native_run)",
            ),
            frames,
        )
        assertAllFrames(frames)
    }

    @Test
    fun aProtobufTombstoneKeepsOnlyTheCrashingThreadsFrameNames() {
        val crashing = thread(
            id = 4242,
            frame(relPc = 0x4e1d4, function = "abort", file = "/apex/com.android.runtime/lib64/bionic/libc.so"),
            frame(relPc = 0x1234, function = "ember::crash(int)", file = "/data/app/~~R4nd==/base.apk!libember.so"),
            frame(relPc = 0x5678, function = null, file = "/data/app/~~R4nd==/lib/arm64/libember.so"),
        )
        val other = thread(id = 4243, frame(relPc = 0x42, function = "other", file = "/system/lib64/libother.so"))
        val tombstone = message(
            string(2, "google/panther/panther:14/AP1A/1234567:user/release-keys"),
            varint(5, 4242),
            varint(6, 4242),
            string(14, "secret abort text"),
            entry(4243, other),
            entry(4242, crashing),
            fixed64(99),
        )

        val frames = Frames.tombstone(tombstone)

        assertEquals(
            listOf(
                "#00 pc 000000000004e1d4 libc.so (abort)",
                "#01 pc 0000000000001234 libember.so",
                "#02 pc 0000000000005678 libember.so",
            ),
            frames,
        )
        assertAllFrames(frames)
        assertEquals(emptyList<String>(), Frames.tombstone(tombstone.copyOf(tombstone.size - 5)))
    }

    @Test
    fun aThrowableGivesItsTypeChainAndOwnFramesButNeverAMessage() {
        val cause = IllegalStateException("token=abc123 user@example.com")
        val crash = RuntimeException("habit Walk failed for user-1", cause)
        crash.stackTrace = arrayOf(
            StackTraceElement("app.ember.habits.Sync", "run", "Sync.kt", 12),
            StackTraceElement("app.ember.habits.Sync\$\$ExternalSyntheticLambda0", "<init>", null, -1),
            StackTraceElement("dalvik.system.VMStack", "getThreadStackTrace", null, -2),
            StackTraceElement("app.ember.habits.Odd", "method with spaces", "Odd.kt", 3),
        )

        val record = CrashRecorder.record(crash, 1_000L).toString()

        assertEquals(listOf("java.lang.RuntimeException", "java.lang.IllegalStateException"), Frames.exceptionChain(crash))
        assertEquals(
            listOf(
                "app.ember.habits.Sync.run(Sync.kt:12)",
                "app.ember.habits.Sync\$\$ExternalSyntheticLambda0.<init>(Unknown Source)",
                "dalvik.system.VMStack.getThreadStackTrace(Native Method)",
            ),
            Frames.java(crash),
        )
        for (secret in listOf("token", "abc123", "example.com", "Walk", "user-1", "failed")) {
            assertTrue("$secret leaked into $record", secret !in record)
        }
    }

    private fun frame(relPc: Long, function: String?, file: String) = message(
        varint(1, relPc),
        varint(2, relPc + 0x7a00000000),
        listOfNotNull(function?.let { string(4, it) }).fold(ByteArray(0)) { a, b -> a + b },
        string(6, file),
        string(8, "0a1b2c3d4e5f"),
    )

    private fun thread(id: Long, vararg frames: ByteArray) = message(
        varint(1, id),
        string(2, "secret thread name"),
        *frames.map { bytes(4, it) }.toTypedArray(),
    )

    private fun entry(key: Long, thread: ByteArray) = bytes(16, message(varint(1, key), bytes(2, thread)))

    private fun message(vararg parts: ByteArray) = parts.fold(ByteArray(0)) { a, b -> a + b }

    private fun varint(field: Int, value: Long) = encode((field shl 3).toLong()) + encode(value)

    private fun fixed64(field: Int) = encode(((field shl 3) or 1).toLong()) + ByteArray(8) { 0x7f }

    private fun string(field: Int, value: String) = bytes(field, value.toByteArray(Charsets.UTF_8))

    private fun bytes(field: Int, value: ByteArray) =
        encode(((field shl 3) or 2).toLong()) + encode(value.size.toLong()) + value

    private fun encode(value: Long): ByteArray {
        val out = ByteArrayOutputStream()
        var rest = value
        while (rest and 0x7fL.inv() != 0L) {
            out.write(((rest and 0x7f) or 0x80).toInt())
            rest = rest ushr 7
        }
        out.write(rest.toInt())
        return out.toByteArray()
    }
}
