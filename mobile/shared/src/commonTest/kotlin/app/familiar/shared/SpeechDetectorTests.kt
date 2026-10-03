package app.familiar.shared

import app.familiar.shared.voice.SpeechDetector
import app.familiar.shared.voice.SpeechDetector.Event
import app.familiar.shared.voice.pcm16
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertIs
import kotlin.test.assertTrue

class SpeechDetectorTests {
    private val frame = FloatArray(SpeechDetector.FRAME_SAMPLES)

    /** Feeds [count] frames at [probability]; the events, in order. */
    private fun SpeechDetector.feed(count: Int, probability: Float) = (1..count).mapNotNull { process(frame, probability) }

    @Test
    fun an_utterance_starts_once_it_is_sustained_and_ends_after_a_pause() {
        val detector = SpeechDetector(SpeechDetector.LISTENING)

        assertEquals(emptyList(), detector.feed(20, 0.1f)) // quiet
        val talking = detector.feed(30, 0.9f) // ~1 s of speech
        assertEquals(listOf<Event>(Event.Started), talking)
        // A short pause mid-sentence isn't the end.
        assertEquals(emptyList(), detector.feed(10, 0.2f))
        assertEquals(emptyList(), detector.feed(10, 0.9f))
        val end = detector.feed(30, 0.1f).single()

        assertIs<Event.Ended>(end)
        // The speech, the pause and the 700 ms that ended it, and ~300 ms before it began.
        val frames = end.samples.size / SpeechDetector.FRAME_SAMPLES
        assertTrue(frames in 80..85, "$frames frames")
    }

    @Test
    fun a_cough_is_a_misfire_not_an_utterance() {
        val detector = SpeechDetector(SpeechDetector.LISTENING)

        val events = detector.feed(4, 0.95f) + detector.feed(30, 0.05f)

        assertEquals(listOf<Event>(Event.Misfire), events)
    }

    @Test
    fun over_the_familiar_it_takes_a_firmer_voice() {
        val detector = SpeechDetector(SpeechDetector.OVER_THE_FAMILIAR)

        // What leaks from the speaker after echo cancellation: speech-ish, but not convincingly.
        assertEquals(emptyList(), detector.feed(60, 0.7f))
        detector.settings = SpeechDetector.LISTENING
        assertEquals(listOf<Event>(Event.Started), detector.feed(20, 0.7f))
    }

    @Test
    fun a_very_long_utterance_is_sent_anyway() {
        val detector = SpeechDetector(SpeechDetector.LISTENING.copy(maxSpeechMs = 3_200))

        val events = detector.feed(120, 0.9f)

        assertEquals(Event.Started, events.first())
        assertTrue(events.count { it is Event.Ended } >= 1)
    }

    @Test
    fun samples_become_16_bit_pcm() {
        val pcm = pcm16(floatArrayOf(0f, 0.5f, -1f, 2f))

        assertEquals(listOf<Short>(0, 16383, -32767, 32767), pcm.samples.toList())
        assertEquals(1, pcm.channels)
    }
}
