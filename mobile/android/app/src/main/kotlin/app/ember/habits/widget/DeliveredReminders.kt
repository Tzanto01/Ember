package app.ember.habits.widget

import android.app.NotificationManager
import android.content.Context
import com.capacitorjs.plugins.localnotifications.LocalNotificationManager
import com.capacitorjs.plugins.localnotifications.NotificationStorage

/**
 * The habit reminders the app scheduled through the local-notifications plugin and Android has delivered. The posted
 * notification carries only the plugin's id; the reminder's `extra` (`userId`, `habitId`, `date`, as the app's
 * `reminders.ts` writes it) lives in the plugin's own record of that id ([NotificationStorage]).
 */
internal object DeliveredReminders {

    /**
     * Removes the delivered reminders of [userId] for [habitId] on [date] from the shade, and forgets their plugin
     * records the way the plugin does when the user dismisses one. A failure leaves the rest in place.
     */
    fun clear(context: Context, userId: String, habitId: Long, date: String) {
        val manager = context.getSystemService(NotificationManager::class.java) ?: return
        val storage = NotificationStorage(context)
        try {
            for (delivered in manager.activeNotifications) {
                val id = delivered.id.toString()
                val extra = storage.getSavedNotificationAsJSObject(id)?.optJSONObject("extra") ?: continue
                if (extra.optString("userId") != userId || extra.optLong("habitId") != habitId) continue
                if (extra.optString("date") != date) continue
                manager.cancel(delivered.tag, delivered.id)
                if (LocalNotificationManager.isSafeToForget(storage.getSavedNotification(id))) storage.deleteNotification(id)
            }
        } catch (e: RuntimeException) {
        }
    }
}
