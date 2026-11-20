package app.ember.habits

import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.widget.RemoteViews

/**
 * The 1x1 "I need a moment" home-screen door. It holds no user data: it only shows the door label and opens
 * [MainActivity] with [MainActivity.ACTION_OPEN_MOMENT].
 */
class MomentWidget : AppWidgetProvider() {

    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) {
        val views = RemoteViews(context.packageName, R.layout.widget_moment)
        val label = AppLanguage.localized(context).getString(R.string.moment_label)
        views.setTextViewText(R.id.widget_moment_label, label)
        views.setContentDescription(R.id.widget_moment_root, label)
        val moment = Intent(context, MainActivity::class.java).setAction(MainActivity.ACTION_OPEN_MOMENT)
        views.setOnClickPendingIntent(R.id.widget_moment_root, WidgetIntents.open(context, 0, moment))
        manager.updateAppWidget(ids, views)
    }

    companion object {
        /** Redraws every moment widget, for example after the app's language changed. */
        internal fun refresh(context: Context) {
            val manager = AppWidgetManager.getInstance(context) ?: return
            val ids = manager.getAppWidgetIds(ComponentName(context, MomentWidget::class.java))
            if (ids != null && ids.isNotEmpty()) MomentWidget().onUpdate(context, manager, ids)
        }
    }
}
