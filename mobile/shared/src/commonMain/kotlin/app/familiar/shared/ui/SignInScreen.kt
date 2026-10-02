package app.familiar.shared.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import app.familiar.shared.api.FamiliarApi
import app.familiar.shared.auth.SignedIn
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

/**
 * Which server, and who you are there. The phone gets its own long-lived token (PLAN.md §2): it
 * shows up in the web app's Settings › Devices, where it can be signed out.
 */
@Composable
fun SignInScreen(initialServer: String, deviceName: String, onSignedIn: (SignedIn) -> Unit) {
    var server by remember { mutableStateOf(initialServer) }
    var username by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    val url = FamiliarApi.normaliseServerUrl(server)

    fun signIn() {
        if (url == null || username.isBlank() || password.isEmpty() || busy) return
        busy = true
        error = null
        scope.launch {
            try {
                val response = FamiliarApi(url).deviceLogin(username.trim(), password, deviceName)
                onSignedIn(SignedIn(url, response.token, response.device.id, response.account.username))
            } catch (exception: Exception) {
                if (exception is CancellationException) throw exception
                error = exception.message ?: "Couldn't sign in."
            } finally {
                busy = false
            }
        }
    }

    Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        Box(Modifier.fillMaxSize().safeDrawingPadding().imePadding(), contentAlignment = Alignment.Center) {
            Column(
                Modifier.widthIn(max = 420.dp).fillMaxWidth().verticalScroll(rememberScrollState()).padding(24.dp),
                verticalArrangement = Arrangement.spacedBy(14.dp),
            ) {
                Text("Familiar", style = MaterialTheme.typography.headlineLarge, fontWeight = FontWeight.Bold)
                Text(
                    "Sign in to your Familiar server.",
                    style = MaterialTheme.typography.bodyLarge,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )

                OutlinedTextField(
                    value = server,
                    onValueChange = { server = it },
                    label = { Text("Server") },
                    placeholder = { Text("https://familiar.home:7280") },
                    singleLine = true,
                    isError = server.isNotBlank() && url == null,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri, imeAction = ImeAction.Next),
                    supportingText = {
                        when {
                            server.isNotBlank() && url == null -> Text("That isn't a web address.")
                            url?.startsWith("http://") == true -> Text("Not encrypted - fine on your own network or VPN only.")
                            else -> Text("The address you open Familiar at in a browser.")
                        }
                    },
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    value = username,
                    onValueChange = { username = it },
                    label = { Text("Username") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(imeAction = ImeAction.Next),
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    value = password,
                    onValueChange = { password = it },
                    label = { Text("Password") },
                    singleLine = true,
                    visualTransformation = PasswordVisualTransformation(),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password, imeAction = ImeAction.Go),
                    keyboardActions = KeyboardActions(onGo = { signIn() }),
                    modifier = Modifier.fillMaxWidth(),
                )

                error?.let { Text(it, color = MaterialTheme.colorScheme.error) }

                Button(
                    onClick = { signIn() },
                    enabled = url != null && username.isNotBlank() && password.isNotEmpty() && !busy,
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    if (busy) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp) else Text("Sign in")
                }

                Text(
                    "This phone will appear as \"$deviceName\" in Settings › Devices.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}
