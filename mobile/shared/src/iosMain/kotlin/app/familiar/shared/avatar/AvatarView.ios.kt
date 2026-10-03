package app.familiar.shared.avatar

import androidx.compose.foundation.layout.Box
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier

/** WKWebView hosting comes with iOS parity (docs/MOBILE.md §7, M5); until then, an empty stage. */
@Composable
actual fun AvatarView(controller: AvatarController, loadModel: suspend (String) -> ByteArray?, modifier: Modifier) {
    Box(modifier)
}
