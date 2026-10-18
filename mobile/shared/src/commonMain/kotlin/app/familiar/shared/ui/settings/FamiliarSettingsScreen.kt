package app.familiar.shared.ui.settings

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import app.familiar.shared.protocol.FamiliarView
import app.familiar.shared.protocol.PlaceView
import app.familiar.shared.protocol.VoicesView
import app.familiar.shared.session.FamiliarSession
import kotlinx.coroutines.launch

private class Trait(val label: String, val low: String, val high: String, val get: (FamiliarView) -> Double, val set: (FamiliarView, Double) -> FamiliarView)

private val traits = listOf(
    Trait("Warmth", "Reserved", "Affectionate", { it.warmth }, { p, v -> p.copy(warmth = v) }),
    Trait("Playfulness", "Earnest", "Jokey", { it.playfulness }, { p, v -> p.copy(playfulness = v) }),
    Trait("Chattiness", "Brief", "Chatty", { it.verbosity }, { p, v -> p.copy(verbosity = v) }),
    Trait("Formality", "Casual", "Formal", { it.formality }, { p, v -> p.copy(formality = v) }),
    Trait("Expressiveness", "Subtle", "Animated", { it.expressiveness }, { p, v -> p.copy(expressiveness = v) }),
)

/**
 * Where you live, as a real place (BACKLOG item 32): searched and picked, so "Warren" is the right
 * Warren, or found from where the phone is. The familiar is told it; the look-ups use it.
 */
@Composable
private fun HomePicker(session: FamiliarSession, current: FamiliarView, update: (FamiliarView) -> Unit) {
    val scope = rememberCoroutineScope()
    var query by remember { mutableStateOf("") }
    var matches by remember { mutableStateOf<List<PlaceView>?>(null) }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }

    fun pick(place: PlaceView) {
        update(current.copy(home = place, homePlace = place.label))
        matches = null
        query = ""
        error = null
    }

    fun run(block: suspend () -> Unit) {
        busy = true
        error = null
        scope.launch {
            runCatching { block() }.onFailure { error = it.shown("Couldn't find that just now.") }
            busy = false
        }
    }

    Muted(
        "${current.name.ifBlank { "Your familiar" }} knows it, for anything local, and the weather, air quality and holidays use it. " +
            "Searching sends the name to Open-Meteo; \"Use my location\" sends the phone's position, to about a kilometre, to OpenStreetMap.",
    )
    val typedOnly = current.home == null && !current.homePlace.isNullOrBlank()
    if (!current.homePlace.isNullOrBlank()) {
        ItemRow(current.homePlace!!, if (typedOnly) "Typed, not picked: search for it so it's the right place" else "", detailIsError = typedOnly) {
            TextButton(onClick = { update(current.copy(home = null, homePlace = "")) }) { Text("Clear") }
        }
    } else {
        Muted("Not set.")
    }
    OutlinedTextField(
        value = query,
        onValueChange = { query = it.take(80) },
        label = { Text("Town or city") },
        placeholder = { Text(if (typedOnly) current.homePlace.orEmpty() else "e.g. Warren") },
        singleLine = true,
        modifier = Modifier.fillMaxWidth(),
    )
    Row(verticalAlignment = Alignment.CenterVertically) {
        OutlinedButton(enabled = !busy && query.trim().length >= 2, onClick = {
            run {
                val found = session.api.places(query.trim())
                matches = found
                if (found.isEmpty()) error = "No places by that name. Try the town or city."
            }
        }) { Text("Search") }
        session.phone?.let { phone ->
            TextButton(enabled = !busy, onClick = {
                run {
                    val (latitude, longitude) = phone.position()
                    pick(session.api.placeAt(latitude, longitude))
                }
            }) { Text("Use my location") }
        }
    }
    matches?.forEach { place ->
        TextButton(onClick = { pick(place) }) { Text(place.label) }
    }
    error?.let { ErrorText(it) }
}

/** Who the familiar is (PLAN.md §5.5): the web's "Your familiar". Changes apply from the next thing said. */
@Composable
fun FamiliarSettingsScreen(session: FamiliarSession, onBack: () -> Unit) {
    val scope = rememberCoroutineScope()
    var profile by remember { mutableStateOf<FamiliarView?>(null) }
    var voices by remember { mutableStateOf<VoicesView?>(null) }
    var status by remember { mutableStateOf<Status?>(null) }
    var busy by remember { mutableStateOf(false) }

    LaunchedEffect(Unit) {
        runCatching { session.api.profile() }
            .onSuccess { profile = it }
            .onFailure { status = Status(false, it.shown("Couldn't load your familiar.")) }
        voices = runCatching { session.api.voices() }.getOrElse { VoicesView(emptyList(), "") }
    }

    ScreenFrame(Route.Familiar.title, onBack) {
        Scrolling {
            val current = profile
            if (current == null) {
                status?.let { ErrorText(it.message) } ?: Loading()
                return@Scrolling
            }
            val update = { next: FamiliarView ->
                profile = next
                status = null
            }

            Card {
                OutlinedTextField(
                    value = current.name,
                    onValueChange = { update(current.copy(name = it.take(32))) },
                    label = { Text("Its name") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    value = current.userNickname.orEmpty(),
                    onValueChange = { update(current.copy(userNickname = it.take(32).ifEmpty { null })) },
                    label = { Text("What it calls you") },
                    placeholder = { Text(session.signedIn.username) },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
            }

            Card("Personality") {
                traits.forEach { trait ->
                    LabeledSlider(trait.label, trait.get(current), { update(trait.set(current, it)) }, trait.low, trait.high)
                }
            }

            Card("Voice") {
                VoiceSettings(session, current, voices, update)
            }

            Card {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text("Say hello first", style = MaterialTheme.typography.bodyLarge)
                        Muted("When you come back after a few hours, ${current.name.ifBlank { "it" }} greets you and mentions anything coming up soon.")
                    }
                    Switch(checked = current.proactive ?: true, onCheckedChange = { update(current.copy(proactive = it)) })
                }
            }

            Card {
                val lookUps = current.lookUps ?: false
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text("Look things up", style = MaterialTheme.typography.bodyLarge)
                        Muted(
                            "The weather, air quality and pollen (Open-Meteo), what words mean (Wiktionary), facts and figures " +
                                "(Wikipedia, Wikidata), public holidays (Nager.Date), exchange rates (Frankfurter) and, in the US, your " +
                                "county and districts (the Census Bureau). Only those; what " +
                                "you ask goes to them, nothing else about you. Sums and unit conversions are worked out here either way.",
                        )
                    }
                    Switch(checked = lookUps, onCheckedChange = { update(current.copy(lookUps = it)) })
                }
            }

            Card("Where you live") {
                HomePicker(session, current, update)
            }

            StatusLine(status)
            BusyButton(
                "Save",
                busy = busy,
                enabled = current.name.isNotBlank(),
                onClick = {
                    busy = true
                    scope.launch {
                        runCatching { session.api.saveProfile(current) }
                            .onSuccess {
                                profile = it
                                session.profileSaved(it)
                                status = Status(true, "Saved. It applies from the next thing you say.")
                            }
                            .onFailure { status = Status(false, it.shown("Couldn't save.")) }
                        busy = false
                    }
                },
            )
        }
    }
}

@Composable
private fun VoiceSettings(session: FamiliarSession, profile: FamiliarView, voices: VoicesView?, onChange: (FamiliarView) -> Unit) {
    val scope = rememberCoroutineScope()
    if (voices == null) return
    if (voices.voices.isEmpty()) {
        Muted("Voice settings appear here when text-to-speech is running.")
        return
    }
    val chosen = profile.voice ?: voices.default
    var open by remember { mutableStateOf(false) }
    var previewing by remember { mutableStateOf(false) }
    var problem by remember { mutableStateOf<String?>(null) }

    Box {
        OutlinedButton(onClick = { open = true }, modifier = Modifier.fillMaxWidth()) {
            val voice = describeVoice(chosen)
            Text("${voice.label} · ${voice.language}")
        }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            groupVoices(voices.voices).forEach { (language, options) ->
                DropdownMenuItem(text = { Text(language, fontWeight = FontWeight.Bold) }, onClick = {}, enabled = false)
                options.forEach { option ->
                    DropdownMenuItem(
                        text = { Text((if (option.id == chosen) "✓ " else "") + option.label) },
                        onClick = {
                            open = false
                            onChange(profile.copy(voice = option.id.takeUnless { it == voices.default }))
                        },
                    )
                }
            }
        }
    }
    LabeledSlider("Speed", profile.voiceSpeed, { onChange(profile.copy(voiceSpeed = it)) }, "Slower", "Faster", 0.7f..1.4f)
    if (session.player != null) {
        OutlinedButton(
            enabled = !previewing,
            onClick = {
                previewing = true
                problem = null
                scope.launch {
                    if (!session.previewVoice(chosen, profile.voiceSpeed)) problem = "Couldn't play the preview."
                    previewing = false
                }
            },
        ) { Text(if (previewing) "Playing…" else "Preview") }
        problem?.let { ErrorText(it) }
    }
}
