package app.ember.habits;

import android.content.Context;
import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;
import java.io.IOException;
import java.net.URI;
import java.net.URISyntaxException;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.Key;
import java.security.KeyStore;
import java.util.regex.Pattern;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

/**
 * The per-device widget check-in key, sealed with an AndroidKeyStore AES-GCM key, and the API origin it is sent to.
 * Both live in the widget's app-private prefs, so {@link TodayWidget#clear} erases them. Unrelated to the app's
 * session tokens.
 */
final class WidgetKey {

    private static final String KEYSTORE = "AndroidKeyStore";
    private static final String ALIAS = "ember_widget_key";
    private static final String TRANSFORMATION = "AES/GCM/NoPadding";
    private static final String SEALED = "widgetKey";
    private static final String IV = "widgetKeyIv";
    private static final String API_BASE = "apiBase";
    private static final Pattern VALUE = Pattern.compile("^[A-Za-z0-9_-]{32,128}={0,2}$");

    private WidgetKey() {}

    /** Seals and stores {@code key} with its {@code apiBase}; throws IllegalArgumentException on invalid input. */
    static void set(Context context, String key, String apiBase) throws GeneralSecurityException, IOException {
        String origin = origin(apiBase);
        if (key == null || !VALUE.matcher(key).matches() || origin == null) throw new IllegalArgumentException("key");
        Cipher cipher = Cipher.getInstance(TRANSFORMATION);
        cipher.init(Cipher.ENCRYPT_MODE, secret());
        byte[] sealed = cipher.doFinal(key.getBytes(StandardCharsets.UTF_8));
        synchronized (TodayWidget.class) {
            TodayWidget.prefs(context)
                .edit()
                .putString(SEALED, Base64.encodeToString(sealed, Base64.NO_WRAP))
                .putString(IV, Base64.encodeToString(cipher.getIV(), Base64.NO_WRAP))
                .putString(API_BASE, origin)
                .commit();
        }
    }

    /** The stored key, or null when there is none or it does not decrypt. */
    static String get(Context context) {
        SharedPreferences prefs = TodayWidget.prefs(context);
        String sealed = prefs.getString(SEALED, null);
        String iv = prefs.getString(IV, null);
        if (sealed == null || iv == null) return null;
        try {
            KeyStore store = KeyStore.getInstance(KEYSTORE);
            store.load(null);
            Key secret = store.getKey(ALIAS, null);
            if (!(secret instanceof SecretKey)) return null;
            Cipher cipher = Cipher.getInstance(TRANSFORMATION);
            cipher.init(Cipher.DECRYPT_MODE, secret, new GCMParameterSpec(128, Base64.decode(iv, Base64.NO_WRAP)));
            String key = new String(cipher.doFinal(Base64.decode(sealed, Base64.NO_WRAP)), StandardCharsets.UTF_8);
            return VALUE.matcher(key).matches() ? key : null;
        } catch (GeneralSecurityException | IOException | IllegalArgumentException e) {
            return null;
        }
    }

    /** The stored API origin, or null. */
    static String apiBase(Context context) {
        return origin(TodayWidget.prefs(context).getString(API_BASE, null));
    }

    /** Erases the key if it is still {@code rejected}, so a key the app set meanwhile survives. */
    static void erase(Context context, String rejected) {
        synchronized (TodayWidget.class) {
            if (!rejected.equals(get(context))) return;
            TodayWidget.prefs(context).edit().remove(SEALED).remove(IV).commit();
        }
    }

    /** {@code value} as {@code https://host[:port]} when it is an absolute https origin without path, else null. */
    static String origin(String value) {
        if (value == null) return null;
        try {
            URI uri = new URI(value);
            String path = uri.getRawPath();
            if (!"https".equals(uri.getScheme()) || uri.getHost() == null || uri.getRawUserInfo() != null) return null;
            if (uri.getRawQuery() != null || uri.getRawFragment() != null) return null;
            if (path != null && !path.isEmpty() && !path.equals("/")) return null;
            return "https://" + uri.getRawAuthority();
        } catch (URISyntaxException e) {
            return null;
        }
    }

    private static SecretKey secret() throws GeneralSecurityException, IOException {
        KeyStore store = KeyStore.getInstance(KEYSTORE);
        store.load(null);
        Key existing = store.getKey(ALIAS, null);
        if (existing instanceof SecretKey secretKey) return secretKey;
        KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, KEYSTORE);
        generator.init(
            new KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build()
        );
        return generator.generateKey();
    }
}
