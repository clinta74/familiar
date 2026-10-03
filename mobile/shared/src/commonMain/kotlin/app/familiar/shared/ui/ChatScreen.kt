package app.familiar.shared.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.height
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
import app.familiar.shared.avatar.AvatarController
import app.familiar.shared.avatar.AvatarView
import app.familiar.shared.auth.SignedIn
import app.familiar.shared.conversation.ChatMessage
import app.familiar.shared.conversation.Connection
import app.familiar.shared.conversation.ConversationController
import app.familiar.shared.conversation.ToolChip
import app.familiar.shared.platform.DeviceCalendar
import app.familiar.shared.protocol.ServerEvent
import kotlinx.serialization.json.JsonObject
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

/** States the avatar page knows; anything newer from the server reads as busy. */
private val avatarStates = setOf("idle", "listening", "thinking", "speaking", "resting")

/**
 * The familiar and the conversation: the avatar above the chat on a phone, beside it on a wide
 * screen (an unfolded Fold, a tablet). Typed for now; voice is the rest of phase M1
 * (docs/MOBILE.md §7).
 */
@Composable
fun ChatScreen(signedIn: SignedIn, calendar: DeviceCalendar?, onSignedOut: () -> Unit) {
    val scope = rememberCoroutineScope()
    val api = remember(signedIn) { FamiliarApi(signedIn.server, signedIn.token) }
    val controller = remember(signedIn) { ConversationController(api, scope, onSignedOut, calendar) }
    val state by controller.state.collectAsState()
    val calendarOn by controller.calendarOn.collectAsState()
    val avatar = remember(signedIn) { AvatarController() }

    // How it looks is the studio's business (on the web); here it's read, and again on each
    // reconnect so a change made there shows up.
    suspend fun refreshLook() {
        runCatching { (api.familiar()["appearance"] as? JsonObject)?.let(avatar::look) }
    }
    LaunchedEffect(controller) {
        controller.onEvent = { event ->
            avatar.handle(event)
            if (event is ServerEvent.Ready) scope.launch { refreshLook() }
        }
        refreshLook()
    }

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

    // Typing reads as the familiar listening.
    val avatarState = when {
        state.state == "idle" && draft.isNotBlank() -> "listening"
        state.state in avatarStates -> state.state
        else -> "thinking"
    }
    LaunchedEffect(avatarState) { avatar.state(avatarState) }

    fun send() {
        val text = draft.trim()
        if (text.isEmpty()) return
        controller.send(text)
        draft = ""
    }

    // The messages and the box to type in, wherever the layout puts them.
    val conversation: @Composable ColumnScope.() -> Unit = {
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
    val loadModel: suspend (String) -> ByteArray? = { id -> runCatching { api.avatarModel(id) }.getOrNull() }

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
                        if (calendar != null) {
                            DropdownMenuItem(
                                text = { Text(if (calendarOn) "Refresh phone calendar" else "Use this phone's calendar") },
                                onClick = {
                                    menu = false
                                    if (calendarOn) controller.syncCalendar() else controller.useCalendar()
                                },
                            )
                        }
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

            BoxWithConstraints(Modifier.weight(1f).fillMaxWidth()) {
                if (maxWidth >= 600.dp) {
                    Row(Modifier.fillMaxSize()) {
                        AvatarView(avatar, loadModel, Modifier.weight(1f).fillMaxHeight())
                        Column(Modifier.weight(1f).fillMaxHeight(), content = conversation)
                    }
                } else {
                    val stage = maxHeight * 0.4f
                    Column(Modifier.fillMaxSize()) {
                        AvatarView(avatar, loadModel, Modifier.fillMaxWidth().height(stage))
                        conversation()
                    }
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
