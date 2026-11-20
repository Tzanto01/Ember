package app.ember.habits;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.Locale;
import org.junit.Test;

/** A downloaded beta APK is installed only when its SHA-256 and size match the manifest. */
public class EmberUpdaterMatchesTest {

    private static final byte[] APK = "synthetic apk bytes".getBytes(StandardCharsets.US_ASCII);

    private static byte[] digest(byte[] bytes) throws Exception {
        return MessageDigest.getInstance("SHA-256").digest(bytes);
    }

    private static String hex(byte[] bytes) {
        StringBuilder out = new StringBuilder();
        for (byte b : bytes) out.append(String.format(Locale.ROOT, "%02x", b));
        return out.toString();
    }

    @Test
    public void acceptsTheExpectedDigestInEitherCaseWithOrWithoutASize() throws Exception {
        byte[] actual = digest(APK);
        assertTrue(EmberUpdaterPlugin.matches(hex(actual), (long) APK.length, actual, APK.length));
        assertTrue(EmberUpdaterPlugin.matches(hex(actual).toUpperCase(Locale.ROOT), null, actual, APK.length));
    }

    @Test
    public void rejectsAnotherFileOrAnotherSize() throws Exception {
        byte[] actual = digest(APK);
        byte[] other = digest("tampered apk bytes".getBytes(StandardCharsets.US_ASCII));
        assertFalse(EmberUpdaterPlugin.matches(hex(other), null, actual, APK.length));
        assertFalse(EmberUpdaterPlugin.matches(hex(actual), (long) APK.length + 1, actual, APK.length));
        assertFalse(EmberUpdaterPlugin.matches(hex(actual).substring(2), null, actual, APK.length));
    }
}
