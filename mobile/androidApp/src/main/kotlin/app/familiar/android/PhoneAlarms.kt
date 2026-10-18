package app.familiar.android

import android.app.AlarmManager
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.media.AudioAttributes
import android.media.RingtoneManager
import android.os.Build
import app.familiar.shared.platform.DeviceAlarms
import app.familiar.shared.protocol.JobView
import app.familiar.shared.protocol.isTimer
import org.json.JSONArray
import org.json.JSONObject
import java.time.Instant

/**
 * The familiar's timers and reminders as the phone's own alarms (docs/MOBILE.md, M3): each pending
 * job is an `AlarmManager.setAlarmClock` alarm, so it goes off with no connection - airplane mode,
 * the server down - and shows in the status bar's alarm icon. Kept in step with the server's list
 * (the stream's `jobs` event, or a push saying it changed), and put back after a restart.
 */
class PhoneAlarms(context: Context) : DeviceAlarms {
    private val context = context.applicationContext
    private val manager = this.context.getSystemService(AlarmManager::class.java)
    private val prefs = this.context.getSharedPreferences("alarms", Context.MODE_PRIVATE)

    data class Alarm(val id: String, val timer: Boolean, val label: String, val dueAt: Long, val ring: Boolean, val familiar: String)

    override fun message(id: String, title: String, body: String) = Alerts.message(context, id, title, body)

    override fun dismissMessage(id: String) = Alerts.dismiss(context, id)

    @Synchronized
    override fun sync(jobs: List<JobView>, familiarName: String) {
        val wanted = jobs.mapNotNull { job ->
            val due = runCatching { Instant.parse(job.dueAt).toEpochMilli() }.getOrNull() ?: return@mapNotNull null
            Alarm(job.id, job.isTimer, job.label, due, job.ring || job.isTimer, familiarName.ifBlank { "Familiar" })
        }
        prefs.edit().putString(NAME, familiarName).apply()
        val ids = wanted.map { it.id }.toSet()
        stored().filter { it.id !in ids }.forEach { cancel(it) }
        wanted.forEach { schedule(it) }
        store(wanted)
    }

    /** The alarms still to go off, for the widget. */
    fun upcoming(): List<Alarm> = stored().filter { it.dueAt > System.currentTimeMillis() }

    /** The familiar's name as last seen, for alarms set while the app isn't open. */
    fun familiarName(): String = prefs.getString(NAME, null) ?: "Familiar"

    /** After a restart (alarms don't survive one): set them again; any missed meanwhile go off now. */
    @Synchronized
    fun restore() {
        val now = System.currentTimeMillis()
        val (missed, pending) = stored().partition { it.dueAt <= now }
        pending.forEach { schedule(it) }
        missed.forEach { Alerts.show(context, it, late = true) }
        store(pending)
    }

    /** One went off: show it, and forget it. */
    @Synchronized
    fun fired(id: String) {
        val alarm = stored().firstOrNull { it.id == id } ?: return
        Alerts.show(context, alarm, late = System.currentTimeMillis() - alarm.dueAt > 60_000)
        store(stored().filter { it.id != id })
    }

    private fun schedule(alarm: Alarm) {
        val fire = fireIntent(alarm.id)
        val exact = Build.VERSION.SDK_INT < 31 || manager.canScheduleExactAlarms()
        if (exact) {
            val show = PendingIntent.getActivity(
                context, 0, Intent(context, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE,
            )
            manager.setAlarmClock(AlarmManager.AlarmClockInfo(alarm.dueAt, show), fire)
        } else {
            // Not allowed exact alarms: within a few minutes, still while dozing.
            manager.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, alarm.dueAt, fire)
        }
    }

    private fun cancel(alarm: Alarm) = manager.cancel(fireIntent(alarm.id))

    private fun fireIntent(id: String) = PendingIntent.getBroadcast(
        context,
        id.hashCode(),
        Intent(context, AlarmReceiver::class.java).setAction(ACTION_FIRE).putExtra(EXTRA_ID, id),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )

    private fun stored(): List<Alarm> = runCatching {
        val array = JSONArray(prefs.getString(KEY, null) ?: "[]")
        (0 until array.length()).map { i ->
            array.getJSONObject(i).run {
                Alarm(getString("id"), getBoolean("timer"), getString("label"), getLong("dueAt"), getBoolean("ring"), getString("familiar"))
            }
        }
    }.getOrDefault(emptyList())

    private fun store(alarms: List<Alarm>) {
        val array = JSONArray(alarms.map {
            JSONObject().put("id", it.id).put("timer", it.timer).put("label", it.label)
                .put("dueAt", it.dueAt).put("ring", it.ring).put("familiar", it.familiar)
        })
        prefs.edit().putString(KEY, array.toString()).apply()
        NextUpWidget.refresh(context)
    }

    companion object {
        const val ACTION_FIRE = "app.familiar.ALARM_FIRE"
        const val ACTION_DISMISS = "app.familiar.ALARM_DISMISS"
        const val EXTRA_ID = "id"
        private const val KEY = "alarms"
        private const val NAME = "familiar"
    }
}

/** An alarm going off, the dismiss button on one, and the phone restarting. */
class AlarmReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val alarms = PhoneAlarms(context)
        when (intent.action) {
            PhoneAlarms.ACTION_FIRE -> intent.getStringExtra(PhoneAlarms.EXTRA_ID)?.let(alarms::fired)
            PhoneAlarms.ACTION_DISMISS -> intent.getStringExtra(PhoneAlarms.EXTRA_ID)?.let { Alerts.dismiss(context, it) }
            Intent.ACTION_LOCKED_BOOT_COMPLETED -> alarms.restore()
            // Unlocked, the sign-in can be read: the push connection starts again too (BACKLOG item 38).
            Intent.ACTION_BOOT_COMPLETED, Intent.ACTION_MY_PACKAGE_REPLACED -> {
                alarms.restore()
                PushConnection.start(context)
            }
        }
    }
}

/**
 * What the phone shows when one goes off. Timers and alarm reminders ring - the alarm sound, over
 * and over, on the alarm volume, full screen on the lock screen - until dismissed; other reminders
 * are an ordinary notification with a sound.
 */
object Alerts {
    private const val RINGING = "ringing"
    private const val REMINDERS = "reminders"

    /** What's been shown, so a push about the same thing doesn't show it twice. */
    private const val SHOWN = "shown"

    fun show(context: Context, alarm: PhoneAlarms.Alarm, late: Boolean) {
        val title = when {
            alarm.timer -> "${alarm.familiar}: time's up"
            alarm.ring -> "${alarm.familiar}: ${alarm.label.ifBlank { "alarm" }}"
            else -> "${alarm.familiar}: reminder"
        }
        val body = when {
            alarm.timer -> if (alarm.label.isNotBlank()) "Your ${alarm.label} timer is done." else "Your timer is done."
            else -> alarm.label
        } + if (late) " (missed while the phone was off)" else ""
        post(context, alarm.id, title, body, alarm.ring)
    }

    /** A notification for something that went off while this phone wasn't listening (from a push). */
    fun showRecent(context: Context, id: String, title: String, body: String) {
        if (id in shown(context)) return
        post(context, id, title, body, ring = false)
    }

    /**
     * What the familiar said while the app was in the background. Shown even if a push showed it
     * already: the same id replaces it, with the familiar's own words.
     */
    fun message(context: Context, id: String, title: String, body: String) = post(context, id, title, body, ring = false, message = true)

    /** Already seen in the open app: noted, so a later push doesn't show it. */
    fun seen(context: Context, id: String) = remember(context, id)

    fun dismiss(context: Context, id: String) {
        context.getSystemService(NotificationManager::class.java).cancel(id.hashCode())
    }

    private fun post(context: Context, id: String, title: String, body: String, ring: Boolean, message: Boolean = false) {
        val manager = context.getSystemService(NotificationManager::class.java)
        channels(manager)
        val open = PendingIntent.getActivity(
            context, id.hashCode(),
            Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
            PendingIntent.FLAG_IMMUTABLE,
        )
        val builder = Notification.Builder(context, if (ring) RINGING else REMINDERS)
            .setSmallIcon(R.drawable.ic_launcher_foreground)
            .setContentTitle(title)
            .setContentText(body)
            .setStyle(Notification.BigTextStyle().bigText(body))
            .setContentIntent(open)
            .setAutoCancel(true)
            .setCategory(if (ring) Notification.CATEGORY_ALARM else if (message) Notification.CATEGORY_MESSAGE else Notification.CATEGORY_REMINDER)
        if (ring) {
            val dismiss = PendingIntent.getBroadcast(
                context, id.hashCode(),
                Intent(context, AlarmReceiver::class.java).setAction(PhoneAlarms.ACTION_DISMISS).putExtra(PhoneAlarms.EXTRA_ID, id),
                PendingIntent.FLAG_IMMUTABLE,
            )
            builder.addAction(Notification.Action.Builder(null, "Dismiss", dismiss).build())
                .setDeleteIntent(dismiss)
                .setFullScreenIntent(open, true)
                .setOngoing(true)
        }
        val notification = builder.build()
        // Rings until dismissed, as an alarm clock does.
        if (ring) notification.flags = notification.flags or Notification.FLAG_INSISTENT
        manager.notify(id.hashCode(), notification)
        remember(context, id)
    }

    private fun channels(manager: NotificationManager) {
        val alarm = RingtoneManager.getDefaultUri(RingtoneManager.TYPE_ALARM)
        manager.createNotificationChannel(
            NotificationChannel(RINGING, "Timers and alarms", NotificationManager.IMPORTANCE_HIGH).apply {
                description = "Timers, and reminders set as alarms: they ring until dismissed."
                setSound(alarm, AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_ALARM).build())
                enableVibration(true)
                setBypassDnd(true)
            },
        )
        manager.createNotificationChannel(
            NotificationChannel(REMINDERS, "Reminders", NotificationManager.IMPORTANCE_HIGH).apply {
                description = "Reminders and messages from your familiar."
            },
        )
    }

    private fun shown(context: Context): Set<String> =
        context.getSharedPreferences("alarms", Context.MODE_PRIVATE).getStringSet(SHOWN, emptySet()).orEmpty()

    private fun remember(context: Context, id: String) {
        val prefs = context.getSharedPreferences("alarms", Context.MODE_PRIVATE)
        // The last fifty is plenty: a push only asks about the last quarter of an hour.
        val shown = (prefs.getStringSet(SHOWN, emptySet()).orEmpty().toList() + id).takeLast(50).toSet()
        prefs.edit().putStringSet(SHOWN, shown).apply()
    }
}
