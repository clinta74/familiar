package app.familiar.shared.ui

import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import app.familiar.shared.auth.Session
import app.familiar.shared.platform.DeviceCalendar
import app.familiar.shared.platform.SecureStore

/**
 * The whole app: sign in to a Familiar server, then talk. The same Compose code will run on iOS
 * (docs/MOBILE.md §1); only the store, the device name and the phone's calendar come from the platform.
 */
@Composable
fun FamiliarApp(store: SecureStore, deviceName: String, calendar: DeviceCalendar? = null) {
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
                calendar = calendar,
                onSignedOut = {
                    session.clear()
                    signedIn = null
                },
            )
        }
    }
}
