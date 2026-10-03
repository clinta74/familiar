package app.familiar.shared.ui

import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import app.familiar.shared.auth.Session
import app.familiar.shared.platform.DeviceCalendar
import app.familiar.shared.platform.SecureStore
import app.familiar.shared.voice.Microphone
import app.familiar.shared.voice.SpeechAudio

/**
 * The whole app: sign in to a Familiar server, then talk. The same Compose code will run on iOS
 * (docs/MOBILE.md §1); only the store, the device name, the phone's calendar and its speaker come
 * from the platform, and so does its microphone.
 */
@Composable
fun FamiliarApp(
    store: SecureStore,
    deviceName: String,
    calendar: DeviceCalendar? = null,
    speechAudio: SpeechAudio? = null,
    microphone: Microphone? = null,
) {
    val session = remember { Session(store) }
    var signedIn by remember { mutableStateOf(session.current()) }

    FamiliarTheme {
        val current = signedIn
        if (current == null) {
            SignInScreen(
                initialServer = session.lastServer().orEmpty(),
                deviceName = deviceName,
                onSignedIn = {
                    session.save(it)
                    signedIn = it
                },
            )
        } else {
            ChatScreen(
                signedIn = current,
                store = store,
                calendar = calendar,
                speechAudio = speechAudio,
                microphone = microphone,
                onSignedOut = {
                    session.clear()
                    signedIn = null
                },
            )
        }
    }
}
