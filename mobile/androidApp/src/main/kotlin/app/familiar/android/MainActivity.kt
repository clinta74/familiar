package app.familiar.android

import android.Manifest
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import app.familiar.shared.platform.AndroidDeviceCalendar
import app.familiar.shared.platform.KeystoreStore
import app.familiar.shared.ui.FamiliarApp
import app.familiar.shared.voice.AndroidSpeechAudio
import kotlinx.coroutines.CompletableDeferred

/** The app's one screen; everything inside is the shared Compose UI (mobile/shared). */
class MainActivity : ComponentActivity() {
    private var permissionAnswer: CompletableDeferred<Unit>? = null
    private val calendarPermission = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
        permissionAnswer?.complete(Unit)
        permissionAnswer = null
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        val store = KeystoreStore(applicationContext)
        val deviceName = listOf(Build.MANUFACTURER.replaceFirstChar { it.uppercase() }, Build.MODEL)
            .distinct()
            .joinToString(" ")
            .take(64)
        val calendar = AndroidDeviceCalendar(applicationContext) {
            val answer = CompletableDeferred<Unit>().also { permissionAnswer = it }
            calendarPermission.launch(arrayOf(Manifest.permission.READ_CALENDAR, Manifest.permission.WRITE_CALENDAR))
            answer.await()
        }
        val speech = AndroidSpeechAudio(applicationContext)
        setContent { FamiliarApp(store, deviceName, calendar, speech) }
    }
}
