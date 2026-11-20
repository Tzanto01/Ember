package app.ember.habits

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.io.IOException
import java.net.URI
import java.net.URISyntaxException
import java.security.GeneralSecurityException
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * The per-device widget check-in key, sealed with an AndroidKeyStore AES-GCM key, and the API origin it is sent to.
 * Both live in [WidgetStore]'s app-private prefs, so [WidgetStore.clear] erases them. Unrelated to the app's session
 * tokens.
 */
internal object WidgetKey {

    private const val KEYSTORE = "AndroidKeyStore"
    private const val ALIAS = "ember_widget_key"
    private const val TRANSFORMATION = "AES/GCM/NoPadding"
    private const val SEALED = "widgetKey"
    private const val IV = "widgetKeyIv"
    private const val API_BASE = "apiBase"
    private val VALUE = Regex("^[A-Za-z0-9_-]{32,128}={0,2}$")

    /** Seals and stores [key] with its [apiBase]; throws IllegalArgumentException on invalid input. */
    @Throws(GeneralSecurityException::class, IOException::class)
    fun set(context: Context, key: String?, apiBase: String?) {
        val origin = origin(apiBase)
        if (key == null || !VALUE.matches(key) || origin == null) throw IllegalArgumentException("key")
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, secret())
        val sealed = cipher.doFinal(key.toByteArray(Charsets.UTF_8))
        synchronized(WidgetStore.LOCK) {
            WidgetStore.prefs(context)
                .edit()
                .putString(SEALED, Base64.encodeToString(sealed, Base64.NO_WRAP))
                .putString(IV, Base64.encodeToString(cipher.iv, Base64.NO_WRAP))
                .putString(API_BASE, origin)
                .commit()
        }
    }

    /** The stored key, or null when there is none or it does not decrypt. */
    fun get(context: Context): String? {
        val prefs = WidgetStore.prefs(context)
        val sealed = prefs.getString(SEALED, null)
        val iv = prefs.getString(IV, null)
        if (sealed == null || iv == null) return null
        return try {
            val store = KeyStore.getInstance(KEYSTORE)
            store.load(null)
            val secret = store.getKey(ALIAS, null) as? SecretKey ?: return null
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.DECRYPT_MODE, secret, GCMParameterSpec(128, Base64.decode(iv, Base64.NO_WRAP)))
            val key = String(cipher.doFinal(Base64.decode(sealed, Base64.NO_WRAP)), Charsets.UTF_8)
            if (VALUE.matches(key)) key else null
        } catch (e: GeneralSecurityException) {
            null
        } catch (e: IOException) {
            null
        } catch (e: IllegalArgumentException) {
            null
        }
    }

    /** The stored API origin, or null. */
    fun apiBase(context: Context): String? = origin(WidgetStore.prefs(context).getString(API_BASE, null))

    /** Erases the key if it is still [rejected], so a key the app set meanwhile survives. */
    fun erase(context: Context, rejected: String) {
        synchronized(WidgetStore.LOCK) {
            if (rejected != get(context)) return
            WidgetStore.prefs(context).edit().remove(SEALED).remove(IV).commit()
        }
    }

    /** [value] as `https://host[:port]` when it is an absolute https origin without path, else null. */
    fun origin(value: String?): String? {
        if (value == null) return null
        return try {
            val uri = URI(value)
            val path = uri.rawPath
            if (uri.scheme != "https" || uri.host == null || uri.rawUserInfo != null) return null
            if (uri.rawQuery != null || uri.rawFragment != null) return null
            if (!path.isNullOrEmpty() && path != "/") return null
            "https://" + uri.rawAuthority
        } catch (e: URISyntaxException) {
            null
        }
    }

    @Throws(GeneralSecurityException::class, IOException::class)
    private fun secret(): SecretKey {
        val store = KeyStore.getInstance(KEYSTORE)
        store.load(null)
        val existing = store.getKey(ALIAS, null)
        if (existing is SecretKey) return existing
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, KEYSTORE)
        generator.init(
            KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build(),
        )
        return generator.generateKey()
    }
}
