package app.familiar.shared.ui

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

// The web client's palette (client/src/styles.css), so the familiar feels the same everywhere.

private val Light = lightColorScheme(
    primary = Color(0xFF7C5CFF),
    onPrimary = Color.White,
    background = Color(0xFFF6F5FB),
    onBackground = Color(0xFF1D1B26),
    surface = Color.White,
    onSurface = Color(0xFF1D1B26),
    surfaceVariant = Color(0xFFF6F5FB),
    onSurfaceVariant = Color(0xFF6B6880),
    outline = Color(0xFFE3E0EF),
    error = Color(0xFFC62F4A),
)

private val Dark = darkColorScheme(
    primary = Color(0xFF9B82FF),
    onPrimary = Color(0xFF14131B),
    background = Color(0xFF14131B),
    onBackground = Color(0xFFECEBF4),
    surface = Color(0xFF1D1C27),
    onSurface = Color(0xFFECEBF4),
    surfaceVariant = Color(0xFF14131B),
    onSurfaceVariant = Color(0xFF9C99B3),
    outline = Color(0xFF2F2D3D),
    error = Color(0xFFFF7A90),
)

@Composable
fun FamiliarTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = if (isSystemInDarkTheme()) Dark else Light, content = content)
}
