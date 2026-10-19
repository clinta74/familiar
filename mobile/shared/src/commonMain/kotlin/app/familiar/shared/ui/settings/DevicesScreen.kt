package app.familiar.shared.ui.settings

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Switch
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
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import app.familiar.shared.platform.BackgroundConnection
import app.familiar.shared.platform.formatInstant
import app.familiar.shared.protocol.DeviceSummary
import app.familiar.shared.session.FamiliarSession
import kotlinx.coroutines.launch

/**
 * The phones signed in to the account; any can be signed out from here, this one included. And
 * whether this one stays connected while the app's closed.
 */
@Composable
fun DevicesScreen(session: FamiliarSession, background: BackgroundConnection?, onBack: () -> Unit) {
    val scope = rememberCoroutineScope()
    var devices by remember { mutableStateOf<List<DeviceSummary>?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var reload by remember { mutableIntStateOf(0) }
    var signingOutThis by remember { mutableStateOf(false) }

    LaunchedEffect(reload) {
        runCatching { session.api.devices() }.onSuccess { devices = it }.onFailure { error = it.shown("Couldn't load devices.") }
    }

    ScreenFrame(Route.Devices.title, onBack) {
        Scrolling {
            val list = devices
            if (list == null) {
                error?.let { ErrorText(it) } ?: Loading()
                return@Scrolling
            }
            Card {
                list.forEachIndexed { index, device ->
                    if (index > 0) Divider()
                    val used = device.lastUsedAt?.let { " · last used ${formatInstant(it)}" }.orEmpty()
                    ItemRow(device.name + if (device.isCurrent) " (this phone)" else "", "Signed in ${formatInstant(device.createdAt)}$used") {
                        TextButton(onClick = {
                            if (device.isCurrent) {
                                signingOutThis = true
                            } else {
                                scope.launch {
                                    runCatching { session.api.revokeDevice(device.id) }.onFailure { error = it.shown() }
                                    reload++
                                }
                            }
                        }) { Text("Sign out") }
                    }
                }
            }
            Muted("Browsers signed in to the web app aren't listed; changing your password signs them out.")
            val phone = session.phone
            if (phone?.canSendTexts == true) {
                var sends by remember { mutableStateOf(phone.sendsTexts()) }
                Card {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Column(Modifier.weight(1f)) {
                            Text("Send texts without Messages", style = MaterialTheme.typography.bodyLarge)
                            Muted(
                                "For hands-free: ${session.controller.state.value.familiarName} reads the text back - who it's to and " +
                                    "what it says - and sends it when you say yes. Off, Messages opens with it written for you to tap Send. " +
                                    "Never after it's looked something up or seen a picture: then it's always Messages.",
                            )
                        }
                        Switch(checked = sends, onCheckedChange = { on -> scope.launch { sends = session.sendTexts(on) } })
                    }
                }
            }
            if (background != null) {
                var on by remember { mutableStateOf(background.isOn()) }
                Card {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Column(Modifier.weight(1f)) {
                            Text("Stay connected when closed", style = MaterialTheme.typography.bodyLarge)
                            Muted(
                                "So a timer set on another device rings here, and announcements show, with the app closed. " +
                                    "Keeps one small connection to your server, with a quiet notification while it does. " +
                                    "Timers set on this phone ring either way.",
                            )
                        }
                        Switch(checked = on, onCheckedChange = {
                            background.set(it)
                            on = it
                        })
                    }
                }
            }
            error?.let { ErrorText(it) }
        }
    }

    if (signingOutThis) {
        Confirm(
            title = "Sign out of this phone?",
            text = "You'll need your password to sign in again.",
            confirm = "Sign out",
            onConfirm = { session.signOut() },
            onDismiss = { signingOutThis = false },
        )
    }
}
