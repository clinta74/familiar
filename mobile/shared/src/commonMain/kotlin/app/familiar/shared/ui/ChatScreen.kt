package app.familiar.shared.ui

import app.familiar.shared.platform.PicturePicker
import app.familiar.shared.platform.SharedPictures
import androidx.compose.foundation.layout.heightIn
import androidx.compose.ui.layout.ContentScale
import androidx.compose.foundation.Image
import app.familiar.shared.protocol.LinkView
import androidx.compose.foundation.clickable
import androidx.compose.ui.platform.LocalUriHandler
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.flow.distinctUntilChanged
import androidx.compose.runtime.snapshotFlow
import androidx.compose.foundation.gestures.scrollBy
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
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
import androidx.compose.material3.Switch
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.familiar.shared.avatar.AvatarView
import app.familiar.shared.conversation.ChatMessage
import app.familiar.shared.conversation.Connection
import app.familiar.shared.conversation.ToolChip
import app.familiar.shared.session.FamiliarSession
import app.familiar.shared.ui.settings.Route
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

/** Slimmer than a button's default, so the message box gets the room. */
private val compact = PaddingValues(horizontal = 14.dp, vertical = 8.dp)

/** States the avatar page knows; anything newer from the server reads as busy. */
private val avatarStates = setOf("idle", "listening", "thinking", "speaking", "resting")

/**
 * The familiar and the conversation: the avatar above the chat on a phone, beside it on a wide
 * screen (an unfolded Fold, a tablet). Everything here is the [session]'s, which outlives this
 * screen - a hands-free conversation carries on with the screen off.
 */
@Composable
fun ChatScreen(session: FamiliarSession, actions: List<PlatformAction> = emptyList(), open: (Route) -> Unit = {}, pictures: PicturePicker? = null) {
    val scope = rememberCoroutineScope()
    val controller = session.controller
    val avatar = session.avatar
    val state by controller.state.collectAsState()
    val calendarOn by controller.calendarOn.collectAsState()
    val voiceOn by session.voiceOn.collectAsState()
    val effectsOn by session.effectsOn.collectAsState()
    val audible by session.audible.collectAsState()
    val recording by session.recording.collectAsState()
    val micLevel by session.micLevel.collectAsState()
    val handsFreeOn by session.handsFreeOn.collectAsState()
    val userSpeaking by session.userSpeaking.collectAsState()

    DisposableEffect(session) {
        session.start()
        onDispose { }
    }

    var draft by remember { mutableStateOf("") }
    var menu by remember { mutableStateOf(false) }
    val list = rememberLazyListState()

    // Follow the conversation as it grows. A conversation that arrives all at once (restored on
    // opening, reloaded after a reconnect) jumps straight to its latest message.
    val lastText = state.messages.lastOrNull()?.text
    var shown by remember { mutableStateOf(0) }
    // To the end of the latest message, not its start: a long reply's last lines are what's new.
    LaunchedEffect(state.messages.size, lastText) {
        val count = state.messages.size
        if (count > 0) {
            if (count - shown > 2) list.scrollToItem(count - 1) else list.animateScrollToItem(count - 1)
            list.scrollBy(END)
        }
        shown = count
    }
    // The list shrinking (the keyboard coming up) keeps the end in view too.
    LaunchedEffect(list) {
        snapshotFlow { list.layoutInfo.viewportSize.height }.distinctUntilChanged().drop(1).collect {
            val count = list.layoutInfo.totalItemsCount
            if (count > 0) {
                list.scrollToItem(count - 1)
                list.scrollBy(END)
            }
        }
    }

    val busy = state.state == "thinking" || state.state == "speaking" || audible
    val status = when {
        recording -> "listening… let go to send"
        state.connection == Connection.Reconnecting -> "reconnecting…"
        state.connection == Connection.Connecting -> "connecting…"
        userSpeaking -> "listening…"
        state.state == "thinking" -> "thinking…"
        state.state == "speaking" || audible -> "talking"
        state.state == "resting" -> "resting - the model isn't reachable"
        handsFreeOn -> "hands-free - just talk"
        else -> null
    }

    // Typing reads as the familiar listening, and so does hearing the user hands-free.
    val avatarState = when {
        recording || userSpeaking -> "listening"
        audible -> "speaking"
        state.state == "idle" && draft.isNotBlank() -> "listening"
        state.state in avatarStates -> state.state
        else -> "thinking"
    }
    LaunchedEffect(avatarState) { avatar.state(avatarState) }

    // A picture to go with the next message: picked here, or shared to Familiar from another app.
    var picture by remember { mutableStateOf<ByteArray?>(null) }
    val shared by SharedPictures.pending.collectAsState()
    LaunchedEffect(shared) {
        shared?.let {
            picture = it
            SharedPictures.pending.value = null
        }
    }

    fun send() {
        val text = draft.trim()
        if (text.isEmpty() && picture == null) return
        controller.send(text, picture)
        draft = ""
        picture = null
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
            items(state.messages, key = { it.id }) { Bubble(it, picture = { id -> PictureFrom(session, pictures, id) }) }
        }

        state.error?.let {
            Text(it, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp))
        }

        picture?.let { chosen ->
            Row(Modifier.fillMaxWidth().padding(start = 16.dp, end = 12.dp, top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                pictures?.decode(chosen)?.let {
                    Image(it, contentDescription = "The picture you're about to send", contentScale = ContentScale.Crop,
                        modifier = Modifier.size(52.dp).clip(RoundedCornerShape(8.dp)))
                }
                Text(
                    "Goes with your message. Its location and camera details aren't sent.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.weight(1f).padding(horizontal = 10.dp),
                )
                TextButton(onClick = { picture = null }) { Text("Remove") }
            }
        }

        Row(Modifier.fillMaxWidth().padding(start = 12.dp, end = 12.dp, top = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            if (pictures != null) {
                TextButton(
                    onClick = {
                        scope.launch {
                            runCatching { pictures.pick() }
                                .onSuccess { picked -> picked?.let { picture = it } }
                                .onFailure { controller.showError(it.message ?: "Couldn't open that picture.") }
                        }
                    },
                    contentPadding = compact,
                    modifier = Modifier.semantics { contentDescription = "Add a picture" },
                ) { Text("🖼", style = MaterialTheme.typography.titleMedium) }
            }
            if (session.microphone != null) {
                // Hold to talk: recording while it's pressed, sent on release; a slide off cancels.
                // Hands-free has the microphone, so it's dimmed then and lights up as it hears you.
                val ring = MaterialTheme.colorScheme.primary
                val lit = recording || userSpeaking
                Box(
                    contentAlignment = Alignment.Center,
                    modifier = Modifier
                        .padding(end = 8.dp)
                        .size(44.dp)
                        .clip(CircleShape)
                        .background(
                            when {
                                recording -> ring.copy(alpha = 0.25f + 0.5f * micLevel.toFloat())
                                userSpeaking -> ring.copy(alpha = 0.5f)
                                else -> MaterialTheme.colorScheme.surfaceVariant
                            },
                        )
                        .border(2.dp, if (lit || handsFreeOn) ring else MaterialTheme.colorScheme.outline, CircleShape)
                        .semantics { contentDescription = if (handsFreeOn) "Hands-free is listening" else "Hold to talk to ${state.familiarName}" }
                        .pointerInput(session, handsFreeOn) {
                            if (handsFreeOn) return@pointerInput
                            detectTapGestures(onPress = {
                                if (session.startTalking()) {
                                    val released = tryAwaitRelease()
                                    session.stopTalking(send = released)
                                }
                            })
                        },
                ) {
                    Text(if (lit) "●" else "🎤", style = MaterialTheme.typography.titleSmall)
                }
            }
            OutlinedTextField(
                value = draft,
                onValueChange = { draft = it },
                // One line for the hint, however long the familiar's name: wrapped, it made the box
                // twice the height. What's typed still grows to five lines.
                placeholder = { Text("Message ${state.familiarName}…", maxLines = 1, overflow = TextOverflow.Ellipsis) },
                textStyle = MaterialTheme.typography.bodyMedium,
                maxLines = 5,
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Send),
                keyboardActions = KeyboardActions(onSend = { send() }),
                modifier = Modifier.weight(1f),
            )
            if (busy) {
                OutlinedButton(onClick = { session.stopReply() }, contentPadding = compact, modifier = Modifier.padding(start = 8.dp)) { Text("Stop") }
            } else {
                Button(onClick = { send() }, enabled = draft.isNotBlank() || picture != null, contentPadding = compact, modifier = Modifier.padding(start = 8.dp)) { Text("Send") }
            }
        }

        if (session.handsFree != null) {
            Row(
                Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    if (handsFreeOn) "Hands-free: just talk. It keeps listening with the screen off." else "Hands-free",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.weight(1f),
                )
                Switch(checked = handsFreeOn, onCheckedChange = { on -> scope.launch { session.setHandsFree(on) } })
            }
        }
    }
    val loadModel: suspend (String) -> ByteArray? = { id -> runCatching { session.api.avatarModel(id) }.getOrNull() }

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
                        DropdownMenuItem(text = { Text("Settings") }, onClick = {
                            menu = false
                            open(Route.Settings)
                        })
                        DropdownMenuItem(text = { Text("Studio") }, onClick = {
                            menu = false
                            open(Route.Studio)
                        })
                        DropdownMenuItem(text = { Text("New conversation") }, onClick = {
                            menu = false
                            controller.startOver()
                        })
                        if (session.player != null) {
                            DropdownMenuItem(text = { Text(if (voiceOn) "Turn voice off" else "Turn voice on") }, onClick = {
                                menu = false
                                session.setVoice(!voiceOn)
                            })
                        }
                        DropdownMenuItem(text = { Text(if (effectsOn) "Turn sound effects off" else "Turn sound effects on") }, onClick = {
                            menu = false
                            session.setEffects(!effectsOn)
                        })
                        if (session.calendar != null) {
                            DropdownMenuItem(
                                text = { Text(if (calendarOn) "Refresh phone calendar" else "Use this phone's calendar") },
                                onClick = {
                                    menu = false
                                    if (calendarOn) controller.syncCalendar() else controller.useCalendar()
                                },
                            )
                        }
                        actions.forEach { action ->
                            DropdownMenuItem(text = { Text(action.label) }, onClick = {
                                menu = false
                                action.run()
                            })
                        }
                        DropdownMenuItem(text = { Text("Sign out of ${session.signedIn.username}") }, onClick = {
                            menu = false
                            session.signOut()
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
                    // Short - the keyboard's up, or a small phone - the character gives up some room so
                    // the messages can still be read. It's framed to fit whatever it gets.
                    val stage = maxHeight * if (maxHeight < 480.dp) 0.28f else 0.4f
                    Column(Modifier.fillMaxSize()) {
                        AvatarView(avatar, loadModel, Modifier.fillMaxWidth().height(stage))
                        conversation()
                    }
                }
            }
        }
    }
}

/** A page the familiar handed over (BACKLOG item 34): tapped, it opens in the browser. */
@Composable
private fun LinkCard(link: LinkView) {
    val opener = LocalUriHandler.current
    val shape = RoundedCornerShape(10.dp)
    Column(
        Modifier
            .fillMaxWidth()
            .clip(shape)
            .border(1.dp, MaterialTheme.colorScheme.outline, shape)
            .clickable { runCatching { opener.openUri(link.url) } }
            .padding(horizontal = 10.dp, vertical = 7.dp),
    ) {
        Text(link.title, style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.SemiBold)
        Text(link.site, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

/** A picture sent in the chat, fetched once and drawn; nothing while it loads or if it can't be. */
@Composable
private fun PictureFrom(session: FamiliarSession, pictures: PicturePicker?, id: String) {
    if (pictures == null) return
    var bitmap by remember(id) { mutableStateOf<androidx.compose.ui.graphics.ImageBitmap?>(null) }
    LaunchedEffect(id) { bitmap = runCatching { pictures.decode(session.api.image(id)) }.getOrNull() }
    bitmap?.let {
        Image(it, contentDescription = "A picture you sent", contentScale = ContentScale.Fit,
            modifier = Modifier.heightIn(max = 220.dp).clip(RoundedCornerShape(10.dp)))
    }
}

@Composable
private fun Bubble(message: ChatMessage, picture: @Composable (String) -> Unit = {}) {
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
            message.imageId?.let { picture(it) }
            if (message.text.isNotEmpty() || (message.tools.isEmpty() && message.imageId == null)) {
                Text(
                    message.text + (if (message.interrupted) "…" else "") + (if (message.streaming) " ▍" else ""),
                    color = if (message.fromUser) colors.onPrimary else colors.onSurface,
                )
            }
            message.links.filter { it.url.startsWith("https://") || it.url.startsWith("http://") }.forEach { LinkCard(it) }
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

/** Further than any message is long: scrolling stops at the end. */
private const val END = 100_000f
