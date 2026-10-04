package app.familiar.shared.ui

import androidx.compose.runtime.Composable

// iOS has no back button: each screen's own back arrow does it.
@Composable
actual fun SystemBack(enabled: Boolean, onBack: () -> Unit) = Unit
