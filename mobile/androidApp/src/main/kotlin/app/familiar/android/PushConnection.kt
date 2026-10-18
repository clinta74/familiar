package app.familiar.android

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.Network
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import app.familiar.shared.api.ApiException
import app.familiar.shared.api.FamiliarApi
import app.familiar.shared.api.PushSignal
import app.familiar.shared.auth.Session
import app.familiar.shared.platform.BackgroundConnection
import app.familiar.shared.platform.KeystoreStore
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull

/**
 * Push without the ntfy app (BACKLOG item 38): a foreground service that keeps one small stream open
 * to the server - as the ntfy app did, but Familiar's own - with a quiet notification while it does.
 * The stream says only "changed"; on that (and on connecting, for what changed while it wasn't
 * listening) the alarms are set again from the pending list, and what went off is shown.
 *
 * Battery: the stream is idle between the server's pings, every 45 s, which keep the proxy and any
 * router from dropping it; a foreground service keeps its network through Doze. Dropped, it
 * reconnects at once when a network comes back, otherwise after a wait that doubles up to 5 minutes.
 * After a restart, the boot broadcast starts it again (AlarmReceiver).
 */
class PushConnectionService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var listening: Job? = null

    /** A network came back, or changed: stop waiting, and drop a stream on the old one. */
    private val networkChanged = Channel<Unit>(Channel.CONFLATED)
    private var network: Network? = null
    private var registered = false
    private val callback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) {
            val changed = network != this@PushConnectionService.network
            this@PushConnectionService.network = network
            // The first is the network it started on, said on registering.
            if (!registered) registered = true else if (changed) networkChanged.trySend(Unit)
        }

        override fun onLost(network: Network) {
            if (network == this@PushConnectionService.network) this@PushConnectionService.network = null
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_TURN_OFF) {
            // From the notification: the same as the switch in Settings, Devices.
            PushConnection.set(this, false)
            stopSelf()
            return START_NOT_STICKY
        }
        if (!PushConnection.isOn(this) || PushConnection.api(this) == null) {
            stopSelf()
            return START_NOT_STICKY
        }
        try {
            if (Build.VERSION.SDK_INT >= 34) {
                startForeground(NOTIFICATION, notification(), ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
            } else {
                startForeground(NOTIFICATION, notification())
            }
        } catch (exception: IllegalStateException) {
            // Not allowed from the background just now (a restart by the system, on Android 12+):
            // opening the app starts it again.
            stopSelf()
            return START_NOT_STICKY
        }
        if (listening?.isActive != true) {
            getSystemService(ConnectivityManager::class.java).registerDefaultNetworkCallback(callback)
            listening = scope.launch { listen() }
        }
        return START_STICKY
    }

    override fun onDestroy() {
        runCatching { getSystemService(ConnectivityManager::class.java).unregisterNetworkCallback(callback) }
        scope.cancel()
        super.onDestroy()
    }

    private suspend fun listen() {
        var wait = FIRST_WAIT_MS
        while (scope.isActive) {
            // Signed out, here or elsewhere: nothing to listen for.
            val api = PushConnection.api(this) ?: break
            try {
                coroutineScope {
                    val stream = launch {
                        api.pushStream().collect { signal ->
                            if (signal == PushSignal.Open) wait = FIRST_WAIT_MS
                            catchUp(api)
                        }
                    }
                    // A new network: the stream on the old one is dead, though it may not know yet.
                    val watch = launch {
                        networkChanged.receive()
                        stream.cancel()
                    }
                    stream.join()
                    watch.cancel()
                }
            } catch (exception: CancellationException) {
                if (!scope.isActive) throw exception
            } catch (exception: ApiException) {
                if (exception.unauthorized) break
            } catch (exception: Exception) {
                // Unreachable, or the stream went quiet: try again below.
            }
            withTimeoutOrNull(wait) { networkChanged.receive() }
            wait = (wait * 2).coerceAtMost(LONGEST_WAIT_MS)
        }
        stopSelf()
    }

    /** What changed: the alarms from the pending list, and what went off while this wasn't listening. */
    private suspend fun catchUp(api: FamiliarApi) {
        // Long enough for two small requests on a slow connection; the phone may be asleep.
        val wake = getSystemService(PowerManager::class.java)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "familiar:push")
            .apply { acquire(30_000) }
        try {
            val alarms = PhoneAlarms(this)
            runCatching { api.jobs() }.getOrNull()?.let { alarms.sync(it, alarms.familiarName()) }
            runCatching { api.recentNotifications() }.getOrNull()?.forEach {
                // With the app open, the conversation shows it: only noted, so it isn't shown later.
                if (AppServices.inFront) Alerts.seen(this, it.id) else Alerts.showRecent(this, it.id, it.title, it.body)
            }
        } finally {
            if (wake.isHeld) wake.release()
        }
    }

    private fun notification(): Notification {
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL, "Staying connected", NotificationManager.IMPORTANCE_MIN).apply {
                description = "Shown while Familiar keeps its connection with the app closed, for timers and announcements."
                setShowBadge(false)
            },
        )
        val open = PendingIntent.getActivity(
            this, 0,
            Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
            PendingIntent.FLAG_IMMUTABLE,
        )
        val turnOff = PendingIntent.getService(
            this, 2,
            Intent(this, PushConnectionService::class.java).setAction(ACTION_TURN_OFF),
            PendingIntent.FLAG_IMMUTABLE,
        )
        val name = PhoneAlarms(this).familiarName()
        return Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_launcher_foreground)
            .setContentTitle("$name is standing by")
            .setContentText("For timers and announcements while the app's closed.")
            .setContentIntent(open)
            .setOngoing(true)
            .setCategory(Notification.CATEGORY_SERVICE)
            .addAction(Notification.Action.Builder(null, "Turn off", turnOff).build())
            .build()
    }

    companion object {
        private const val CHANNEL = "connection"

        // The hands-free service's is 1.
        private const val NOTIFICATION = 2
        private const val ACTION_TURN_OFF = "app.familiar.PUSH_TURN_OFF"
        private const val FIRST_WAIT_MS = 2_000L
        private const val LONGEST_WAIT_MS = 5 * 60_000L
    }
}

/** Starting, stopping and the on/off setting of [PushConnectionService]. */
object PushConnection {
    private const val PREFS = "app"
    private const val ON = "push_connection"

    /** The signed-in device's API, if one is signed in. */
    fun api(context: Context): FamiliarApi? =
        Session(KeystoreStore(context.applicationContext)).current()?.let { FamiliarApi(it.server, it.token) }

    fun isOn(context: Context): Boolean = prefs(context).getBoolean(ON, true)

    fun set(context: Context, on: Boolean) {
        prefs(context).edit().putBoolean(ON, on).apply()
        if (on) start(context) else stop(context)
    }

    /** Starts it, if it's on and someone's signed in. Harmless to repeat. */
    fun start(context: Context) {
        if (!isOn(context) || api(context) == null) return
        // Refused from the background on Android 12+ except at boot or when the app's in front.
        runCatching { context.startForegroundService(Intent(context, PushConnectionService::class.java)) }
    }

    fun stop(context: Context) {
        context.stopService(Intent(context, PushConnectionService::class.java))
    }

    /** For the switch in Settings, Devices. */
    fun setting(context: Context) = object : BackgroundConnection {
        override fun isOn() = isOn(context)

        override fun set(on: Boolean) = set(context, on)
    }

    private fun prefs(context: Context) = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
}
