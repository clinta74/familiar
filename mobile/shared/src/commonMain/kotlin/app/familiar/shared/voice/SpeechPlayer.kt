package app.familiar.shared.voice

import app.familiar.shared.protocol.AudioEvent
import app.familiar.shared.protocol.InterruptRequest
import app.familiar.shared.protocol.SpokenWord
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.Job
import kotlinx.coroutines.async
import kotlinx.coroutines.launch

/** Decoded audio: interleaved 16-bit samples. */
class Pcm(val samples: ShortArray, val sampleRate: Int, val channels: Int) {
    val seconds: Double get() = samples.size.toDouble() / channels / sampleRate
}

/**
 * The phone's speaker, natively (docs/MOBILE.md §1.1): audio focus, Bluetooth and the volume keys
 * behave as they do for any app that talks.
 */
interface SpeechAudio {
    /** An encoded clip (MP3, WAV…) as PCM, or null if it can't be decoded. */
    suspend fun decode(bytes: ByteArray): Pcm?

    /**
     * Plays [pcm] after whatever was queued before it, returning when it has been heard. About 30
     * times a second [onFrame] says how far in it is (seconds) and how loud it is there (0-1).
     */
    suspend fun play(pcm: Pcm, onFrame: (seconds: Double, level: Double) -> Unit)

    /** Silence, now. */
    fun stop()

    /** Speaking started or finished: take audio focus (ducking music) or give it back. */
    fun focus(speaking: Boolean) {}
}

/**
 * Plays a reply's sentences in order as they're announced, fetching and decoding each as soon as
 * it is so the next is ready before the current one ends - the web client's AudioPlayer, ported.
 * Reports each clip's word timings and the voice's loudness (for the avatar's mouth), and keeps
 * track of how much of the reply has been heard: what an interruption cuts the stored reply to.
 */
class SpeechPlayer(
    private val audio: SpeechAudio,
    private val scope: CoroutineScope,
    /** Fetches a clip by the `url` its `audio` event gave. */
    private val fetch: suspend (String) -> ByteArray,
) {
    /** A clip started: its word timings, if the TTS service gave them. */
    var onClip: (words: List<SpokenWord>?) -> Unit = {}

    /** Loudness 0-1, ~30 times a second while speaking. */
    var onLevel: (Double) -> Unit = {}

    var onPlayingChange: (Boolean) -> Unit = {}

    var isPlaying = false
        private set

    private class Pending(val clip: AudioEvent, val pcm: Deferred<Pcm?>)

    private var replyId: String? = null
    private var nextSeq = 0
    private val pending = mutableMapOf<Int, Pending>()
    private val finished = mutableListOf<String>()
    private var current: AudioEvent? = null
    private var elapsed = 0.0
    private var pump: Job? = null

    fun enqueue(clip: AudioEvent) {
        if (clip.replyId != replyId) {
            // A new reply supersedes whatever was still being said.
            stop()
            replyId = clip.replyId
        }
        if (clip.seq >= nextSeq && clip.seq !in pending) {
            pending[clip.seq] = Pending(clip, scope.async { load(clip.url) })
        }
        start()
    }

    /**
     * What has been heard of the reply being spoken: every finished sentence and the words of the
     * current one said so far. Null when nothing is being spoken.
     */
    fun heard(): InterruptRequest? {
        val reply = replyId ?: return null
        val playing = current
        if (playing == null && finished.isEmpty()) return null

        val parts = finished.toMutableList()
        if (playing != null) {
            val words = playing.words
            if (!words.isNullOrEmpty()) {
                parts += joinWords(words.filter { it.end <= elapsed + 0.05 }.map { it.word })
            } else {
                // No timings: assume the words are spread evenly through the clip.
                val share = (elapsed / currentSeconds.coerceAtLeast(0.01)).coerceIn(0.0, 1.0)
                val cut = playing.text.lastIndexOf(' ', (playing.text.length * share).toInt())
                if (cut > 0) parts += playing.text.substring(0, cut)
            }
        }
        return InterruptRequest(reply, parts.filter { it.isNotBlank() }.joinToString(" ").trim())
    }

    /** Silence, now: the user interrupted, a new reply began, or the conversation was cleared. */
    fun stop() {
        pump?.cancel()
        pump = null
        pending.values.forEach { it.pcm.cancel() }
        pending.clear()
        replyId = null
        nextSeq = 0
        finished.clear()
        current = null
        audio.stop()
        setPlaying(false)
    }

    private var currentSeconds = 0.0

    private fun start() {
        if (pump?.isActive == true) return
        pump = scope.launch {
            while (true) {
                val next = pending.remove(nextSeq) ?: break // the next `audio` event starts it again
                nextSeq++
                val pcm = next.pcm.await() ?: continue
                current = next.clip
                currentSeconds = pcm.seconds
                elapsed = 0.0
                setPlaying(true)
                onClip(next.clip.words)
                audio.play(pcm) { seconds, level ->
                    elapsed = seconds
                    onLevel(level)
                }
                finished += next.clip.text
                current = null
            }
            setPlaying(false)
        }
    }

    private suspend fun load(url: String): Pcm? = try {
        audio.decode(fetch(url))
    } catch (exception: Exception) {
        if (exception is CancellationException) throw exception
        null
    }

    private fun setPlaying(playing: Boolean) {
        if (playing == isPlaying) return
        isPlaying = playing
        audio.focus(playing)
        if (!playing) onLevel(0.0)
        onPlayingChange(playing)
    }

    companion object {
        /** Kokoro times punctuation as words of its own; put it back against the word before. */
        fun joinWords(words: List<String>): String = words.fold("") { text, w ->
            when {
                w.isNotEmpty() && w[0] in ".,!?;:…'\"”’)" -> text + w
                text.isEmpty() -> w
                else -> "$text $w"
            }
        }
    }
}
