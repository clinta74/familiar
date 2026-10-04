package app.familiar.shared.ui.settings

import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import app.familiar.shared.protocol.AccountResponse
import app.familiar.shared.protocol.AccountRole
import app.familiar.shared.session.FamiliarSession
import kotlinx.coroutines.launch

/** Who you're signed in as, and the password - changed here signs out every other browser and phone. */
@Composable
fun AccountScreen(session: FamiliarSession, onBack: () -> Unit) {
    val scope = rememberCoroutineScope()
    var account by remember { mutableStateOf<AccountResponse?>(null) }
    var current by remember { mutableStateOf("") }
    var next by remember { mutableStateOf("") }
    var status by remember { mutableStateOf<Status?>(null) }
    var busy by remember { mutableStateOf(false) }

    LaunchedEffect(Unit) { account = runCatching { session.api.account() }.getOrNull() }

    ScreenFrame(Route.Account.title, onBack) {
        Scrolling {
            Card("Account") {
                val shown = account
                if (shown == null) {
                    ItemRow("Username", session.signedIn.username)
                } else {
                    ItemRow("Username", shown.username)
                    ItemRow("Email", shown.email)
                    ItemRow("Role", if (shown.role == AccountRole.ADMIN) "Administrator" else "Member")
                }
            }

            Card("Password") {
                OutlinedTextField(
                    current,
                    { current = it },
                    label = { Text("Current password") },
                    singleLine = true,
                    visualTransformation = PasswordVisualTransformation(),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    next,
                    { next = it.take(256) },
                    label = { Text("New password") },
                    supportingText = { Text("At least 8 characters.") },
                    singleLine = true,
                    visualTransformation = PasswordVisualTransformation(),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                    modifier = Modifier.fillMaxWidth(),
                )
                StatusLine(status)
                BusyButton("Change password", busy, enabled = current.isNotEmpty() && next.length >= 8) {
                    busy = true
                    status = null
                    scope.launch {
                        runCatching { session.api.changePassword(current, next) }
                            .onSuccess {
                                current = ""
                                next = ""
                                status = Status(true, "Password changed. Your other sessions and devices have been signed out; this phone stays signed in.")
                            }
                            .onFailure { status = Status(false, it.shown("Couldn't change the password.")) }
                        busy = false
                    }
                }
            }
        }
    }
}
