package app.familiar.shared.ui.settings

import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.foundation.text.KeyboardOptions
import app.familiar.shared.platform.formatInstant
import app.familiar.shared.protocol.CalendarKind
import app.familiar.shared.protocol.CalendarView
import app.familiar.shared.protocol.EventView
import app.familiar.shared.session.FamiliarSession
import kotlinx.coroutines.launch

/**
 * The calendars the familiar can see (PLAN.md §6.4): its own, which it can write to; the phone's,
 * when the app is allowed to read it; and subscribed feeds - the "secret iCal address" Google,
 * Outlook and iCloud all offer - which it only reads.
 */
@Composable
fun CalendarsScreen(session: FamiliarSession, onBack: () -> Unit) {
    val scope = rememberCoroutineScope()
    var calendars by remember { mutableStateOf<List<CalendarView>?>(null) }
    var events by remember { mutableStateOf<List<EventView>>(emptyList()) }
    var name by remember { mutableStateOf("") }
    var url by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var reload by remember { mutableIntStateOf(0) }

    LaunchedEffect(reload) {
        runCatching { session.api.calendars() }.onSuccess { calendars = it }.onFailure { error = it.shown("Couldn't load calendars.") }
        runCatching { session.api.events() }.onSuccess { events = it }
    }

    fun act(fallback: String, block: suspend () -> Unit) {
        busy = true
        error = null
        scope.launch {
            runCatching { block() }.onFailure { error = it.shown(fallback) }
            busy = false
            reload++
        }
    }

    ScreenFrame(Route.Calendars.title, onBack) {
        Scrolling {
            val list = calendars
            if (list == null) {
                error?.let { ErrorText(it) } ?: Loading()
                return@Scrolling
            }

            Card {
                list.forEachIndexed { index, calendar ->
                    if (index > 0) Divider()
                    val synced = calendar.lastSyncedAt?.let { formatInstant(it) } ?: "never"
                    when (calendar.kind) {
                        CalendarKind.LOCAL -> ItemRow("Familiar", "Its own calendar: new events go here when your phone isn't connected.")
                        CalendarKind.DEVICE -> ItemRow(
                            "${calendar.name}'s calendars",
                            "Every calendar on the phone, from the Familiar app · updated $synced. New events go here while the app is open.",
                        )
                        else -> ItemRow(
                            calendar.name,
                            calendar.lastError ?: if (calendar.lastSyncedAt != null) "Read-only feed · updated $synced" else "Not fetched yet",
                            detailIsError = calendar.lastError != null,
                        ) {
                            TextButton(enabled = !busy, onClick = { act("Couldn't refresh it.") { session.api.syncCalendar(calendar.id) } }) { Text("Refresh") }
                            TextButton(enabled = !busy, onClick = { act("Couldn't remove it.") { session.api.removeCalendar(calendar.id) } }) { Text("Remove") }
                        }
                    }
                }
                if (session.calendar != null && list.none { it.kind == CalendarKind.DEVICE }) {
                    Muted("To share this phone's calendars, choose \"Use this phone's calendar\" in the chat's menu.")
                }
            }

            Card("Add a calendar") {
                Muted(
                    "By its secret iCal address: Google Calendar → Settings → your calendar → \"Secret address in iCal format\"; " +
                        "Outlook → Settings → Shared calendars → Publish; iCloud → Share → Public calendar.",
                )
                OutlinedTextField(name, { name = it.take(64) }, label = { Text("Name") }, placeholder = { Text("Work") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                OutlinedTextField(
                    url,
                    { url = it },
                    label = { Text("iCal address") },
                    placeholder = { Text("https://… or webcal://…") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                    modifier = Modifier.fillMaxWidth(),
                )
                BusyButton(if (busy) "Fetching…" else "Add calendar", busy, enabled = name.isNotBlank() && url.isNotBlank()) {
                    act("Couldn't add that calendar.") {
                        val added = session.api.addCalendar(name.trim(), url.trim())
                        added.lastError?.let { error = it }
                        name = ""
                        url = ""
                    }
                }
            }
            error?.let { ErrorText(it) }

            Card("Next 7 days") {
                if (events.isEmpty()) Muted("Nothing on the calendar.")
                val byId = list.associateBy { it.id }
                events.forEachIndexed { index, event ->
                    if (index > 0) Divider()
                    val calendar = byId[event.calendarId]
                    val whenText = if (event.allDay) "${formatInstant(event.startsAt, withTime = false, utc = true)}, all day" else formatInstant(event.startsAt)
                    val where = listOfNotNull(event.location, calendar?.takeIf { it.kind != CalendarKind.LOCAL }?.name)
                    ItemRow(event.title, (listOf(whenText) + where).joinToString(" · ")) {
                        if (calendar?.kind == CalendarKind.LOCAL) {
                            TextButton(onClick = { act("Couldn't delete it.") { session.api.deleteEvent(event.id) } }) { Text("Delete") }
                        }
                    }
                }
            }
            Row {
                OutlinedButton(enabled = !busy, onClick = { reload++ }) { Text("Reload") }
            }
        }
    }
}
