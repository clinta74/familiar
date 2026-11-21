package app.familiar.shared.ui

import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.unit.dp
import app.familiar.shared.session.FamiliarSession
import app.familiar.shared.ui.settings.Card
import app.familiar.shared.ui.settings.Divider
import app.familiar.shared.ui.settings.Muted
import app.familiar.shared.ui.settings.ScreenFrame
import app.familiar.shared.ui.settings.Scrolling

/**
 * The shopping list, for the shop: tick things off as they go in the basket, add what's been
 * forgotten. The familiar fills it by voice too ("add milk", "I got the bread"); either way it's the
 * same list on the website.
 */
@Composable
fun ShoppingScreen(session: FamiliarSession, onBack: () -> Unit) {
    val items by session.shopping.collectAsState()
    var text by remember { mutableStateOf("") }
    LaunchedEffect(session) { session.refreshShopping() }

    fun add() {
        val adding = text.trim()
        if (adding.isEmpty()) return
        text = ""
        session.changeShopping { session.api.addShopping(adding) }
    }

    ScreenFrame("Shopping list", onBack) {
        Scrolling {
            Card {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    OutlinedTextField(
                        value = text,
                        onValueChange = { text = it.take(120) },
                        placeholder = { Text("Add something…") },
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
                        keyboardActions = KeyboardActions(onDone = { add() }),
                        modifier = Modifier.weight(1f),
                    )
                    Button(onClick = { add() }, enabled = text.isNotBlank(), modifier = Modifier.padding(start = 8.dp)) { Text("Add") }
                }
            }

            if (items.isEmpty()) {
                Muted("Nothing on it. Add things here, or say \"add milk to the shopping list\".")
            } else {
                Card {
                    items.forEachIndexed { index, item ->
                        if (index > 0) Divider()
                        val got = item.gotAt != null
                        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
                            Checkbox(checked = got, onCheckedChange = { checked -> session.changeShopping { session.api.tickShopping(item.id, checked) } })
                            Text(
                                item.text,
                                style = MaterialTheme.typography.bodyLarge,
                                textDecoration = if (got) TextDecoration.LineThrough else null,
                                color = if (got) MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.onSurface,
                                modifier = Modifier.weight(1f),
                            )
                            TextButton(onClick = { session.changeShopping { session.api.removeShopping(item.id) } }) { Text("✕") }
                        }
                    }
                }
                val gotCount = items.count { it.gotAt != null }
                if (gotCount > 0) {
                    TextButton(onClick = { session.changeShopping { session.api.clearShopping() } }) { Text("Clear what's got ($gotCount)") }
                }
            }
        }
    }
}
