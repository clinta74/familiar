package app.familiar.shared.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Button
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import app.familiar.shared.api.FamiliarApi
import app.familiar.shared.auth.SignedIn
import app.familiar.shared.conversation.ChatMessage
import app.familiar.shared.conversation.Connection
import app.familiar.shared.conversation.ConversationController
import app.familiar.shared.conversation.ToolChip
import kotlinx.coroutines.launch

private val emotionLabels = mapOf(
    "joy" to "happy",
    "sad" to "sad",
    "angry" to "cross",
    "surprised" to "surprised",
    "relaxed" to "relaxed",
    "thinking" to "thoughtful",
)

private val toolDoing = mapOf(
    "set_timer" to "Setting a timer",
    "create_reminder" to "Making a reminder",
    "list_scheduled" to "Checking your timers",
    "cancel_scheduled" to "Cancelling",
    "list_events" to "Checking your calendar",
    "create_event" to "Adding to your calendar",
    "remember" to "Remembering",
    "forget" to "Forgetting",
)

/**
 * The conversation, typed for now: the avatar, voice and hands-free arrive in phases M1 and M2
 * (docs/MOBILE.md §7).
 */
@Composable
fun ChatScreen(signedIn: SignedIn, onSignedOut: () -> Unit) {
    val scope = rememberCoroutineScope()
    val api = remember(signedIn) { FamiliarApi(signedIn.server, signedIn.token) }
    val controller = remember(signedIn) { ConversationController(api, scope, onSignedOut) }
    val state by controller.state.collectAsState()

    DisposableEffect(controller) {
        controller.start()
        onDispose { controller.stop() }
    }

    var draft by remember { mutableStateOf("") }
    var menu by remember { mutableStateOf(false) }
    val list = rememberLazyListState()

    // Follow the conversation as it grows.
    val lastText = state.messages.lastOrNull()?.text
    LaunchedEffect(state.messages.size, lastText) {
        if (state.messages.isNotEmpty()) list.animateScrollToItem(state.messages.size - 1)
    }

    val busy = state.state == "thinking" || state.state == "speaking"
    val status = when {
        state.connection == Connection.Reconnecting -> "reconnecting…"
        state.connection == Connection.Connecting -> "connecting…"
        state.state == "thinking" -> "thinking…"
        state.state == "speaking" -> "talking"
        state.state == "resting" -> "resting - the model isn't reachable"
        else -> null
    }

    fun send() {
        val text = draft.trim()
        if (text.isEmpty()) return
        controller.send(text)
        draft = ""
    }

    Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        Column(Modifier.fillMaxSize().safeDrawingPadding().imePadding()) {
            Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(state.familiarName, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold)
                    status?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
                }
                Box {
                    TextButton(onClick = { menu = true }) { Text("⋮") }
                    DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                        DropdownMenuItem(text = { Text("New conversation") }, onClick = {
                            menu = false
                            controller.startOver()
                        })
                        DropdownMenuItem(text = { Text("Sign out of ${signedIn.username}") }, onClick = {
                            menu = false
                            scope.launch {
                                runCatching { if (signedIn.deviceId.isNotEmpty()) api.revokeDevice(signedIn.deviceId) }
                                onSignedOut()
                            }
                        })
                    }
                }
            }

            LazyColumn(
                state = list,
                modifier = Modifier.weight(1f).fillMaxWidth().padding(horizontal = 12.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                if (state.messages.isEmpty()) {
                    item {
                        Text(
                            "Say hello to ${state.familiarName}.",
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.fillMaxWidth().padding(32.dp),
                        )
                    }
                }
                items(state.messages, key = { it.id }) { Bubble(it) }
            }

            state.error?.let {
                Text(it, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp))
            }

            Row(Modifier.fillMaxWidth().padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
                OutlinedTextField(
                    value = draft,
                    onValueChange = { draft = it },
                    placeholder = { Text("Message ${state.familiarName}…") },
                    maxLines = 5,
                    keyboardOptions = KeyboardOptions(imeAction = ImeAction.Send),
                    keyboardActions = KeyboardActions(onSend = { send() }),
                    modifier = Modifier.weight(1f),
                )
                if (busy) {
                    OutlinedButton(onClick = { controller.interrupt() }, modifier = Modifier.padding(start = 8.dp)) { Text("Stop") }
                } else {
                    Button(onClick = { send() }, enabled = draft.isNotBlank(), modifier = Modifier.padding(start = 8.dp)) { Text("Send") }
                }
            }
        }
    }
}

@Composable
private fun Bubble(message: ChatMessage) {
    val shape = RoundedCornerShape(16.dp)
    val colors = MaterialTheme.colorScheme
    Box(Modifier.fillMaxWidth(), contentAlignment = if (message.fromUser) Alignment.CenterEnd else Alignment.CenterStart) {
        Column(
            Modifier
                .widthIn(max = 320.dp)
                .then(
                    if (message.fromUser) Modifier.background(colors.primary, shape)
                    else if (message.text.isEmpty()) Modifier
                    else Modifier.background(colors.surface, shape).border(1.dp, colors.outline, shape),
                )
                .padding(horizontal = 13.dp, vertical = 9.dp),
            verticalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            if (message.tools.isNotEmpty()) {
                Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) { message.tools.forEach { Chip(it) } }
            }
            if (message.text.isNotEmpty() || message.tools.isEmpty()) {
                Text(
                    message.text + (if (message.interrupted) "…" else "") + (if (message.streaming) " ▍" else ""),
                    color = if (message.fromUser) colors.onPrimary else colors.onSurface,
                )
            }
            val label = message.emotion?.let { emotionLabels[it] }
            if (!message.fromUser && label != null && !message.streaming) {
                Text(label, style = MaterialTheme.typography.labelSmall, color = colors.onSurfaceVariant)
            }
        }
    }
}

@Composable
private fun Chip(chip: ToolChip) {
    val colors = MaterialTheme.colorScheme
    val doing = toolDoing[chip.tool] ?: chip.tool.replace('_', ' ')
    val text = if (chip.running) "$doing…" else chip.summary ?: doing
    Text(
        text,
        style = MaterialTheme.typography.labelMedium,
        color = when (chip.ok) {
            true -> MaterialTheme.colorScheme.primary
            false -> MaterialTheme.colorScheme.error
            null -> colors.onSurfaceVariant
        },
        modifier = Modifier.border(1.dp, colors.outline, RoundedCornerShape(50)).padding(horizontal = 9.dp, vertical = 2.dp),
    )
}
