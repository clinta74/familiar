package app.familiar.shared.ui.settings

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.unit.dp
import app.familiar.shared.avatar.AvatarController
import app.familiar.shared.avatar.AvatarView
import app.familiar.shared.avatar.Character
import app.familiar.shared.avatar.characters
import app.familiar.shared.platform.FilePicker
import app.familiar.shared.protocol.AppearanceView
import app.familiar.shared.protocol.Emotion
import app.familiar.shared.protocol.FamiliarJson
import app.familiar.shared.protocol.FamiliarView
import app.familiar.shared.protocol.Gesture
import app.familiar.shared.protocol.LlamaColours
import app.familiar.shared.protocol.SoundEffect
import app.familiar.shared.session.FamiliarSession
import kotlinx.coroutines.launch
import kotlinx.serialization.json.jsonObject
import kotlin.math.PI
import app.familiar.shared.protocol.AvatarView as UploadedModel

private val accessoryLabels = mapOf("none" to "Nothing", "bow" to "Bow", "scarf" to "Scarf", "glasses" to "Glasses", "flower" to "Flower")

private val tryEmotions = listOf(
    Emotion.JOY to "Happy",
    Emotion.SAD to "Sad",
    Emotion.SURPRISED to "Surprised",
    Emotion.ANGRY to "Cross",
    Emotion.THINKING to "Thinking",
    Emotion.RELAXED to "Relaxed",
)

private val tryGestures = listOf(
    Gesture.NOD, Gesture.SHAKE, Gesture.TILT, Gesture.WAVE, Gesture.SHRUG,
    Gesture.LAUGH, Gesture.WINK, Gesture.SIGH, Gesture.YAWN, Gesture.EXCITED,
)

private val trySounds = listOf(
    SoundEffect.POP, SoundEffect.BOING, SoundEffect.SPARKLE, SoundEffect.TADA, SoundEffect.WHOOSH, SoundEffect.WOMP,
    SoundEffect.RIMSHOT, SoundEffect.DRUMROLL, SoundEffect.DING, SoundEffect.SLIDE, SoundEffect.UHOH,
)

/** The most an upload may be; the server says the same. */
private const val MAX_MODEL_BYTES = 40 * 1024 * 1024

private val defaultLook = AppearanceView("llama", null, characters.first().presets.first().colours, "none", "2d")

private fun LlamaColours.get(key: String) = when (key) {
    "wool" -> wool
    "face" -> face
    "ear" -> ear
    "blush" -> blush
    else -> accent
}

private fun LlamaColours.with(key: String, value: String) = when (key) {
    "wool" -> copy(wool = value)
    "face" -> copy(face = value)
    "ear" -> copy(ear = value)
    "blush" -> copy(blush = value)
    else -> copy(accent = value)
}

private fun LlamaColours.same(other: LlamaColours) =
    listOf("wool", "face", "ear", "blush", "accent").all { get(it).equals(other.get(it), ignoreCase = true) }

private fun characterOf(model: String): Character = characters.firstOrNull { it.id == model } ?: characters.first()

/**
 * The studio (PLAN.md §5.5), as on the web: change how the familiar looks and see it straight away.
 * A built-in character can be recoloured, dressed and drawn flat or in 3D; or upload a VRM model.
 */
@Composable
fun StudioScreen(session: FamiliarSession, files: FilePicker?, onBack: () -> Unit) {
    val scope = rememberCoroutineScope()
    val preview = remember { AvatarController() }
    var profile by remember { mutableStateOf<FamiliarView?>(null) }
    var saved by remember { mutableStateOf(defaultLook) }
    var look by remember { mutableStateOf(defaultLook) }
    var models by remember { mutableStateOf<List<UploadedModel>>(emptyList()) }
    var status by remember { mutableStateOf<Status?>(null) }
    var busy by remember { mutableStateOf(false) }
    var turnDegrees by remember { mutableFloatStateOf(0f) }
    var deleting by remember { mutableStateOf<UploadedModel?>(null) }
    var reload by remember { mutableStateOf(0) }
    var uploading by remember { mutableStateOf(false) }

    LaunchedEffect(reload) {
        runCatching { session.api.profile() }
            .onSuccess {
                profile = it
                val appearance = it.appearance ?: defaultLook
                look = appearance
                saved = appearance
            }
            .onFailure { status = Status(false, it.shown("Couldn't load your familiar.")) }
        runCatching { session.api.avatars() }.onSuccess { models = it }
    }
    LaunchedEffect(look) { preview.look(FamiliarJson.encodeToJsonElement(AppearanceView.serializer(), look).jsonObject) }
    LaunchedEffect(turnDegrees) { preview.turn(turnDegrees * PI / 180) }

    val builtIn = look.model != "vrm"
    val info = characterOf(look.model)
    val turnable = !builtIn || (look.style == "3d" && info.has3d)
    val dirty = look != saved

    /** Switching character: its own default colours, unless the current ones are already its. */
    fun choose(character: Character) {
        val keep = look.model == character.id || character.presets.any { it.colours.same(look.colours) }
        look = look.copy(
            model = character.id,
            vrmId = null,
            colours = if (keep) look.colours else character.presets.first().colours,
            accessory = if (look.accessory in character.accessories) look.accessory else "none",
        )
        status = null
    }

    val previewPane: @Composable (Modifier) -> Unit = { modifier ->
        Column(modifier, verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Box(Modifier.weight(1f).fillMaxWidth()) {
                AvatarView(preview, { id -> runCatching { session.api.avatarModel(id) }.getOrNull() }, Modifier.fillMaxSize())
            }
            if (turnable) {
                Row(Modifier.padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text("Turn", style = MaterialTheme.typography.bodySmall)
                    Slider(turnDegrees, { turnDegrees = (it / 5).toInt() * 5f }, valueRange = -180f..180f, modifier = Modifier.weight(1f).padding(horizontal = 8.dp))
                    TextButton(enabled = turnDegrees != 0f, onClick = { turnDegrees = 0f }) { Text("Face front") }
                }
            }
            // One scrolling row each, so the preview keeps the room.
            Row(Modifier.horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                tryEmotions.forEach { (emotion, label) -> Pill(label) { preview.expression(emotion, 0.9) } }
            }
            Row(Modifier.horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                tryGestures.forEach { gesture -> Pill(gesture) { preview.gesture(gesture) } }
            }
            Row(Modifier.horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                trySounds.forEach { sound -> Pill("♪ $sound") { preview.sound(sound) } }
            }
        }
    }

    val controls: @Composable (Modifier) -> Unit = { modifier ->
        Column(modifier.verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Card("Model") {
                characters.forEach { character ->
                    Row(Modifier.fillMaxWidth().clickable { choose(character) }, verticalAlignment = Alignment.CenterVertically) {
                        RadioButton(selected = look.model == character.id, onClick = { choose(character) })
                        Column { Text(character.name); Muted(character.blurb) }
                    }
                }
                models.forEach { model ->
                    val pick = { look = look.copy(model = "vrm", vrmId = model.id); status = null }
                    Row(Modifier.fillMaxWidth().clickable(onClick = pick), verticalAlignment = Alignment.CenterVertically) {
                        RadioButton(selected = look.model == "vrm" && look.vrmId == model.id, onClick = pick)
                        Column(Modifier.weight(1f)) {
                            Text(model.name)
                            val meta = model.meta
                            Muted(
                                listOfNotNull(
                                    "VRM ${meta.version}" + if (meta.authors.isNotEmpty()) " by ${meta.authors.joinToString(", ")}" else "",
                                    meta.licence?.let { "licence: $it" },
                                    "${(model.sizeBytes / 1024.0 / 1024.0 * 10).toInt() / 10.0} MB",
                                ).joinToString(" · "),
                            )
                        }
                        TextButton(onClick = { deleting = model }) { Text("Delete") }
                    }
                }
                if (files != null) {
                    OutlinedButton(enabled = !busy && !uploading, onClick = {
                        scope.launch {
                            val picked = files.pick() ?: return@launch
                            if (picked.bytes.size > MAX_MODEL_BYTES) {
                                status = Status(false, "That's over 40 MB.")
                                return@launch
                            }
                            uploading = true
                            status = null
                            runCatching { session.api.uploadAvatar(picked.name, picked.bytes) }
                                .onSuccess { model ->
                                    models = models + model
                                    look = look.copy(model = "vrm", vrmId = model.id)
                                    status = Status(true, "Uploaded \"${model.name}\". Save to use it.")
                                }
                                .onFailure { status = Status(false, it.shown("Couldn't upload that.")) }
                            uploading = false
                        }
                    }) {
                        if (uploading) {
                            CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp)
                            Spacer(Modifier.width(8.dp))
                        }
                        Text(if (uploading) "Uploading…" else "Upload a VRM model…")
                    }
                }
                Muted("Make one for free in VRoid Studio and export it as .vrm (up to 40 MB). Check the model's licence allows how you use it.")
            }

            if (builtIn) {
                if (info.has3d) {
                    Card("Style") {
                        ChipRow {
                            Pill("Flat", selected = look.style != "3d") { look = look.copy(style = "2d") }
                            Pill("3D", selected = look.style == "3d") { look = look.copy(style = "3d") }
                        }
                        Muted("3D is modelled and lit, in soft fur. It needs a little more from the phone.")
                    }
                }

                Card("Colours") {
                    ChipRow {
                        info.presets.forEach { preset ->
                            val selected = preset.colours.same(look.colours)
                            Column(
                                Modifier.clip(RoundedCornerShape(10.dp)).clickable { look = look.copy(colours = preset.colours) }.padding(6.dp),
                                horizontalAlignment = Alignment.CenterHorizontally,
                            ) {
                                PresetSwatch(preset.colours, selected)
                                Text(preset.name, style = MaterialTheme.typography.labelSmall)
                            }
                        }
                    }
                    var picking by remember { mutableStateOf<String?>(null) }
                    info.colourLabels.forEach { (key, label) ->
                        Row(Modifier.fillMaxWidth().clickable { picking = key }.padding(vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
                            Swatch(parseHex(look.colours.get(key)) ?: MaterialTheme.colorScheme.outline, onClick = { picking = key })
                            Text(label, Modifier.padding(start = 12.dp).weight(1f))
                            Muted(look.colours.get(key))
                        }
                    }
                    picking?.let { key ->
                        ColourDialog(
                            title = info.colourLabels[key] ?: key,
                            initial = look.colours.get(key),
                            onPick = { look = look.copy(colours = look.colours.with(key, it)) },
                            onDismiss = { picking = null },
                        )
                    }
                }

                if (info.accessories.size > 1) {
                    Card("Wearing") {
                        ChipRow {
                            info.accessories.forEach { accessory ->
                                Pill(accessoryLabels[accessory] ?: accessory, selected = look.accessory == accessory) { look = look.copy(accessory = accessory) }
                            }
                        }
                    }
                }
            }

            StatusLine(status)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                BusyButton("Save", busy, enabled = dirty && profile != null && !uploading) {
                    val current = profile ?: return@BusyButton
                    busy = true
                    status = null
                    scope.launch {
                        runCatching { session.api.saveProfile(current.copy(appearance = look)) }
                            .onSuccess {
                                profile = it
                                saved = it.appearance ?: look
                                look = saved
                                session.profileSaved(it)
                                status = Status(true, "Saved. This is how it looks everywhere now.")
                            }
                            .onFailure { status = Status(false, it.shown("Couldn't save.")) }
                        busy = false
                    }
                }
                OutlinedButton(enabled = dirty, onClick = { look = saved; status = null }) { Text("Undo changes") }
            }
            Muted("Name, voice and personality are in Settings › Your familiar.")
        }
    }

    ScreenFrame(Route.Studio.title, onBack) {
        BoxWithConstraints(Modifier.weight(1f).fillMaxWidth()) {
            if (maxWidth >= 600.dp) {
                Row(Modifier.fillMaxSize()) {
                    previewPane(Modifier.weight(1f).fillMaxHeight().padding(bottom = 8.dp))
                    controls(Modifier.weight(1f).fillMaxHeight())
                }
            } else {
                val previewHeight = maxHeight * 0.45f
                Column(Modifier.fillMaxSize()) {
                    previewPane(Modifier.fillMaxWidth().height(previewHeight))
                    controls(Modifier.weight(1f).fillMaxWidth())
                }
            }
        }
    }

    deleting?.let { model ->
        Confirm(
            title = "Delete \"${model.name}\"?",
            text = "If your familiar is wearing it, it goes back to the llama.",
            confirm = "Delete",
            onConfirm = {
                scope.launch {
                    runCatching { session.api.deleteAvatar(model.id) }
                        .onSuccess {
                            models = models.filterNot { it.id == model.id }
                            reload++
                        }
                        .onFailure { status = Status(false, it.shown("Couldn't delete it.")) }
                }
            },
            onDismiss = { deleting = null },
        )
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun ChipRow(modifier: Modifier = Modifier, content: @Composable () -> Unit) {
    FlowRow(modifier, horizontalArrangement = Arrangement.spacedBy(6.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) { content() }
}

/** A small rounded button, like the web studio's chips. */
@Composable
private fun Pill(label: String, selected: Boolean = false, onClick: () -> Unit) {
    val colors = MaterialTheme.colorScheme
    Text(
        label,
        style = MaterialTheme.typography.labelLarge,
        color = if (selected) colors.onPrimary else colors.onSurface,
        modifier = Modifier
            .clip(RoundedCornerShape(50))
            .background(if (selected) colors.primary else colors.surface)
            .border(1.dp, if (selected) colors.primary else colors.outline, RoundedCornerShape(50))
            .clickable(onClick = onClick)
            .padding(horizontal = 12.dp, vertical = 6.dp),
    )
}

/** A preset as the web shows it: the coat, with the face colour inside. */
@Composable
private fun PresetSwatch(colours: LlamaColours, selected: Boolean) {
    val outline = if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline
    Box(
        Modifier.size(40.dp).clip(CircleShape).background(parseHex(colours.wool) ?: outline).border(if (selected) 3.dp else 1.dp, outline, CircleShape),
        contentAlignment = Alignment.Center,
    ) {
        Box(Modifier.size(18.dp).clip(CircleShape).background(parseHex(colours.face) ?: outline))
    }
}
