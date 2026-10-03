package app.familiar.shared

import app.familiar.shared.protocol.AudioEvent
import app.familiar.shared.protocol.SpokenWord
import app.familiar.shared.voice.Pcm
import app.familiar.shared.voice.SpeechAudio
import app.familiar.shared.voice.SpeechPlayer
import kotlinx.coroutines.delay
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class SpeechPlayerTests {
    /** A speaker on the test's virtual clock: a clip's url says how many tenths of a second it lasts. */
    private class FakeAudio : SpeechAudio {
        val played = mutableListOf<String>()
        var stops = 0

        override suspend fun decode(bytes: ByteArray) = Pcm(ShortArray(bytes.size * 100), 1000, 1)

        override suspend fun play(pcm: Pcm, onFrame: (seconds: Double, level: Double) -> Unit) {
            played += "${pcm.samples.size / 100}"
            val tenths = pcm.samples.size / 100
            for (i in 0 until tenths) {
                onFrame(i / 10.0, 0.5)
                delay(100)
            }
        }

        override fun stop() {
            stops++
        }
    }

    private fun clip(reply: String, seq: Int, tenths: Int, text: String = "", words: List<SpokenWord>? = null) =
        AudioEvent(reply, seq, "$tenths", text, words)

    @Test
    fun sentences_play_in_order_whatever_order_they_arrive_in() = runTest {
        val audio = FakeAudio()
        val player = SpeechPlayer(audio, this) { url -> ByteArray(url.toInt()) }
        val playing = mutableListOf<Boolean>()
        player.onPlayingChange = { playing += it }

        player.enqueue(clip("r1", 1, 3))
        runCurrent()
        assertEquals(emptyList(), audio.played) // waiting for the first sentence

        player.enqueue(clip("r1", 0, 2))
        advanceUntilIdle()
        assertEquals(listOf("2", "3"), audio.played)
        assertEquals(listOf(true, false), playing)
    }

    @Test
    fun what_was_heard_is_the_words_said_so_far() = runTest {
        val audio = FakeAudio()
        val player = SpeechPlayer(audio, this) { url -> ByteArray(url.toInt()) }
        val words = listOf(SpokenWord("Hello", 0.0, 0.3), SpokenWord("there", 0.3, 0.6), SpokenWord(".", 0.6, 0.65))

        player.enqueue(clip("r1", 0, 2, "One."))
        player.enqueue(clip("r1", 1, 8, "Hello there.", words))
        advanceTimeBy(250) // the first sentence is done
        runCurrent()
        assertEquals("r1" to "One.", player.heard()!!.let { it.replyId to it.heard })

        advanceTimeBy(400) // 0.4 s into the second
        runCurrent()
        assertEquals("One. Hello", player.heard()!!.heard)

        player.stop()
        assertNull(player.heard())
    }

    @Test
    fun a_new_reply_cuts_off_the_last() = runTest {
        val audio = FakeAudio()
        val player = SpeechPlayer(audio, this) { url -> ByteArray(url.toInt()) }

        player.enqueue(clip("r1", 0, 50))
        advanceTimeBy(200)
        val stops = audio.stops
        player.enqueue(clip("r2", 0, 1))
        advanceUntilIdle()

        assertEquals(listOf("50", "1"), audio.played)
        assertEquals(stops + 1, audio.stops)
        assertEquals(false, player.isPlaying)
    }

    @Test
    fun punctuation_goes_back_against_its_word() {
        assertEquals("Hello, there!", SpeechPlayer.joinWords(listOf("Hello", ",", "there", "!")))
    }
}
