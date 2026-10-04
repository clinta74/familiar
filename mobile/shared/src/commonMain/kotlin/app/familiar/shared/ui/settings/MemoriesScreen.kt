package app.familiar.shared.ui.settings

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
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
import androidx.compose.ui.unit.dp
import app.familiar.shared.platform.formatInstant
import app.familiar.shared.protocol.MemorySource
import app.familiar.shared.protocol.MemoryView
import app.familiar.shared.session.FamiliarSession
import kotlinx.coroutines.launch

private val sourceText = mapOf(
    MemorySource.CONVERSATION to "picked up in conversation",
    MemorySource.TOLD to "you asked it to remember",
    MemorySource.SETTINGS to "added here",
)

/**
 * Everything the familiar remembers (PLAN.md §6.6): picked up from conversations, or asked to
 * remember. All of it can be corrected or forgotten here, or by saying "forget that".
 */
@Composable
fun MemoriesScreen(session: FamiliarSession, onBack: () -> Unit) {
    val scope = rememberCoroutineScope()
    var memories by remember { mutableStateOf<List<MemoryView>?>(null) }
    var draft by remember { mutableStateOf("") }
    var error by remember { mutableStateOf<String?>(null) }
    var editing by remember { mutableStateOf<String?>(null) }
    var confirmClear by remember { mutableStateOf(false) }
    var busy by remember { mutableStateOf(false) }

    LaunchedEffect(Unit) {
        runCatching { session.api.memories() }.onSuccess { memories = it }.onFailure { error = it.shown("Couldn't load memories.") }
    }

    fun act(fallback: String, block: suspend () -> Unit) {
        error = null
        busy = true
        scope.launch {
            runCatching { block() }.onFailure { error = it.shown(fallback) }
            busy = false
        }
    }

    ScreenFrame(Route.Memories.title, onBack) {
        Scrolling {
            val list = memories
            if (list == null) {
                error?.let { ErrorText(it) } ?: Loading()
                return@Scrolling
            }
            Muted("Things it has picked up about you, so it doesn't have to ask again. Say \"remember…\" or \"forget…\" to it, or change them here.")

            Card {
                if (list.isEmpty()) Muted("Nothing yet.")
                list.forEachIndexed { index, memory ->
                    if (index > 0) Divider()
                    if (editing == memory.id) {
                        var text by remember(memory.id) { mutableStateOf(memory.text) }
                        Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                            OutlinedTextField(value = text, onValueChange = { text = it.take(300) }, modifier = Modifier.fillMaxWidth())
                            Row {
                                BusyButton("Save", busy, enabled = text.isNotBlank()) {
                                    act("Couldn't save that.") {
                                        val saved = session.api.updateMemory(memory.id, text.trim())
                                        memories = memories?.map { if (it.id == saved.id) saved else it }
                                        editing = null
                                    }
                                }
                                TextButton(onClick = { editing = null }) { Text("Cancel") }
                            }
                        }
                    } else {
                        ItemRow(memory.text, "${sourceText[memory.source] ?: memory.source} · ${formatInstant(memory.updatedAt, withTime = false)}") {
                            TextButton(onClick = { editing = memory.id }) { Text("Edit") }
                            TextButton(onClick = {
                                act("Couldn't forget that.") {
                                    session.api.forgetMemory(memory.id)
                                    memories = memories?.filterNot { it.id == memory.id }
                                }
                            }) { Text("Forget") }
                        }
                    }
                }
            }

            Card("Add something") {
                OutlinedTextField(
                    value = draft,
                    onValueChange = { draft = it.take(300) },
                    placeholder = { Text("e.g. I'm vegetarian") },
                    modifier = Modifier.fillMaxWidth(),
                )
                BusyButton("Add", busy, enabled = draft.isNotBlank()) {
                    act("Couldn't add that.") {
                        val added = session.api.addMemory(draft.trim())
                        memories = memories.orEmpty() + added
                        draft = ""
                    }
                }
            }

            error?.let { ErrorText(it) }
            if (list.isNotEmpty()) {
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    TextButton(onClick = { confirmClear = true }) { Text("Forget everything", color = MaterialTheme.colorScheme.error) }
                }
            }
        }
    }

    if (confirmClear) {
        Confirm(
            title = "Forget everything?",
            text = "Everything it remembers about you goes. This can't be undone.",
            confirm = "Forget everything",
            onConfirm = {
                act("Couldn't forget.") {
                    session.api.forgetEverything()
                    memories = emptyList()
                }
            },
            onDismiss = { confirmClear = false },
        )
    }
}
