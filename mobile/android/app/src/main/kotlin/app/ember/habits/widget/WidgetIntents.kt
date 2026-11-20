package app.ember.habits.widget

import android.app.PendingIntent
import android.content.Context
import android.content.Intent

/** The immutable pending intents behind the home-screen widgets' taps and alarms. */
internal object WidgetIntents {

    /** Opens [intent]'s activity in a new task. */
    fun open(context: Context, requestCode: Int, intent: Intent): PendingIntent {
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        return PendingIntent.getActivity(
            context,
            requestCode,
            intent,
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
    }

    /** Sends [intent] as a broadcast. */
    fun broadcast(context: Context, requestCode: Int, intent: Intent): PendingIntent =
        PendingIntent.getBroadcast(
            context,
            requestCode,
            intent,
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
}
