package app.familiar.shared.ui.settings

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.LocalContentColor
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Slider
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import app.familiar.shared.ui.SystemBack
import kotlin.math.roundToInt

// The pieces the settings screens and the studio share, in the web client's manner: a card per
// section, muted explanations, a status line after saving.

/** What a request said when it failed, fit to show. */
fun Throwable.shown(fallback: String = "Something went wrong."): String = message?.takeIf { it.isNotBlank() } ?: fallback

/** After saving or doing something: green when it worked, red when it didn't. */
data class Status(val ok: Boolean, val message: String)

/** A screen under the chat: a title with a back arrow, and the system's back doing the same. */
@Composable
fun ScreenFrame(title: String, onBack: () -> Unit, content: @Composable ColumnScope.() -> Unit) {
    SystemBack(onBack = onBack)
    Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        Column(Modifier.fillMaxSize().safeDrawingPadding().imePadding()) {
            Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp, vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                TextButton(onClick = onBack) { Text("←", style = MaterialTheme.typography.titleLarge) }
                Text(title, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold)
            }
            content()
        }
    }
}

/** The usual body of a settings screen: scrolls, and keeps to a readable width on a wide screen. */
@Composable
fun ColumnScope.Scrolling(content: @Composable ColumnScope.() -> Unit) {
    Box(Modifier.weight(1f).fillMaxWidth(), contentAlignment = Alignment.TopCenter) {
        Column(
            Modifier.widthIn(max = 640.dp).fillMaxWidth().verticalScroll(rememberScrollState()).padding(horizontal = 16.dp, vertical = 8.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
            content = content,
        )
    }
}

/** A group of settings, like the web's cards. */
@Composable
fun Card(title: String? = null, content: @Composable ColumnScope.() -> Unit) {
    val colors = MaterialTheme.colorScheme
    Column(
        Modifier.fillMaxWidth()
            .background(colors.surface, RoundedCornerShape(14.dp))
            .border(1.dp, colors.outline, RoundedCornerShape(14.dp))
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        title?.let { Text(it, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold) }
        content()
    }
}

@Composable
fun Muted(text: String, modifier: Modifier = Modifier) =
    Text(text, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = modifier)

@Composable
fun ErrorText(text: String) = Text(text, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.error)

@Composable
fun StatusLine(status: Status?) {
    status?.let {
        Text(
            it.message,
            style = MaterialTheme.typography.bodyMedium,
            color = if (it.ok) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.error,
        )
    }
}

@Composable
fun Loading() = Muted("Loading…", Modifier.padding(8.dp))

/** One thing in a list: what it is, a line about it, and what can be done with it. */
@Composable
fun ItemRow(
    title: String,
    detail: String? = null,
    detailIsError: Boolean = false,
    onClick: (() -> Unit)? = null,
    actions: @Composable RowScope.() -> Unit = {},
) {
    Row(
        Modifier.fillMaxWidth().then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier).padding(vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Text(title, style = MaterialTheme.typography.bodyLarge)
            detail?.let {
                Text(
                    it,
                    style = MaterialTheme.typography.bodySmall,
                    color = if (detailIsError) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
        actions()
    }
}

@Composable
fun Divider() = HorizontalDivider(color = MaterialTheme.colorScheme.outline)

/** A setting between two ends ("Reserved" to "Affectionate"), in steps of 0.05 as on the web. */
@Composable
fun LabeledSlider(
    label: String,
    value: Double,
    onChange: (Double) -> Unit,
    low: String,
    high: String,
    range: ClosedFloatingPointRange<Float> = 0f..1f,
) {
    Column {
        Text(label, style = MaterialTheme.typography.bodyMedium)
        // Smooth rather than stepped: a stepped slider draws a dot per step, which is busy.
        Slider(value = value.toFloat(), onValueChange = { onChange((it * 20).roundToInt() / 20.0) }, valueRange = range)
        Row(Modifier.fillMaxWidth()) {
            Muted(low, Modifier.weight(1f))
            Muted(high)
        }
    }
}

/** A button that shows it's working: disabled, with a spinner, until [busy] is over. */
@Composable
fun BusyButton(text: String, busy: Boolean, enabled: Boolean = true, onClick: () -> Unit) {
    Button(onClick = onClick, enabled = enabled && !busy) {
        if (busy) {
            CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp, color = LocalContentColor.current)
            Spacer(Modifier.width(8.dp))
        }
        Text(text)
    }
}

/** "Are you sure?" for what can't be undone. */
@Composable
fun Confirm(title: String, text: String, confirm: String, onConfirm: () -> Unit, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = { Text(text) },
        confirmButton = {
            TextButton(onClick = {
                onDismiss()
                onConfirm()
            }) { Text(confirm, color = MaterialTheme.colorScheme.error) }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

/** A secret the server shows once (a hook token, a webhook's signing secret), with Copy. */
@Composable
fun RevealSecret(label: String, value: String, extra: @Composable ColumnScope.() -> Unit = {}) {
    @Suppress("DEPRECATION") // LocalClipboard is suspend-only and platform-specific; this is enough for text.
    val clipboard = LocalClipboardManager.current
    var copied by remember(value) { mutableStateOf(false) }
    val colors = MaterialTheme.colorScheme
    Column(
        Modifier.fillMaxWidth().background(colors.primary.copy(alpha = 0.08f), RoundedCornerShape(10.dp)).padding(12.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Text("$label - copy it now; it won't be shown again.", style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.SemiBold)
        SelectionContainer { Text(value, fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.bodySmall) }
        OutlinedButton(onClick = {
            clipboard.setText(AnnotatedString(value))
            copied = true
        }) { Text(if (copied) "Copied" else "Copy") }
        extra()
    }
}

// --- colours -------------------------------------------------------------------------------------

/** #rrggbb as a colour; null if it isn't one. */
fun parseHex(hex: String): Color? {
    val digits = hex.trim().removePrefix("#")
    if (digits.length != 6 || digits.any { it.lowercaseChar() !in "0123456789abcdef" }) return null
    return Color(0xFF000000 or digits.toLong(16))
}

fun Color.toHex(): String {
    fun channel(value: Float) = (value * 255f + 0.5f).toInt().coerceIn(0, 255).toString(16).padStart(2, '0')
    return "#" + channel(red) + channel(green) + channel(blue)
}

/** Hue (0-360), saturation and value (0-1) to a colour. */
fun hsv(hue: Float, saturation: Float, value: Float): Color = Color.hsv(hue.coerceIn(0f, 360f), saturation.coerceIn(0f, 1f), value.coerceIn(0f, 1f))

/** A colour's hue (0-360), saturation and value (0-1). */
fun Color.toHsv(): Triple<Float, Float, Float> {
    val max = maxOf(red, green, blue)
    val min = minOf(red, green, blue)
    val delta = max - min
    val hue = when {
        delta == 0f -> 0f
        max == red -> 60f * (((green - blue) / delta).mod(6f))
        max == green -> 60f * (((blue - red) / delta) + 2f)
        else -> 60f * (((red - green) / delta) + 4f)
    }
    return Triple(hue, if (max == 0f) 0f else delta / max, max)
}

@Composable
fun Swatch(colour: Color, size: Int = 28, selected: Boolean = false, onClick: (() -> Unit)? = null) {
    Box(
        Modifier.size(size.dp).clip(CircleShape).background(colour)
            .border(if (selected) 3.dp else 1.dp, if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline, CircleShape)
            .then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier),
    )
}

/** Picks a colour: hue, strength and brightness, or typed as #rrggbb. */
@Composable
fun ColourDialog(title: String, initial: String, onPick: (String) -> Unit, onDismiss: () -> Unit) {
    val start = remember(initial) { (parseHex(initial) ?: Color.Gray).toHsv() }
    var hue by remember { mutableStateOf(start.first) }
    var saturation by remember { mutableStateOf(start.second) }
    var value by remember { mutableStateOf(start.third) }
    var typed by remember { mutableStateOf(initial) }
    val colour = hsv(hue, saturation, value)

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                Box(Modifier.fillMaxWidth().height(48.dp).clip(RoundedCornerShape(10.dp)).background(colour))
                Text("Colour", style = MaterialTheme.typography.bodySmall)
                Slider(value = hue, onValueChange = { hue = it; typed = hsv(it, saturation, value).toHex() }, valueRange = 0f..360f)
                Text("Strength", style = MaterialTheme.typography.bodySmall)
                Slider(value = saturation, onValueChange = { saturation = it; typed = hsv(hue, it, value).toHex() })
                Text("Brightness", style = MaterialTheme.typography.bodySmall)
                Slider(value = value, onValueChange = { value = it; typed = hsv(hue, saturation, it).toHex() })
                OutlinedTextField(
                    value = typed,
                    onValueChange = { text ->
                        typed = text
                        parseHex(text)?.toHsv()?.let { (h, s, v) ->
                            hue = h
                            saturation = s
                            value = v
                        }
                    },
                    label = { Text("#rrggbb") },
                    singleLine = true,
                )
            }
        },
        confirmButton = {
            TextButton(onClick = {
                onPick(colour.toHex())
                onDismiss()
            }) { Text("Use it") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}
