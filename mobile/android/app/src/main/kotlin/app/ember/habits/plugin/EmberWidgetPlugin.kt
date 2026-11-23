package app.ember.habits.plugin

import android.content.ActivityNotFoundException
import android.content.Intent
import app.ember.habits.AppLanguage
import app.ember.habits.MomentWidget
import app.ember.habits.TodayWidget
import app.ember.habits.widget.WidgetKey
import app.ember.habits.widget.WidgetStore
import com.getcapacitor.JSObject
import com.getcapacitor.Plugin
import com.getcapacitor.PluginCall
import com.getcapacitor.PluginMethod
import com.getcapacitor.annotation.CapacitorPlugin
import org.json.JSONException
import org.json.JSONObject

/**
 * Bridge to the "Today in Ember" widget: `update` stores a validated snapshot of today's habits in app-private storage
 * and redraws, `clear` forgets it with the widget key and pending check-ins, and validated widget taps that open the
 * app arrive as `widgetTap` events (retained until JS listens, so a cold-start tap is not lost). `setStyle` sets its
 * theme, colour scheme and motion. `setKey` and `hasKey` manage the key the widget checks in with on its own, and
 * `takePending` hands its unconfirmed check-ins to JS. The "I need a moment" door is a pending launch action on a
 * cold start (`takeLaunchAction`) and an `openMoment` event on a warm one. `canOpenScreenTime` and `openScreenTime`
 * point to the system's screen-time screen.
 */
@CapacitorPlugin(name = "EmberWidget")
class EmberWidgetPlugin : Plugin() {

    private var launchAction: String? = null

    /**
     * Validates and stores the snapshot ([WidgetStore.save], with the `held` rows of the day and of `next`) with a
     * fresh tap token, then redraws the Today widget. Rejects an invalid snapshot and keeps the stored one.
     */
    @PluginMethod
    fun update(call: PluginCall) {
        val habits = call.getArray("habits")
        val more = call.getInt("more")
        try {
            if (habits == null || more == null) throw JSONException("habits and more are required")
            WidgetStore.save(
                context,
                call.getString("userId"),
                call.getString("date"),
                call.getString("timeZone"),
                habits,
                more,
                call.getInt("done"),
                call.getInt("total"),
                call.getObject("next"),
                call.getArray("held"),
            )
            TodayWidget.refresh(context)
        } catch (e: JSONException) {
            call.reject("Invalid widget snapshot")
            return
        } catch (e: IllegalArgumentException) {
            call.reject("Invalid widget snapshot")
            return
        }
        call.resolve()
    }

    /**
     * Forgets the snapshot, widget key, API origin and pending check-ins (sign-out, session end, account deletion),
     * keeping the style, and redraws the signed-out state.
     */
    @PluginMethod
    fun clear(call: PluginCall) {
        WidgetStore.clear(context)
        TodayWidget.refresh(context)
        call.resolve()
    }

    /**
     * Stores the widget check-in key (`key`, base64url) for `apiBase` (an absolute https origin without path), sealed
     * with an AndroidKeyStore key, then redraws. Rejects invalid input.
     */
    @PluginMethod
    fun setKey(call: PluginCall) {
        try {
            WidgetKey.set(context, call.getString("key"), call.getString("apiBase"))
        } catch (e: Exception) {
            call.reject("Invalid widget key")
            return
        }
        TodayWidget.refresh(context)
        call.resolve()
    }

    /** Resolves `{ value }`, true when a stored widget key decrypts. */
    @PluginMethod
    fun hasKey(call: PluginCall) {
        val result = JSObject()
        result.put("value", WidgetKey.get(context) != null)
        call.resolve(result)
    }

    /**
     * Resolves `{ checkIns: [{ userId, habitId, date, count }] }` with the widget's unconfirmed check-ins (`count` is
     * the absolute count each tap aimed for) and removes them, then redraws.
     */
    @PluginMethod
    fun takePending(call: PluginCall) {
        val checkIns = WidgetStore.takePending(context)
        TodayWidget.refresh(context)
        val result = JSObject()
        result.put("checkIns", checkIns)
        call.resolve(result)
    }

    /**
     * Stores the app's language for native text (`language` "de", anything else or missing is "en"), then redraws
     * both widgets in it. Always resolves.
     */
    @PluginMethod
    fun setLanguage(call: PluginCall) {
        AppLanguage.set(context, call.getString("language"))
        TodayWidget.refresh(context)
        MomentWidget.refresh(context)
        call.resolve()
    }

    /**
     * Stores the widget's look (`theme`, `colorScheme` light, dark or system, `motion`), then redraws the Today widget
     * in it. Always resolves.
     */
    @PluginMethod
    fun setStyle(call: PluginCall) {
        WidgetStore.setStyle(
            context,
            call.getString("theme"),
            call.getBoolean("motion", false) == true,
            call.getString("colorScheme"),
        )
        TodayWidget.refresh(context)
        call.resolve()
    }

    /** Resolves `{ available }`, true when this device has a screen-time screen [ScreenTime] can open. */
    @PluginMethod
    fun canOpenScreenTime(call: PluginCall) {
        val result = JSObject()
        result.put("available", ScreenTime.intent(context) != null)
        call.resolve(result)
    }

    /** Opens the system's screen-time screen in its own task, or rejects when there is none. */
    @PluginMethod
    fun openScreenTime(call: PluginCall) {
        val intent = ScreenTime.intent(context)
        if (intent == null) {
            call.reject("Screen time is not available")
            return
        }
        try {
            context.startActivity(intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        } catch (e: ActivityNotFoundException) {
            call.reject("Screen time is not available")
            return
        } catch (e: SecurityException) {
            call.reject("Screen time is not available")
            return
        }
        call.resolve()
    }

    /** Resolves `{ action: 'moment' }` or `{ action: null }` and clears the pending launch action. */
    @PluginMethod
    fun takeLaunchAction(call: PluginCall) {
        val result = JSObject()
        result.put("action", launchAction ?: JSONObject.NULL)
        launchAction = null
        call.resolve(result)
    }

    /** Hands a validated widget tap to JS, retained until a listener takes it. */
    internal fun tap(tap: JSObject) {
        notifyListeners("widgetTap", tap, true)
    }

    /** Makes the moment door the pending launch action, for JS to take once it starts. */
    internal fun pendMoment() {
        launchAction = "moment"
    }

    /** Tells a running app that a moment door was used, retained until a listener takes it. */
    internal fun openMoment() {
        notifyListeners("openMoment", JSObject(), true)
    }
}
