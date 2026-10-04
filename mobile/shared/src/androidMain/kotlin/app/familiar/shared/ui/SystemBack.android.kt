package app.familiar.shared.ui

import androidx.activity.compose.BackHandler
import androidx.compose.runtime.Composable

@Composable
actual fun SystemBack(enabled: Boolean, onBack: () -> Unit) = BackHandler(enabled, onBack)
