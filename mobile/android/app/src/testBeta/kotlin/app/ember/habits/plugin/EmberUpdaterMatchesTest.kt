package app.ember.habits.plugin

import java.security.MessageDigest
import java.util.Locale
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** A downloaded beta APK is installed only when its SHA-256 and size match the manifest. */
class EmberUpdaterMatchesTest {

    private val apk = "synthetic apk bytes".toByteArray(Charsets.US_ASCII)

    private fun digest(bytes: ByteArray) = MessageDigest.getInstance("SHA-256").digest(bytes)

    private fun hex(bytes: ByteArray) = bytes.joinToString("") { "%02x".format(Locale.ROOT, it) }

    @Test
    fun acceptsTheExpectedDigestInEitherCaseWithOrWithoutASize() {
        val actual = digest(apk)
        assertTrue(EmberUpdaterPlugin.matches(hex(actual), apk.size.toLong(), actual, apk.size.toLong()))
        assertTrue(EmberUpdaterPlugin.matches(hex(actual).uppercase(Locale.ROOT), null, actual, apk.size.toLong()))
    }

    @Test
    fun rejectsAnotherFileOrAnotherSize() {
        val actual = digest(apk)
        val other = digest("tampered apk bytes".toByteArray(Charsets.US_ASCII))
        assertFalse(EmberUpdaterPlugin.matches(hex(other), null, actual, apk.size.toLong()))
        assertFalse(EmberUpdaterPlugin.matches(hex(actual), apk.size.toLong() + 1, actual, apk.size.toLong()))
        assertFalse(EmberUpdaterPlugin.matches(hex(actual).substring(2), null, actual, apk.size.toLong()))
    }
}
