package app.familiar.shared.ui

import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import app.familiar.shared.auth.Session
import app.familiar.shared.platform.DeviceAlarms
import app.familiar.shared.platform.DeviceCalendar
import app.familiar.shared.platform.FilePicker
import app.familiar.shared.platform.SecureStore
import app.familiar.shared.session.FamiliarSession
import app.familiar.shared.session.FamiliarSessions
import app.familiar.shared.ui.settings.AccountScreen
import app.familiar.shared.ui.settings.CalendarsScreen
import app.familiar.shared.ui.settings.ConnectionsScreen
import app.familiar.shared.ui.settings.DevicesScreen
import app.familiar.shared.ui.settings.FamiliarSettingsScreen
import app.familiar.shared.ui.settings.MemoriesScreen
import app.familiar.shared.ui.settings.Route
import app.familiar.shared.ui.settings.SettingsScreen
import app.familiar.shared.ui.settings.StudioScreen
import app.familiar.shared.voice.HandsFreeAudio
import app.familiar.shared.voice.Microphone
import app.familiar.shared.voice.SpeechAudio

/**
 * The whole app: sign in to a Familiar server, then talk. The same Compose code will run on iOS
 * (docs/MOBILE.md §1); only the store, the device name, the phone's calendar and its speaker come
 * from the platform, and so do its microphone and hands-free listening. The signed-in session is the
 * app's ([FamiliarSessions]), not this screen's, so it survives the activity being recreated.
 */
/** A menu item the platform provides. */
class PlatformAction(val label: String, val run: () -> Unit)

@Composable
fun FamiliarApp(
    store: SecureStore,
    deviceName: String,
    calendar: DeviceCalendar? = null,
    speechAudio: SpeechAudio? = null,
    microphone: Microphone? = null,
    handsFree: HandsFreeAudio? = null,
    alarms: DeviceAlarms? = null,
    /** The system's file chooser, for uploading a VRM model in the studio. */
    files: FilePicker? = null,
    /** Signed in (or still signed in, on opening): the platform registers for push. */
    onSession: () -> Unit = {},
    /** Extra items for the chat's menu that only the platform can do ("Make Familiar your assistant"). */
    actions: List<PlatformAction> = emptyList(),
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

            // Where the user is: the chat, Settings and its screens, the studio. A path of routes
            // ("Chat/Settings/Memories"), kept across the activity being recreated (unfolding).
            var path by rememberSaveable(current.username) { mutableStateOf(Route.Chat.name) }
            val open = { route: Route -> path = "$path/${route.name}" }
            val back = { path = path.substringBeforeLast('/', Route.Chat.name) }
            when (Route.valueOf(path.substringAfterLast('/'))) {
                Route.Chat -> ChatScreen(session, actions, open)
                Route.Settings -> SettingsScreen(session, open, back)
                Route.Familiar -> FamiliarSettingsScreen(session, back)
                Route.Studio -> StudioScreen(session, files, back)
                Route.Memories -> MemoriesScreen(session, back)
                Route.Calendars -> CalendarsScreen(session, back)
                Route.Connections -> ConnectionsScreen(session, back)
                Route.Account -> AccountScreen(session, back)
                Route.Devices -> DevicesScreen(session, back)
            }
        }
    }
}
