package app.familiar.shared.avatar

import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier

/**
 * The avatar, drawn by the bundled page in a WebView (WKWebView on iOS). [loadModel] fetches an
 * uploaded VRM model by id, with the device's credentials: the page itself has no network access.
 */
@Composable
expect fun AvatarView(controller: AvatarController, loadModel: suspend (String) -> ByteArray?, modifier: Modifier = Modifier)
