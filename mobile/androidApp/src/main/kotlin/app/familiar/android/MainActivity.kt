package app.familiar.android

import android.Manifest
import android.content.Context
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import app.familiar.shared.platform.AndroidDeviceCalendar
import app.familiar.shared.platform.KeystoreStore
import app.familiar.shared.ui.FamiliarApp
import app.familiar.shared.voice.AndroidHandsFree
import app.familiar.shared.voice.AndroidMicrophone
import app.familiar.shared.voice.AndroidSpeechAudio
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.launch

/** The app's one screen; everything inside is the shared Compose UI (mobile/shared). */
class MainActivity : ComponentActivity() {
    private companion object {
        const val ASKED_NOTIFICATIONS = "asked-notifications"
    }

    private var permissionAnswer: CompletableDeferred<Unit>? = null

    /** The system permission prompt, for whichever permissions are asked (calendar, microphone). */
    private val permissions = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
        permissionAnswer?.complete(Unit)
        permissionAnswer = null
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        AppServices.init(applicationContext)
        Permissions.activity = this
        setContent {
            FamiliarApp(
                AppServices.store,
                AppServices.deviceName,
                AppServices.calendar,
                AppServices.speech,
                AppServices.microphone,
                AppServices.handsFree,
                AppServices.alarms,
                onSession = {
                    askForNotifications()
                    Push.register(this)
                },
            )
        }
    }

    override fun onDestroy() {
        if (Permissions.activity === this) Permissions.activity = null
        permissionAnswer?.complete(Unit)
        super.onDestroy()
    }

    /**
     * Timers and reminders ring as notifications (PhoneAlarms), which Android 13+ won't show without
     * permission: asked once, after signing in, rather than when the first timer goes off unseen.
     */
    private fun askForNotifications() {
        if (Build.VERSION.SDK_INT < 33) return
        if (checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == android.content.pm.PackageManager.PERMISSION_GRANTED) return
        val prefs = getSharedPreferences("app", MODE_PRIVATE)
        if (prefs.getBoolean(ASKED_NOTIFICATIONS, false)) return
        prefs.edit().putBoolean(ASKED_NOTIFICATIONS, true).apply()
        lifecycleScope.launch { Permissions.ask(Manifest.permission.POST_NOTIFICATIONS) }
    }

    /** Shows the prompt and waits for the answer. */
    suspend fun ask(names: Array<String>) {
        val answer = CompletableDeferred<Unit>().also { permissionAnswer = it }
        permissions.launch(names)
        answer.await()
    }
}

/**
 * Permission prompts go to whichever activity is showing: the session (and the platform services
 * it uses) outlive any one activity, so they can't keep the one they were made with.
 */
object Permissions {
    var activity: MainActivity? = null

    suspend fun ask(vararg names: String) {
        activity?.ask(arrayOf(*names))
    }
}

/**
 * The phone's side of the app, made once per process: the session holds on to these for as long as
 * it's signed in, across activities.
 */
object AppServices {
    lateinit var store: KeystoreStore
        private set
    lateinit var deviceName: String
        private set
    lateinit var calendar: AndroidDeviceCalendar
        private set
    lateinit var speech: AndroidSpeechAudio
        private set
    lateinit var microphone: AndroidMicrophone
        private set
    var handsFree: AndroidHandsFree? = null
        private set
    lateinit var alarms: PhoneAlarms
        private set

    fun init(context: Context) {
        if (::store.isInitialized) return
        store = KeystoreStore(context)
        deviceName = listOf(Build.MANUFACTURER.replaceFirstChar { it.uppercase() }, Build.MODEL)
            .distinct()
            .joinToString(" ")
            .take(64)
        calendar = AndroidDeviceCalendar(context) {
            Permissions.ask(Manifest.permission.READ_CALENDAR, Manifest.permission.WRITE_CALENDAR)
        }
        speech = AndroidSpeechAudio(context)
        alarms = PhoneAlarms(context)
        microphone = AndroidMicrophone(context) { Permissions.ask(Manifest.permission.RECORD_AUDIO) }
        handsFree = AndroidHandsFree(
            context,
            askPermission = {
                // The service's notification needs its own permission on Android 13+.
                if (Build.VERSION.SDK_INT >= 33) {
                    Permissions.ask(Manifest.permission.RECORD_AUDIO, Manifest.permission.POST_NOTIFICATIONS)
                } else {
                    Permissions.ask(Manifest.permission.RECORD_AUDIO)
                }
            },
            keepAlive = { on -> if (on) HandsFreeService.start(context) else HandsFreeService.stop(context) },
        )
    }
}
