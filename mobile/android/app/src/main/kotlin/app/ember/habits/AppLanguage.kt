package app.ember.habits

import android.annotation.SuppressLint
import android.content.Context
import android.content.res.Configuration
import java.util.Locale

/**
 * The app's language for text built natively (widgets), as last set by the web app through
 * `EmberWidget.setLanguage`. It lives in its own SharedPreferences, so signing out keeps it.
 */
internal object AppLanguage {

    private const val PREFS = "ember_language"
    private const val LANGUAGE = "language"

    /** Stores `"de"` for `"de"`, else `"en"` (unknown or missing values included). */
    @JvmStatic
    fun set(context: Context, language: String?) {
        context
            .getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit()
            .putString(LANGUAGE, if (language == "de") "de" else "en")
            .apply()
    }

    /** The stored language, or from the device's first locale (`de*` is German) when none is stored yet. */
    @JvmStatic
    fun get(context: Context): String {
        val stored = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(LANGUAGE, null)
        if (stored != null) return stored
        val device: Locale? = context.resources.configuration.locales.get(0)
        return if (device?.language == "de") "de" else "en"
    }

    /** A context whose resources resolve strings in [get]'s language (language splits are off in the bundle). */
    @SuppressLint("AppBundleLocaleChanges")
    @JvmStatic
    fun localized(context: Context): Context {
        val config = Configuration(context.resources.configuration)
        config.setLocale(Locale.forLanguageTag(get(context)))
        return context.createConfigurationContext(config)
    }
}
