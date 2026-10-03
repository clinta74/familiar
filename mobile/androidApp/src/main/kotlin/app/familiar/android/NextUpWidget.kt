package app.familiar.android

import android.app.PendingIntent
import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.widget.RemoteViews
import java.text.DateFormat
import java.util.Date

/**
 * The home-screen widget (M4): the next timer or reminder, from the phone's own alarms, and a Talk
 * button that opens the familiar listening. Redrawn whenever the alarms change.
 */
class NextUpWidget : AppWidgetProvider() {
    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) {
        ids.forEach { manager.updateAppWidget(it, views(context)) }
    }

    companion object {
        /** The alarms changed: redraw every widget. */
        fun refresh(context: Context) {
            val manager = AppWidgetManager.getInstance(context)
            val ids = manager.getAppWidgetIds(ComponentName(context, NextUpWidget::class.java))
            if (ids.isNotEmpty()) ids.forEach { manager.updateAppWidget(it, views(context)) }
        }

        private fun views(context: Context): RemoteViews {
            val alarms = PhoneAlarms(context)
            val next = alarms.upcoming().minByOrNull { it.dueAt }
            val views = RemoteViews(context.packageName, R.layout.widget_next_up)
            views.setTextViewText(R.id.widget_name, alarms.familiarName())
            views.setTextViewText(
                R.id.widget_next,
                when {
                    next == null -> "Nothing coming up"
                    next.timer -> "Timer${if (next.label.isNotBlank()) " - ${next.label}" else ""}, ${time(next.dueAt)}"
                    else -> "${next.label}, ${time(next.dueAt)}"
                },
            )
            views.setOnClickPendingIntent(
                R.id.widget_talk,
                PendingIntent.getActivity(context, 1, Talk.intent(context), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT),
            )
            views.setOnClickPendingIntent(
                R.id.widget_root,
                PendingIntent.getActivity(
                    context, 2, Intent(context, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE,
                ),
            )
            return views
        }

        private fun time(at: Long): String {
            val today = DateFormat.getDateInstance(DateFormat.SHORT).format(Date())
            val day = DateFormat.getDateInstance(DateFormat.SHORT).format(Date(at))
            val clock = DateFormat.getTimeInstance(DateFormat.SHORT).format(Date(at))
            return if (day == today) clock else "$day $clock"
        }
    }
}
