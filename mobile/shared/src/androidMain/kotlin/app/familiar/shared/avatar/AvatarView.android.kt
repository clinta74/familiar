package app.familiar.shared.avatar

import android.annotation.SuppressLint
import android.content.pm.ApplicationInfo
import android.graphics.Color
import android.net.Uri
import android.util.Log
import android.view.ViewGroup
import android.webkit.ConsoleMessage
import android.webkit.WebChromeClient
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.viewinterop.AndroidView
import kotlinx.coroutines.runBlocking
import java.io.ByteArrayInputStream
import java.io.InputStream

/** The page's origin: never fetched, every request to it is answered here (docs/MOBILE.md §1.1). */
private const val ORIGIN = "https://appassets.androidplatform.net"
private const val PAGE = "$ORIGIN/avatar/avatar.html"
private val modelPath = Regex("^/api/avatars/([0-9a-fA-F-]{36})/model$")

private val mimeTypes = mapOf(
    "html" to "text/html",
    "js" to "text/javascript",
    "css" to "text/css",
    "svg" to "image/svg+xml",
    "png" to "image/png",
    "json" to "application/json",
    "glb" to "model/gltf-binary",
)

@SuppressLint("SetJavaScriptEnabled")
@Composable
actual fun AvatarView(controller: AvatarController, loadModel: suspend (String) -> ByteArray?, modifier: Modifier) {
    var view by remember { mutableStateOf<WebView?>(null) }
    var loaded by remember { mutableStateOf(false) }

    AndroidView(
        modifier = modifier,
        factory = { context ->
            // Inspectable from desktop Chrome in debug builds only.
            WebView.setWebContentsDebuggingEnabled(context.applicationInfo.flags and ApplicationInfo.FLAG_DEBUGGABLE != 0)
            WebView(context).apply {
                // Filling the space Compose gives it: left to wrap its content, the page's 100%-high
                // stage measures as nothing.
                layoutParams = ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT)
                setBackgroundColor(Color.TRANSPARENT)
                isVerticalScrollBarEnabled = false
                isHorizontalScrollBarEnabled = false
                settings.apply {
                    javaScriptEnabled = true
                    allowFileAccess = false
                    allowContentAccess = false
                    cacheMode = WebSettings.LOAD_NO_CACHE
                    setSupportZoom(false)
                    mediaPlaybackRequiresUserGesture = true
                }
                // The page's warnings (a VRM model that wouldn't load, say) in logcat.
                webChromeClient = object : WebChromeClient() {
                    override fun onConsoleMessage(message: ConsoleMessage): Boolean {
                        Log.i("FamiliarAvatar", "${message.messageLevel()}: ${message.message()} (${message.sourceId()}:${message.lineNumber()})")
                        return true
                    }
                }
                webViewClient = object : WebViewClient() {
                    override fun shouldInterceptRequest(view: WebView, request: WebResourceRequest): WebResourceResponse =
                        answer(view, request.url, loadModel)

                    // Links go nowhere: the page is a picture, not a browser.
                    override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest) = true

                    override fun onPageFinished(view: WebView, url: String) {
                        controller.snapshot().forEach { view.evaluateJavascript(it, null) }
                        loaded = true
                    }
                }
                loadUrl(PAGE)
                view = this
            }
        },
        onRelease = { it.destroy() },
    )

    // Listening from the start; until the page has loaded (and caught up from the snapshot) there's
    // nothing to run the calls in.
    LaunchedEffect(view) {
        val web = view ?: return@LaunchedEffect
        controller.calls.collect { if (loaded) web.evaluateJavascript(it, null) }
    }
}

/** The bundled page and its scripts from the app's assets, an uploaded model from the server, and nothing else. */
private fun answer(view: WebView, url: Uri, loadModel: suspend (String) -> ByteArray?): WebResourceResponse {
    if ("${url.scheme}://${url.host}" != ORIGIN) return refused()
    val path = url.path.orEmpty()

    modelPath.find(path)?.let { match ->
        // On a WebView background thread, so waiting here doesn't hold up the UI.
        val bytes = runCatching { runBlocking { loadModel(match.groupValues[1]) } }.getOrNull() ?: return refused(404)
        return ok("model/gltf-binary", ByteArrayInputStream(bytes))
    }

    if (!path.startsWith("/avatar/") || path.contains("..")) return refused(404)
    val asset: InputStream = try {
        view.context.assets.open(path.removePrefix("/"))
    } catch (_: Exception) {
        return refused(404)
    }
    return ok(mimeTypes[path.substringAfterLast('.', "")] ?: "application/octet-stream", asset)
}

private fun ok(mime: String, body: InputStream) =
    WebResourceResponse(mime, if (mime.startsWith("text/")) "utf-8" else null, 200, "OK", mapOf("Cache-Control" to "no-store"), body)

private fun refused(status: Int = 403) =
    WebResourceResponse("text/plain", "utf-8", status, if (status == 404) "Not Found" else "Forbidden", emptyMap(), ByteArrayInputStream(ByteArray(0)))
