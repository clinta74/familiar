package app.familiar.shared.ui.settings

import androidx.compose.runtime.Composable
import app.familiar.shared.session.FamiliarSession

/** The screens besides the chat, and what each is called. */
enum class Route(val title: String, val about: String) {
    Chat("Chat", ""),
    Settings("Settings", ""),
    Familiar("Your familiar", "Its name, personality and voice"),
    Studio("Studio", "How it looks"),
    Memories("What it remembers", "Things it has picked up about you"),
    Calendars("Calendars", "The calendars it can see"),
    Connections("Connections", "Home Assistant, webhooks and MCP tools"),
    Account("Account", "Your details and password"),
    Devices("Devices", "The phones signed in to your account"),
}

/** Everything a user can change, as on the web's Settings page (user admin stays on the web). */
@Composable
fun SettingsScreen(session: FamiliarSession, open: (Route) -> Unit, onBack: () -> Unit) {
    ScreenFrame("Settings", onBack) {
        Scrolling {
            Card {
                val routes = listOf(Route.Familiar, Route.Studio, Route.Memories, Route.Calendars, Route.Connections, Route.Account, Route.Devices)
                routes.forEachIndexed { index, route ->
                    if (index > 0) Divider()
                    ItemRow(route.title, route.about, onClick = { open(route) })
                }
            }
            Muted("Signed in to ${session.signedIn.server} as ${session.signedIn.username}. Users are managed on the web.")
        }
    }
}
