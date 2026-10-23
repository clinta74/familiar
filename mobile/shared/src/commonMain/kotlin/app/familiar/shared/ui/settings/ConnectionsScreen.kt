package app.familiar.shared.ui.settings

import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.Checkbox
import androidx.compose.material3.MaterialTheme
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
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import app.familiar.shared.platform.formatInstant
import app.familiar.shared.protocol.HomeAssistantRequest
import app.familiar.shared.protocol.HomeAssistantView
import app.familiar.shared.protocol.HookTokenView
import app.familiar.shared.protocol.McpServerRequest
import app.familiar.shared.protocol.McpServerView
import app.familiar.shared.protocol.NewHookTokenView
import app.familiar.shared.protocol.WebhookRequest
import app.familiar.shared.protocol.WebhookView
import app.familiar.shared.session.FamiliarSession
import kotlinx.coroutines.launch

// Hooks (PLAN.md §6.5): Home Assistant's devices for the familiar to work (BACKLOG item 29), other
// systems making the familiar speak (hook tokens), the familiar telling other systems what happened
// (webhooks), and other servers' tools for it to use (MCP).

private val webhookEvents = listOf(
    "timer.done" to "A timer finishes",
    "reminder.due" to "A reminder is due",
    "reply" to "It replies",
    "tool.used" to "It uses a tool",
)

@Composable
fun ConnectionsScreen(session: FamiliarSession, openServer: (String) -> Unit, onBack: () -> Unit) {
    ScreenFrame(Route.Connections.title, onBack) {
        Scrolling {
            Card("Home Assistant") { HomeAssistant(session) }
            Card("Let other things make it speak") { HookTokens(session) }
            Card("Tell other things what happened") { Webhooks(session) }
            Card("Tools from MCP servers") { McpServers(session, openServer) }
        }
    }
}

@Composable
private fun HookTokens(session: FamiliarSession) {
    val scope = rememberCoroutineScope()
    var tokens by remember { mutableStateOf<List<HookTokenView>>(emptyList()) }
    var name by remember { mutableStateOf("Home Assistant") }
    var created by remember { mutableStateOf<NewHookTokenView?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var reload by remember { mutableIntStateOf(0) }
    var busy by remember { mutableStateOf(false) }

    LaunchedEffect(reload) { runCatching { session.api.hookTokens() }.onSuccess { tokens = it } }

    Muted("A hook token lets something else - Home Assistant, a script - have your familiar announce a message. It can't do anything else.")
    tokens.forEach { token ->
        ItemRow(token.name, token.lastUsedAt?.let { "last used ${formatInstant(it)}" } ?: "not used yet") {
            TextButton(onClick = {
                scope.launch {
                    runCatching { session.api.revokeHookToken(token.id) }.onFailure { error = it.shown("Couldn't revoke that token.") }
                    reload++
                }
            }) { Text("Revoke") }
        }
    }
    created?.let { token ->
        RevealSecret("Token for ${token.name}", token.token) {
            Muted("In Home Assistant's configuration.yaml:")
            SelectionContainer {
                Text(
                    """
                    rest_command:
                      familiar_say:
                        url: "${session.signedIn.server}/api/hooks/announce"
                        method: post
                        headers:
                          authorization: "Bearer ${token.token}"
                        content_type: "application/json"
                        payload: '{"message": "{{ message }}"}'
                    """.trimIndent(),
                    fontFamily = FontFamily.Monospace,
                    style = MaterialTheme.typography.bodySmall,
                )
            }
            Muted("Then call rest_command.familiar_say with a message from any automation. Add \"style\": \"tell\" to have it put the message in its own words.")
        }
    }
    OutlinedTextField(name, { name = it.take(64) }, label = { Text("What will use it") }, singleLine = true, modifier = Modifier.fillMaxWidth())
    BusyButton("Create token", busy, enabled = name.isNotBlank()) {
        error = null
        busy = true
        scope.launch {
            runCatching { session.api.createHookToken(name.trim()) }
                .onSuccess { created = it; reload++ }
                .onFailure { error = it.shown("Couldn't create a token.") }
            busy = false
        }
    }
    error?.let { ErrorText(it) }
}

@Composable
private fun Webhooks(session: FamiliarSession) {
    val scope = rememberCoroutineScope()
    var hooks by remember { mutableStateOf<List<WebhookView>>(emptyList()) }
    var name by remember { mutableStateOf("") }
    var url by remember { mutableStateOf("") }
    var events by remember { mutableStateOf(listOf("timer.done", "reminder.due")) }
    var secret by remember { mutableStateOf<String?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    var reload by remember { mutableIntStateOf(0) }

    LaunchedEffect(reload) { runCatching { session.api.webhooks() }.onSuccess { hooks = it } }

    Muted("A webhook gets a signed POST when something happens - to start a Home Assistant automation, say. Check the X-Familiar-Signature header (HMAC-SHA256 of the body) with the webhook's secret.")
    hooks.forEach { hook ->
        ItemRow(hook.name, listOfNotNull(hook.urlHost, hook.events.joinToString(", "), hook.lastResult?.let { "last: $it" }).joinToString(" · ")) {
            TextButton(enabled = !busy, onClick = {
                busy = true
                scope.launch {
                    runCatching { session.api.testWebhook(hook.id) }
                    busy = false
                    reload++
                }
            }) { Text("Test") }
            TextButton(onClick = {
                scope.launch {
                    runCatching { session.api.deleteWebhook(hook.id) }.onFailure { error = it.shown("Couldn't remove that webhook.") }
                    reload++
                }
            }) { Text("Remove") }
        }
    }
    secret?.let { RevealSecret("Signing secret", it) }
    OutlinedTextField(name, { name = it.take(64) }, label = { Text("Name") }, placeholder = { Text("Home Assistant") }, singleLine = true, modifier = Modifier.fillMaxWidth())
    OutlinedTextField(
        url,
        { url = it },
        label = { Text("Address") },
        placeholder = { Text("http://homeassistant.local:8123/api/webhook/…") },
        singleLine = true,
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
        modifier = Modifier.fillMaxWidth(),
    )
    Text("Send when", style = MaterialTheme.typography.bodyMedium)
    webhookEvents.forEach { (id, label) ->
        Row(verticalAlignment = Alignment.CenterVertically) {
            Checkbox(checked = id in events, onCheckedChange = { on -> events = if (on) events + id else events - id })
            Text(label)
        }
    }
    BusyButton("Add webhook", busy, enabled = name.isNotBlank() && url.isNotBlank() && events.isNotEmpty()) {
        error = null
        busy = true
        scope.launch {
            runCatching { session.api.createWebhook(WebhookRequest(name.trim(), url.trim(), events)) }
                .onSuccess {
                    secret = it.secret
                    name = ""
                    url = ""
                    reload++
                }
                .onFailure { error = it.shown("Couldn't add that webhook.") }
            busy = false
        }
    }
    error?.let { ErrorText(it) }
}

@Composable
private fun HomeAssistant(session: FamiliarSession) {
    val scope = rememberCoroutineScope()
    var home by remember { mutableStateOf<HomeAssistantView?>(null) }
    var url by remember { mutableStateOf("") }
    var token by remember { mutableStateOf("") }
    var editing by remember { mutableStateOf(false) }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }

    LaunchedEffect(Unit) { runCatching { session.api.homeAssistant() }.onSuccess { home = it } }

    fun act(block: suspend () -> HomeAssistantView?) {
        busy = true
        error = null
        scope.launch {
            runCatching { block() }
                .onSuccess { home = it; editing = false; token = "" }
                .onFailure { error = it.shown() }
            busy = false
        }
    }

    Muted("Your familiar can work the devices you've exposed to Assist in Home Assistant - \"turn off the kitchen light\", \"is the garage door open?\". Change which in Home Assistant (Settings, Voice assistants, Expose), then refresh here. Unlocking doors and opening the garage ask you first.")
    val connected = home?.connected == true
    home?.takeIf { connected }?.let { linked ->
        Divider()
        ItemRow(
            linked.urlHost ?: "Home Assistant",
            linked.lastError ?: "${linked.devices.size} devices",
            detailIsError = linked.lastError != null,
        ) {
            TextButton(enabled = !busy, onClick = { act { session.api.refreshHomeAssistant() } }) { Text("Refresh") }
            TextButton(enabled = !busy, onClick = { editing = !editing }) { Text("Change") }
            TextButton(enabled = !busy, onClick = { act { session.api.disconnectHomeAssistant(); null } }) { Text("Disconnect") }
        }
        linked.devices.groupBy { it.area ?: "No room" }.forEach { (room, devices) ->
            Muted("$room: ${devices.joinToString(", ") { it.name }}")
        }
    }
    if (!connected || editing) {
        Divider()
        OutlinedTextField(
            url,
            { url = it },
            label = { Text("Address") },
            placeholder = { Text("http://homeassistant.local:8123") },
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
            modifier = Modifier.fillMaxWidth(),
        )
        OutlinedTextField(
            token,
            { token = it },
            label = { Text(if (connected) "Long-lived access token (empty keeps the current one)" else "Long-lived access token (profile, Security)") },
            singleLine = true,
            visualTransformation = PasswordVisualTransformation(),
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
            modifier = Modifier.fillMaxWidth(),
        )
        BusyButton(if (busy) "Connecting…" else "Connect", busy, enabled = url.isNotBlank() && (connected || token.isNotBlank())) {
            act { session.api.connectHomeAssistant(HomeAssistantRequest(url.trim(), token.trim().ifBlank { null })) }
        }
    }
    error?.let { ErrorText(it) }
}

@Composable
private fun McpServers(session: FamiliarSession, openServer: (String) -> Unit) {
    val scope = rememberCoroutineScope()
    val browser = LocalUriHandler.current
    var servers by remember { mutableStateOf<List<McpServerView>>(emptyList()) }
    var name by remember { mutableStateOf("") }
    var url by remember { mutableStateOf("") }
    var token by remember { mutableStateOf("") }
    var ownApp by remember { mutableStateOf(false) }
    var clientId by remember { mutableStateOf("") }
    var clientSecret by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }

    LaunchedEffect(Unit) { runCatching { session.api.mcpServers() }.onSuccess { servers = it }.onFailure { error = it.shown() } }

    Muted(
        "Connect an MCP server (streamable HTTP) and your familiar can use its tools. Anything that isn't marked read-only " +
            "asks you first. Servers you sign in to - Notion, Linear, Atlassian, Asana - open their sign-in page in the browser.",
    )
    servers.forEach { server ->
        Divider()
        ItemRow(server.name, mcpStatus(server), detailIsError = server.lastError != null, onClick = { openServer(server.id) }) {
            Text("›", style = MaterialTheme.typography.titleLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
    Divider()
    OutlinedTextField(
        name,
        { name = it.lowercase().filter { c -> c.isLetterOrDigit() }.take(16) },
        label = { Text("Name (lower-case; it prefixes the tool names)") },
        placeholder = { Text("notion") },
        singleLine = true,
        modifier = Modifier.fillMaxWidth(),
    )
    OutlinedTextField(
        url,
        { url = it },
        label = { Text("Address") },
        placeholder = { Text("https://mcp.notion.com/mcp") },
        singleLine = true,
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
        modifier = Modifier.fillMaxWidth(),
    )
    OutlinedTextField(
        token,
        { token = it },
        label = { Text("Access token (optional; leave empty for servers you sign in to)") },
        singleLine = true,
        visualTransformation = PasswordVisualTransformation(),
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
        modifier = Modifier.fillMaxWidth(),
    )
    if (ownApp) {
        Muted(
            "Make an OAuth app in the service's developer settings, with " +
                "${session.signedIn.server.trimEnd('/')}/api/mcp/oauth/callback as its callback address, and give its client ID here.",
        )
        OutlinedTextField(clientId, { clientId = it }, label = { Text("Client ID") }, singleLine = true, modifier = Modifier.fillMaxWidth())
        OutlinedTextField(
            clientSecret,
            { clientSecret = it },
            label = { Text("Client secret (if it has one)") },
            singleLine = true,
            visualTransformation = PasswordVisualTransformation(),
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
            modifier = Modifier.fillMaxWidth(),
        )
    } else {
        TextButton(onClick = { ownApp = true }) { Text("Its sign-in needs an app you register yourself (GitHub, say)") }
    }
    BusyButton(if (busy) "Connecting…" else "Connect", busy, enabled = name.isNotEmpty() && name.first().isLetter() && url.isNotBlank()) {
        error = null
        busy = true
        scope.launch {
            runCatching {
                val added = session.api.addMcpServer(
                    McpServerRequest(name, url.trim(), token.ifBlank { null }, clientId.trim().ifBlank { null }, clientSecret.trim().ifBlank { null }),
                )
                name = ""
                url = ""
                token = ""
                clientId = ""
                clientSecret = ""
                ownApp = false
                // It wants a sign-in: straight to the browser, and to its page for when the user's back.
                if (added.signInNeeded) browser.openUri(session.api.signInMcpServer(added.id).authorizationUrl)
                openServer(added.id)
            }.onFailure {
                error = it.shown("Couldn't add that server.")
                runCatching { session.api.mcpServers() }.onSuccess { servers = it }
            }
            busy = false
        }
    }
    error?.let { ErrorText(it) }
}
