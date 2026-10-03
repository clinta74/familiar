package app.familiar.shared

import app.familiar.shared.api.FamiliarApi
import app.familiar.shared.voice.Pcm
import app.familiar.shared.voice.wav
import io.ktor.client.HttpClient
import io.ktor.client.engine.mock.MockEngine
import io.ktor.client.engine.mock.respond
import io.ktor.http.HttpHeaders
import io.ktor.http.HttpStatusCode
import io.ktor.http.content.OutgoingContent
import io.ktor.http.headersOf
import io.ktor.utils.io.ByteReadChannel
import kotlinx.coroutines.test.runTest
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertNull

class PushToTalkTests {
    private fun int32(bytes: ByteArray, at: Int) = (0 until 4).sumOf { (bytes[at + it].toInt() and 0xff) shl (8 * it) }

    private fun int16(bytes: ByteArray, at: Int) = (bytes[at].toInt() and 0xff) or (bytes[at + 1].toInt() shl 8)

    @Test
    fun a_recording_becomes_a_16_bit_mono_wav() {
        val bytes = wav(Pcm(shortArrayOf(0, 1000, -1000, Short.MAX_VALUE), 16_000, 1))

        assertEquals("RIFF", bytes.decodeToString(0, 4))
        assertEquals(36 + 8, int32(bytes, 4))
        assertEquals("WAVEfmt ", bytes.decodeToString(8, 16))
        assertEquals(1, int16(bytes, 20)) // PCM
        assertEquals(1, int16(bytes, 22)) // mono
        assertEquals(16_000, int32(bytes, 24))
        assertEquals(32_000, int32(bytes, 28)) // bytes a second
        assertEquals(16, int16(bytes, 34))
        assertEquals("data", bytes.decodeToString(36, 40))
        assertEquals(8, int32(bytes, 40))
        assertEquals(44 + 8, bytes.size)
        assertEquals(1000, int16(bytes, 46))
        assertEquals(-1000, int16(bytes, 48).toShort().toInt())
    }

    @Test
    fun a_spoken_message_is_sent_as_wav_and_comes_back_transcribed() = runTest {
        var sentType: String? = null
        var sentBytes: ByteArray? = null
        val engine = MockEngine { request ->
            sentType = request.body.contentType?.toString()
            sentBytes = (request.body as OutgoingContent.ByteArrayContent).bytes()
            respond(
                ByteReadChannel("""{"id":"m1","role":"user","text":"set a timer"}"""),
                HttpStatusCode.Accepted,
                headersOf(HttpHeaders.ContentType, "application/json"),
            )
        }
        val api = FamiliarApi("https://nas:7280", client = HttpClient(engine))
        val recording = wav(Pcm(ShortArray(1600), 16_000, 1))

        val message = api.sendSpoken(recording, "Europe/London")

        assertEquals("set a timer", message?.text)
        assertEquals("audio/wav", sentType)
        assertContentEquals(recording, sentBytes)
    }

    @Test
    fun nothing_heard_is_no_message() = runTest {
        val engine = MockEngine { respond(ByteReadChannel(""), HttpStatusCode.NoContent) }
        val api = FamiliarApi("https://nas:7280", client = HttpClient(engine))

        assertNull(api.sendSpoken(wav(Pcm(ShortArray(1600), 16_000, 1)), "Europe/London"))
    }
}
