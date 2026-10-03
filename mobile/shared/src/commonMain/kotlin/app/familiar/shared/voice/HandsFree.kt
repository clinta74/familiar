package app.familiar.shared.voice

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
import kotlin.concurrent.Volatile

/**
 * What hands-free needs from the phone (docs/MOBILE.md, M2): a microphone that stays open - with the
 * screen off too, which on Android means a foreground service - and a voice-activity model.
 */
interface HandsFreeAudio {
    /** May record (the user allowed it). */
    fun canRecord(): Boolean

    /** Asks the user (the system prompt); true when it was allowed. */
    suspend fun requestAccess(): Boolean

    /**
     * Opens the microphone and keeps the app running while it's open. [onFrame] gets
     * [SpeechDetector.FRAME_SAMPLES] samples at a time (16 kHz, -1..1) on a background thread.
     * [onStopped] is called if it's stopped from outside the app (the notification's Stop).
     * @throws IllegalStateException when the microphone can't be opened.
     */
    fun start(onFrame: (FloatArray) -> Unit, onStopped: () -> Unit)

    fun stop()

    /** The voice-activity model: how likely [frame] is speech, 0-1. Called on the frame thread. */
    fun speechProbability(frame: FloatArray): Float

    /** Forgets the model's state between utterances. */
    fun resetModel()
}

/**
 * Hands-free listening: the microphone stays open and each utterance is handed over when the user
 * stops talking - no button (PLAN.md §3.2). Speech detection runs on the frame thread; what it
 * finds is reported on [scope] (the UI's).
 */
class HandsFree(private val audio: HandsFreeAudio, private val scope: CoroutineScope) {
    /** The user is really talking (time to stop the familiar if it's speaking). */
    var onSpeechStart: () -> Unit = {}

    /** They've stopped: what they said, as a WAV. */
    var onUtterance: (ByteArray) -> Unit = {}

    /** It wasn't speech after all. */
    var onMisfire: () -> Unit = {}

    /** Switched off from outside the app. */
    var onStopped: () -> Unit = {}

    private val detector = SpeechDetector(SpeechDetector.LISTENING)

    @Volatile
    private var settings = SpeechDetector.LISTENING

    @Volatile
    var active = false
        private set

    fun canRecord() = audio.canRecord()

    suspend fun requestAccess() = audio.requestAccess()

    /** @throws IllegalStateException when the microphone can't be opened. */
    fun start() {
        if (active) return
        detector.reset()
        audio.resetModel()
        active = true
        try {
            audio.start(onFrame = ::frame, onStopped = {
                scope.launch {
                    if (active) {
                        active = false
                        onStopped()
                    }
                }
            })
        } catch (exception: IllegalStateException) {
            active = false
            throw exception
        }
    }

    fun stop() {
        if (!active) return
        active = false
        audio.stop()
    }

    /** Whether the familiar is talking: speaking over it takes a firmer voice (see [SpeechDetector]). */
    fun familiarSpeaking(speaking: Boolean) {
        settings = if (speaking) SpeechDetector.OVER_THE_FAMILIAR else SpeechDetector.LISTENING
    }

    private fun frame(samples: FloatArray) {
        if (!active) return
        detector.settings = settings
        val event = detector.process(samples, audio.speechProbability(samples)) ?: return
        scope.launch {
            if (!active) return@launch
            when (event) {
                SpeechDetector.Event.Started -> onSpeechStart()
                is SpeechDetector.Event.Ended -> onUtterance(wav(pcm16(event.samples)))
                SpeechDetector.Event.Misfire -> onMisfire()
            }
        }
    }
}
