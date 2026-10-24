package app.familiar.shared.ui.settings

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.MaterialTheme
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
import androidx.compose.ui.platform.LocalWindowInfo
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.familiar.shared.protocol.McpAuth
import app.familiar.shared.protocol.McpServerView
import app.familiar.shared.session.FamiliarSession
import kotlinx.coroutines.launch

/** The line under an MCP server's name: what's wrong with it, or whether it's signed in and how many of its tools are on. */
internal fun mcpStatus(server: McpServerView, withHost: Boolean = true): String =
    listOfNotNull(
        server.urlHost.takeIf { withHost },
        server.lastError
            ?: listOfNotNull(
                "signed in".takeIf { server.auth == McpAuth.SIGN_IN },
                server.refreshedAt?.let { "${server.tools.count { it.enabled }} of ${server.tools.size} tools on" },
            ).joinToString(" · ").ifEmpty { null },
    ).joinToString(" · ")

/**
 * One MCP server (from Connections): signing in to it, refreshing or removing it, and which of its
 * tools the familiar may use. A page rather than a fold-out, because a hosted server can offer fifty
 * tools with a paragraph each.
 */
@Composable
fun McpServerScreen(session: FamiliarSession, id: String, onBack: () -> Unit) {
    val scope = rememberCoroutineScope()
    val browser = LocalUriHandler.current
    var server by remember { mutableStateOf<McpServerView?>(null) }
    var gone by remember { mutableStateOf(false) }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var confirmRemove by remember { mutableStateOf(false) }
    var reload by remember { mutableIntStateOf(0) }

    // Signing in happens in the browser, and the app is back in front when it's done: the window
    // having focus again is the moment to look again (there's no lifecycle library in shared code).
    val focused = LocalWindowInfo.current.isWindowFocused
    LaunchedEffect(reload, focused) {
        if (!focused) return@LaunchedEffect
        runCatching { session.api.mcpServers() }
            .onSuccess { all ->
                server = all.firstOrNull { it.id == id }
                gone = server == null
            }
            .onFailure { error = it.shown("Couldn't load it.") }
    }

    fun act(failed: String, block: suspend () -> Unit) {
        busy = true
        error = null
        scope.launch {
            runCatching { block() }.onFailure { error = it.shown(failed) }
            busy = false
            reload++
        }
    }

    ScreenFrame(server?.name ?: Route.McpServer.title, onBack) {
        Scrolling {
            val current = server
            when {
                gone -> Card { Muted("This server isn't connected any more.") }
                current == null -> error?.let { ErrorText(it) } ?: Loading()
                else -> {
                    Card {
                        ItemRow(current.urlHost, mcpStatus(current, withHost = false), detailIsError = current.lastError != null)
                        Row(horizontalArrangement = Arrangement.spacedBy(4.dp), verticalAlignment = Alignment.CenterVertically) {
                            if (current.auth == McpAuth.SIGN_IN) {
                                val signIn = {
                                    act("Couldn't start signing in to ${current.name}.") {
                                        browser.openUri(session.api.signInMcpServer(current.id).authorizationUrl)
                                    }
                                }
                                if (current.signInNeeded) {
                                    Button(onClick = signIn, enabled = !busy) { Text("Sign in") }
                                } else {
                                    TextButton(onClick = signIn, enabled = !busy) { Text("Sign in again") }
                                }
                            }
                            TextButton(enabled = !busy, onClick = { act("Couldn't refresh ${current.name}.") { session.api.refreshMcpServer(current.id) } }) {
                                Text("Refresh")
                            }
                            TextButton(enabled = !busy, onClick = { confirmRemove = true }) {
                                Text("Remove", color = MaterialTheme.colorScheme.error)
                            }
                        }
                        if (current.auth == McpAuth.SIGN_IN && current.signInNeeded) {
                            Muted("Signing in opens its page in the browser. Come back here when it's done.")
                        }
                        error?.let { ErrorText(it) }
                    }
                    if (current.tools.isNotEmpty()) {
                        Card("Tools") {
                            Muted(
                                "${current.tools.count { it.enabled }} of ${current.tools.size} on. Anything that isn't marked read-only " +
                                    "asks you first. Switch on as many as you like: each message is offered the eight, across " +
                                    "all your servers, that fit it best.",
                            )
                            current.tools.forEach { tool ->
                                Divider()
                                Row(verticalAlignment = Alignment.Top) {
                                    Checkbox(checked = tool.enabled, enabled = !busy, onCheckedChange = { on ->
                                        val enabled = current.tools.filter { if (it.name == tool.name) on else it.enabled }.map { it.name }
                                        scope.launch {
                                            runCatching { session.api.setMcpTools(current.id, enabled) }
                                                .onSuccess { server = it }
                                                .onFailure { error = it.shown("Couldn't change that.") }
                                        }
                                    })
                                    Column(Modifier.weight(1f).fillMaxWidth()) {
                                        Text(
                                            tool.name + if (tool.readOnly) "  · reads" else "  · asks first",
                                            fontFamily = FontFamily.Monospace,
                                            style = MaterialTheme.typography.bodySmall,
                                        )
                                        // Three lines of what can be a paragraph; tapped, the rest.
                                        tool.description?.let { description ->
                                            var open by remember(tool.name) { mutableStateOf(false) }
                                            Text(
                                                description,
                                                style = MaterialTheme.typography.bodySmall,
                                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                                maxLines = if (open) Int.MAX_VALUE else 3,
                                                overflow = TextOverflow.Ellipsis,
                                                modifier = Modifier.clickable { open = !open },
                                            )
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    val current = server
    if (confirmRemove && current != null) {
        Confirm(
            "Remove ${current.name}?",
            "Your familiar stops using its tools. You can connect it again later.",
            "Remove",
            onConfirm = {
                act("Couldn't remove ${current.name}.") {
                    session.api.deleteMcpServer(current.id)
                    onBack()
                }
            },
            onDismiss = { confirmRemove = false },
        )
    }
}
