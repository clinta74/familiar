package app.familiar.shared.voice

/**
 * When speech starts and stops, from a voice-activity model's per-frame probability: the web
 * client's hands-free listener (vad-web's frame processor) ported, so the phone and the browser
 * cut utterances the same way. Pure - the model and the microphone are elsewhere.
 */
class SpeechDetector(settings: Settings, private val frameMs: Double = FRAME_SAMPLES * 1000.0 / SAMPLE_RATE) {
    /**
     * How sure the model must be. A frame above [positive] is speech; once speaking, it takes
     * [redemptionMs] of frames below [negative] to end. Fewer than [minSpeechMs] of speech in all is
     * a misfire (a cough, a door), not an utterance. [preSpeechPadMs] before the first speech frame is
     * kept so the first syllable isn't clipped; at [maxSpeechMs] the utterance is sent anyway.
     */
    data class Settings(
        val positive: Float,
        val negative: Float,
        val redemptionMs: Int = 700,
        val minSpeechMs: Int,
        val preSpeechPadMs: Int = 300,
        val maxSpeechMs: Int = 30_000,
    )

    sealed interface Event {
        /** Sustained speech: the user really is talking (enough to interrupt the familiar). */
        data object Started : Event

        /** They stopped: the whole utterance, padding included. */
        class Ended(val samples: FloatArray) : Event

        /** It looked like speech but was too short to be any. */
        data object Misfire : Event
    }

    private class Frame(val samples: FloatArray, val speech: Boolean)

    private val buffer = ArrayDeque<Frame>()
    private var speaking = false
    private var started = false
    private var speechFrames = 0
    private var redemption = 0

    var settings: Settings = settings

    private fun frames(ms: Int) = (ms / frameMs).toInt()

    /** One frame and the model's probability that it's speech; what happened, if anything. */
    fun process(frame: FloatArray, probability: Float): Event? {
        val s = settings
        val speech = probability >= s.positive
        buffer.addLast(Frame(frame, speech))
        var event: Event? = null

        if (speech) {
            speechFrames++
            redemption = 0
            speaking = true
        }
        if (speaking && !started && speechFrames >= frames(s.minSpeechMs)) {
            started = true
            event = Event.Started
        }
        if (speaking) {
            val ended = probability < s.negative && ++redemption >= frames(s.redemptionMs)
            if (ended || buffer.size >= frames(s.maxSpeechMs)) return finish() ?: event
        } else {
            while (buffer.size > frames(s.preSpeechPadMs)) buffer.removeFirst()
            speechFrames = 0
        }
        return event
    }

    /** Ends what's being heard now (hands-free switched off mid-sentence): nothing is sent. */
    fun reset() {
        buffer.clear()
        speaking = false
        started = false
        speechFrames = 0
        redemption = 0
    }

    private fun finish(): Event? {
        val heard = buffer.count { it.speech }
        val samples = FloatArray(buffer.sumOf { it.samples.size })
        var at = 0
        for (frame in buffer) {
            frame.samples.copyInto(samples, at)
            at += frame.samples.size
        }
        reset()
        return if (heard >= frames(settings.minSpeechMs)) Event.Ended(samples) else Event.Misfire
    }

    companion object {
        const val SAMPLE_RATE = 16_000

        /** What Silero v5 takes at 16 kHz: 32 ms. */
        const val FRAME_SAMPLES = 512

        // The web client's settings (client/src/voice/HandsFree.ts). Normal listening: patient enough
        // not to cut someone off mid-pause.
        val LISTENING = Settings(positive = 0.6f, negative = 0.45f, minSpeechMs = 300)

        // While the familiar is talking the microphone also hears it from the speaker. Echo
        // cancellation removes most of that; these make what leaks through much less likely to count
        // as the user, at the cost of speaking up a little to interrupt. (The server also ignores a
        // transcript that's just the familiar's own words.)
        val OVER_THE_FAMILIAR = Settings(positive = 0.85f, negative = 0.7f, minSpeechMs = 450)
    }
}

/** Float samples (-1..1) as 16-bit PCM, for [wav]. */
fun pcm16(samples: FloatArray, sampleRate: Int = SpeechDetector.SAMPLE_RATE): Pcm =
    Pcm(ShortArray(samples.size) { (samples[it].coerceIn(-1f, 1f) * 32767f).toInt().toShort() }, sampleRate, 1)
