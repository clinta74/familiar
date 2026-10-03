package app.familiar.shared.ui

import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import app.familiar.shared.auth.Session
import app.familiar.shared.platform.DeviceAlarms
import app.familiar.shared.platform.DeviceCalendar
import app.familiar.shared.platform.SecureStore
import app.familiar.shared.session.FamiliarSession
import app.familiar.shared.session.FamiliarSessions
import app.familiar.shared.voice.HandsFreeAudio
import app.familiar.shared.voice.Microphone
import app.familiar.shared.voice.SpeechAudio

/**
 * The whole app: sign in to a Familiar server, then talk. The same Compose code will run on iOS
 * (docs/MOBILE.md §1); only the store, the device name, the phone's calendar and its speaker come
 * from the platform, and so do its microphone and hands-free listening. The signed-in session is the
 * app's ([FamiliarSessions]), not this screen's, so it survives the activity being recreated.
 */
@Composable
fun FamiliarApp(
    store: SecureStore,
    deviceName: String,
    calendar: DeviceCalendar? = null,
    speechAudio: SpeechAudio? = null,
    microphone: Microphone? = null,
    handsFree: HandsFreeAudio? = null,
    alarms: DeviceAlarms? = null,
    /** Signed in (or still signed in, on opening): the platform registers for push. */
    onSession: () -> Unit = {},
) {
    val auth = remember { Session(store) }
    var signedIn by remember { mutableStateOf(auth.current()) }

    FamiliarTheme {
        val current = signedIn
        if (current == null) {
            SignInScreen(
                initialServer = auth.lastServer().orEmpty(),
                deviceName = deviceName,
                onSignedIn = {
                    auth.save(it)
                    signedIn = it
                },
            )
        } else {
            val session = remember(current) {
                FamiliarSessions.open(current) { FamiliarSession(current, store, calendar, speechAudio, microphone, handsFree, alarms) }
            }
            val ended by session.ended.collectAsState()
            LaunchedEffect(session) { onSession() }
            LaunchedEffect(ended) {
                if (ended) {
                    auth.clear()
                    FamiliarSessions.close()
                    signedIn = null
                }
            }
            ChatScreen(session)
        }
    }
}
