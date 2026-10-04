package app.familiar.shared.ui

import androidx.compose.runtime.Composable

/** The system's back (Android's button or gesture) runs [onBack] while [enabled]. */
@Composable
expect fun SystemBack(enabled: Boolean = true, onBack: () -> Unit)
