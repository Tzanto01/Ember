package app.ember.habits;

import com.getcapacitor.JSArray;
import com.getcapacitor.JSObject;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.annotation.CapacitorPlugin;
import org.json.JSONException;
import org.json.JSONObject;

/**
 * Bridge to the "Today in Ember" widget: {@code update} stores a validated snapshot of today's habits in
 * app-private storage and redraws, {@code clear} forgets it with the widget key and pending check-ins, and validated
 * widget taps that open the app arrive as {@code widgetTap} events (retained until JS listens, so a cold-start tap is
 * not lost). {@code setStyle} sets its theme, colour scheme and motion. {@code setKey} and {@code hasKey} manage the
 * key the widget checks in with on its own, and {@code takePending} hands its unconfirmed check-ins to JS. The
 * "I need a moment" door
 * is a pending launch action on a cold start ({@code takeLaunchAction}) and an {@code openMoment} event on a warm one.
 */
@CapacitorPlugin(name = "EmberWidget")
public class EmberWidgetPlugin extends Plugin {

    private String launchAction;

    @PluginMethod
    public void update(PluginCall call) {
        JSArray habits = call.getArray("habits");
        Integer more = call.getInt("more");
        try {
            if (habits == null || more == null) throw new JSONException("habits and more are required");
            TodayWidget.save(
                getContext(),
                call.getString("userId"),
                call.getString("date"),
                call.getString("timeZone"),
                habits,
                more,
                call.getInt("done"),
                call.getInt("total"),
                call.getObject("next")
            );
        } catch (JSONException | IllegalArgumentException e) {
            call.reject("Invalid widget snapshot");
            return;
        }
        call.resolve();
    }

    @PluginMethod
    public void clear(PluginCall call) {
        TodayWidget.clear(getContext());
        call.resolve();
    }

    /**
     * Stores the widget check-in key ({@code key}, base64url) for {@code apiBase} (an absolute https origin without
     * path), sealed with an AndroidKeyStore key, then redraws. Rejects invalid input.
     */
    @PluginMethod
    public void setKey(PluginCall call) {
        try {
            WidgetKey.set(getContext(), call.getString("key"), call.getString("apiBase"));
        } catch (Exception e) {
            call.reject("Invalid widget key");
            return;
        }
        TodayWidget.refresh(getContext());
        call.resolve();
    }

    /** Resolves {@code { value }}, true when a stored widget key decrypts. */
    @PluginMethod
    public void hasKey(PluginCall call) {
        JSObject result = new JSObject();
        result.put("value", WidgetKey.get(getContext()) != null);
        call.resolve(result);
    }

    /**
     * Resolves {@code { checkIns: [{ userId, habitId, date, count }] }} with the widget's unconfirmed check-ins
     * ({@code count} is the absolute count each tap aimed for) and removes them.
     */
    @PluginMethod
    public void takePending(PluginCall call) {
        JSObject result = new JSObject();
        result.put("checkIns", TodayWidget.takePending(getContext()));
        call.resolve(result);
    }

    /**
     * Stores the app's language for native text ({@code language} "de", anything else or missing is "en"), then
     * redraws both widgets in it. Always resolves.
     */
    @PluginMethod
    public void setLanguage(PluginCall call) {
        AppLanguage.set(getContext(), call.getString("language"));
        TodayWidget.refresh(getContext());
        MomentWidget.refresh(getContext());
        call.resolve();
    }

    /**
     * Stores the widget's look ({@code theme}, {@code colorScheme} light, dark or system, {@code motion}), then
     * redraws the Today widget in it. Always resolves.
     */
    @PluginMethod
    public void setStyle(PluginCall call) {
        TodayWidget.setStyle(
            getContext(),
            call.getString("theme"),
            Boolean.TRUE.equals(call.getBoolean("motion", false)),
            call.getString("colorScheme")
        );
        call.resolve();
    }

    /** Resolves {@code { action: 'moment' }} or {@code { action: null }} and clears the pending launch action. */
    @PluginMethod
    public void takeLaunchAction(PluginCall call) {
        JSObject result = new JSObject();
        result.put("action", launchAction == null ? JSONObject.NULL : launchAction);
        launchAction = null;
        call.resolve(result);
    }

    void tap(JSObject tap) {
        notifyListeners("widgetTap", tap, true);
    }

    void pendMoment() {
        launchAction = "moment";
    }

    void openMoment() {
        notifyListeners("openMoment", new JSObject(), true);
    }
}
