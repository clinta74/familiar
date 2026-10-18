package app.familiar.android

import android.content.ContentResolver
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.ImageDecoder
import android.net.Uri
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import java.io.ByteArrayOutputStream
import kotlin.math.max
import kotlin.math.roundToInt

/**
 * Pictures for the chat (BACKLOG item 35): read from the photo picker or a share, shrunk to 1024
 * pixels on the longer side - the camera's rotation applied by the decoder - and re-saved as JPEG,
 * which leaves behind what the camera wrote beside it (its GPS position). The server strips it too.
 */
object AndroidPictures {
    private const val MAX_SIDE = 1024

    /** [uri] shrunk and re-saved. Throws with a message to show if it can't be read. */
    fun shrink(resolver: ContentResolver, uri: Uri): ByteArray {
        val bitmap = try {
            ImageDecoder.decodeBitmap(ImageDecoder.createSource(resolver, uri)) { decoder, info, _ ->
                val scale = minOf(1.0, MAX_SIDE.toDouble() / max(info.size.width, info.size.height))
                decoder.setTargetSize(
                    (info.size.width * scale).roundToInt().coerceAtLeast(1),
                    (info.size.height * scale).roundToInt().coerceAtLeast(1),
                )
                decoder.allocator = ImageDecoder.ALLOCATOR_SOFTWARE
            }
        } catch (exception: Exception) {
            throw IllegalStateException("Couldn't open that picture.", exception)
        }
        return ByteArrayOutputStream().use { out ->
            bitmap.compress(Bitmap.CompressFormat.JPEG, 85, out)
            bitmap.recycle()
            out.toByteArray()
        }
    }

    fun decode(jpeg: ByteArray): ImageBitmap? =
        BitmapFactory.decodeByteArray(jpeg, 0, jpeg.size)?.asImageBitmap()
}
