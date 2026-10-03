package app.familiar.android

import android.app.Activity
import android.content.Context
import android.os.PowerManager
import app.familiar.shared.api.FamiliarApi
import app.familiar.shared.auth.Session
import app.familiar.shared.platform.KeystoreStore
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import org.unifiedpush.android.connector.FailedReason
import org.unifiedpush.android.connector.PushService
import org.unifiedpush.android.connector.UnifiedPush
import org.unifiedpush.android.connector.data.PushEndpoint
import org.unifiedpush.android.connector.data.PushMessage

/**
 * Push through the household's ntfy (UnifiedPush; docs/MOBILE.md §8): the ntfy app keeps the one
 * connection and wakes this. A push carries nothing but "something changed" - the timers, or
 * something went off - so this asks the server what, over its own connection: the alarms are set
 * again from the pending list, and what went off while the app wasn't listening is shown.
 */
class FamiliarPushService : PushService() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    override fun onNewEndpoint(endpoint: PushEndpoint, instance: String) {
        val api = Push.api(this) ?: return
        scope.launch { runCatching { api.subscribePush(endpoint.url) } }
    }

    override fun onMessage(message: PushMessage, instance: String) {
        val api = Push.api(this) ?: return
        val alarms = PhoneAlarms(this)
        // Long enough for two small requests on a slow connection; the phone may be asleep.
        val wake = getSystemService(PowerManager::class.java)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "familiar:push")
            .apply { acquire(30_000) }
        scope.launch {
            try {
                runCatching { api.jobs() }.getOrNull()?.let { alarms.sync(it, alarms.familiarName()) }
                runCatching { api.recentNotifications() }.getOrNull()?.forEach {
                    Alerts.showRecent(this@FamiliarPushService, it.id, it.title, it.body)
                }
            } finally {
                if (wake.isHeld) wake.release()
            }
        }
    }

    override fun onRegistrationFailed(reason: FailedReason, instance: String) = Unit

    override fun onUnregistered(instance: String) = Unit
}

object Push {
    /** The signed-in device's API, if one is signed in. */
    fun api(context: Context): FamiliarApi? =
        Session(KeystoreStore(context.applicationContext)).current()?.let { FamiliarApi(it.server, it.token) }

    /**
     * Registers with the phone's UnifiedPush distributor (the ntfy app), if there is one, with the
     * server's key, so only this server's pushes reach the app. Harmless to repeat; without a
     * distributor nothing happens and the app works as before, only without pushes while closed.
     */
    fun register(activity: Activity) {
        val api = api(activity) ?: return
        CoroutineScope(Dispatchers.Main).launch {
            val key = runCatching { api.pushKey() }.getOrNull() ?: return@launch
            UnifiedPush.tryUseCurrentOrDefaultDistributor(activity) { ok ->
                if (ok) UnifiedPush.register(activity, INSTANCE, null, key)
            }
        }
    }

    private const val INSTANCE = "default"
}
