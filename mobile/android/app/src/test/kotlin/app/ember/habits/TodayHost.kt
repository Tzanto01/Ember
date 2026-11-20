package app.ember.habits

import android.appwidget.AppWidgetManager
import android.content.Context
import android.content.IntentFilter
import android.os.Build
import android.os.Bundle
import android.security.keystore.KeyGenParameterSpec
import android.util.SizeF
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.RemoteViews
import android.widget.TextView
import app.ember.habits.widget.WidgetKey
import app.ember.habits.widget.WidgetStore
import java.io.InputStream
import java.io.OutputStream
import java.security.Key
import java.security.KeyStoreSpi
import java.security.Provider
import java.security.SecureRandom
import java.security.Security
import java.security.cert.Certificate
import java.security.spec.AlgorithmParameterSpec
import java.time.LocalDateTime
import java.time.ZoneOffset
import java.util.Collections
import java.util.Date
import java.util.Enumeration
import java.util.Locale
import javax.crypto.KeyGeneratorSpi
import javax.crypto.SecretKey
import javax.crypto.spec.SecretKeySpec
import org.json.JSONArray
import org.json.JSONObject
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows.shadowOf
import org.robolectric.shadows.ShadowAppWidgetManager

/**
 * A home screen hosting one "Today in Ember" widget under Robolectric, with today's snapshot helpers. Robolectric does
 * not register the manifest's widget receivers, so the host registers [TodayWidget] for the broadcasts aimed at it. On
 * API 31+ the widget pushes one layout per size; [view] inflates the one the launcher would show at a size.
 */
class TodayHost {

    val context: Context = RuntimeEnvironment.getApplication()
    private val manager = AppWidgetManager.getInstance(context)
    private val shadow = shadowOf(manager).apply { alwaysRecreateViewsDuringUpdate = true }
    val id = shadow.createWidget(TodayWidget::class.java, R.layout.widget_today)

    /** A UTC offset whose local time is at least an hour from midnight, so no test straddles a date change. */
    val zone: String
    val today: String

    init {
        val filter = IntentFilter()
        if (Build.VERSION.SDK_INT >= 33) context.registerReceiver(TodayWidget(), filter, Context.RECEIVER_EXPORTED)
        else context.registerReceiver(TodayWidget(), filter)
        val (offset, now) = (-12..14)
            .map { it to LocalDateTime.now(ZoneOffset.ofHours(it)) }
            .first { (_, now) -> now.hour in 1..22 }
        zone = String.format(Locale.ROOT, "GMT%+03d:00", offset)
        today = now.toLocalDate().toString()
    }

    fun habit(id: Long, name: String, state: String = "open", count: Int = 0, timesPerDay: Int = 1): JSONObject =
        JSONObject().put("id", id).put("name", name).put("state", state).put("count", count).put("timesPerDay", timesPerDay)

    /** Stores today's snapshot for "user-1" the way the app pushes it. */
    fun today(vararg habits: JSONObject, more: Int = 0, date: String = today, zone: String = this.zone) {
        WidgetStore.save(context, "user-1", date, zone, JSONArray(habits.toList()), more, null, null, null)
    }

    val token: String get() = WidgetStore.current(context)!!.getString("token")

    /** Stores a widget key with an origin no request reaches. */
    fun key() {
        FakeAndroidKeyStore.install()
        WidgetKey.set(context, "k".repeat(43), "https://127.0.0.1:1")
    }

    /** The widget as the launcher shows it at [width] x [height] dp, after a redraw. */
    fun view(width: Float, height: Float): View {
        if (Build.VERSION.SDK_INT < 31) {
            manager.updateAppWidgetOptions(
                id,
                Bundle().apply {
                    putInt(AppWidgetManager.OPTION_APPWIDGET_MIN_WIDTH, width.toInt())
                    putInt(AppWidgetManager.OPTION_APPWIDGET_MIN_HEIGHT, height.toInt())
                },
            )
            TodayWidget.refresh(context)
            return shadow.getViewFor(id)
        }
        TodayWidget.refresh(context)
        return sized(lastRemoteViews(), SizeF(width, height))
    }

    private fun lastRemoteViews(): RemoteViews {
        val infos = ShadowAppWidgetManager::class.java.getDeclaredField("widgetInfos").apply { isAccessible = true }
        val info = (infos.get(shadow) as Map<*, *>)[id]!!
        val last = info.javaClass.getDeclaredField("lastRemoteViews").apply { isAccessible = true }
        return last.get(info) as RemoteViews
    }

    private fun sized(views: RemoteViews, size: SizeF): View {
        val handler = Class.forName("android.widget.RemoteViews\$InteractionHandler")
        val apply = RemoteViews::class.java.getMethod(
            "apply",
            Context::class.java,
            ViewGroup::class.java,
            handler,
            SizeF::class.java,
        )
        return apply.invoke(views, context, FrameLayout(context), null, size) as View
    }
}

/** Whether [id] (the first view with it) and every parent up to [this] are visible. */
fun View.shows(id: Int): Boolean {
    var view: View? = findViewById(id) ?: return false
    while (view != null) {
        if (view.visibility != View.VISIBLE) return false
        if (view === this) return true
        view = view.parent as? View
    }
    return true
}

fun View.text(id: Int): String = findViewById<TextView>(id).text.toString()

/** Every visible view with [id], in layout order. */
fun View.all(id: Int): List<View> {
    val found = ArrayList<View>()
    fun walk(view: View) {
        if (view.visibility != View.VISIBLE) return
        if (view.id == id) found.add(view)
        if (view is ViewGroup) for (i in 0 until view.childCount) walk(view.getChildAt(i))
    }
    walk(this)
    return found
}

/** The habit names the widget lists, in order: its rows, or the single habit's name. */
fun View.names(): List<String> {
    val rows = all(R.id.widget_row_name).map { (it as TextView).text.toString() }
    if (rows.isNotEmpty() || !shows(R.id.widget_single)) return rows
    return listOf(text(R.id.widget_single_name))
}

/**
 * An in-memory stand-in for the device's AndroidKeyStore: AES keys generated under an alias and read back by it. JCA
 * providers are global, so [install] replaces any earlier one with one from this test's class loader.
 */
class FakeAndroidKeyStore : Provider("AndroidKeyStore", 1.0, "in-memory AndroidKeyStore for tests") {

    init {
        put("KeyStore.AndroidKeyStore", Store::class.java.name)
        put("KeyGenerator.AES", Generator::class.java.name)
    }

    class Store : KeyStoreSpi() {
        override fun engineGetKey(alias: String, password: CharArray?): Key? = keys[alias]
        override fun engineGetCertificateChain(alias: String): Array<Certificate>? = null
        override fun engineGetCertificate(alias: String): Certificate? = null
        override fun engineGetCreationDate(alias: String): Date? = null
        override fun engineSetKeyEntry(alias: String, key: Key, password: CharArray?, chain: Array<Certificate>?) {
            keys[alias] = key
        }
        override fun engineSetKeyEntry(alias: String, key: ByteArray, chain: Array<Certificate>?) = Unit
        override fun engineSetCertificateEntry(alias: String, cert: Certificate) = Unit
        override fun engineDeleteEntry(alias: String) {
            keys.remove(alias)
        }
        override fun engineAliases(): Enumeration<String> = Collections.enumeration(keys.keys)
        override fun engineContainsAlias(alias: String) = alias in keys
        override fun engineSize() = keys.size
        override fun engineIsKeyEntry(alias: String) = alias in keys
        override fun engineIsCertificateEntry(alias: String) = false
        override fun engineGetCertificateAlias(cert: Certificate): String? = null
        override fun engineStore(stream: OutputStream?, password: CharArray?) = Unit
        override fun engineLoad(stream: InputStream?, password: CharArray?) = Unit
    }

    class Generator : KeyGeneratorSpi() {
        private var alias = ""

        override fun engineInit(random: SecureRandom?) = Unit
        override fun engineInit(params: AlgorithmParameterSpec, random: SecureRandom?) {
            alias = (params as KeyGenParameterSpec).keystoreAlias
        }
        override fun engineInit(keysize: Int, random: SecureRandom?) = Unit
        override fun engineGenerateKey(): SecretKey {
            val bytes = ByteArray(32).also { SecureRandom().nextBytes(it) }
            return SecretKeySpec(bytes, "AES").also { keys[alias] = it }
        }
    }

    companion object {
        private val keys = HashMap<String, Key>()

        fun install() {
            keys.clear()
            Security.removeProvider("AndroidKeyStore")
            Security.insertProviderAt(FakeAndroidKeyStore(), 1)
        }
    }
}
