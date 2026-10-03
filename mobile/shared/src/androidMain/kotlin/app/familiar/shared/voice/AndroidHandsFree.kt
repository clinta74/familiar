package app.familiar.shared.voice

import ai.onnxruntime.OnnxTensor
import ai.onnxruntime.OrtEnvironment
import ai.onnxruntime.OrtSession
import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.content.pm.PackageManager
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.media.audiofx.AcousticEchoCanceler
import android.media.audiofx.NoiseSuppressor
import java.nio.FloatBuffer
import java.nio.LongBuffer
import kotlin.concurrent.thread

/**
 * Hands-free on Android: the microphone through the voice-communication source (so the phone's echo
 * canceller takes the familiar's own voice out), read on a thread in Silero-sized frames, with the
 * Silero v5 model (the web client's, bundled as assets/vad/) deciding what's speech. [keepAlive]
 * starts and stops the foreground service that keeps the microphone open with the screen off; the
 * service calls [stoppedOutside] when its notification's Stop is pressed.
 */
class AndroidHandsFree(
    context: Context,
    /** Shows the system permission prompt for RECORD_AUDIO (and notifications, for the service's). */
    private val askPermission: suspend () -> Unit,
    /** Starts (true) or stops the foreground service. */
    private val keepAlive: (Boolean) -> Unit,
) : HandsFreeAudio {
    private val context = context.applicationContext
    private val silero by lazy { Silero(this.context) }

    private var record: AudioRecord? = null
    private var reader: Thread? = null
    private var effects: List<android.media.audiofx.AudioEffect> = emptyList()
    private var onStopped: () -> Unit = {}

    @Volatile
    private var running = false

    override fun canRecord() = context.checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED

    /** The microphone, and (Android 13+) notifications, so the service's "listening" shows. */
    override suspend fun requestAccess(): Boolean {
        val notifications = android.os.Build.VERSION.SDK_INT < 33 ||
            context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
        if (!canRecord() || !notifications) askPermission()
        return canRecord()
    }

    @SuppressLint("MissingPermission") // canRecord() is checked first
    override fun start(onFrame: (FloatArray) -> Unit, onStopped: () -> Unit) {
        stop()
        check(canRecord()) { "The microphone isn't allowed." }
        silero // loaded before the service starts, so a bad model fails here, visibly
        this.onStopped = onStopped
        val rate = SpeechDetector.SAMPLE_RATE
        val size = SpeechDetector.FRAME_SAMPLES
        val minimum = AudioRecord.getMinBufferSize(rate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
        val recorder = AudioRecord(
            MediaRecorder.AudioSource.VOICE_COMMUNICATION,
            rate,
            AudioFormat.CHANNEL_IN_MONO,
            AudioFormat.ENCODING_PCM_16BIT,
            maxOf(minimum, size * 2 * 8),
        )
        if (recorder.state != AudioRecord.STATE_INITIALIZED) {
            recorder.release()
            throw IllegalStateException("Couldn't open the microphone.")
        }
        // The source asks for these; some phones only apply them when asked directly.
        effects = listOfNotNull(
            if (AcousticEchoCanceler.isAvailable()) AcousticEchoCanceler.create(recorder.audioSessionId) else null,
            if (NoiseSuppressor.isAvailable()) NoiseSuppressor.create(recorder.audioSessionId) else null,
        ).onEach { it.enabled = true }

        keepAlive(true)
        record = recorder
        running = true
        recorder.startRecording()
        reader = thread(name = "familiar-hands-free") {
            val shorts = ShortArray(size)
            while (running) {
                var filled = 0
                while (running && filled < size) {
                    val read = recorder.read(shorts, filled, size - filled)
                    if (read < 0) break
                    filled += read
                }
                if (!running || filled < size) break
                onFrame(FloatArray(size) { shorts[it] / 32768f })
            }
        }
    }

    override fun stop() {
        val wasRunning = record != null
        running = false
        reader?.join(500)
        reader = null
        effects.forEach { it.release() }
        effects = emptyList()
        record?.let {
            runCatching { it.stop() }
            it.release()
        }
        record = null
        if (wasRunning) keepAlive(false)
    }

    /** The service's Stop: hands-free is switched off from outside the app. */
    fun stoppedOutside() {
        stop()
        onStopped()
    }

    override fun speechProbability(frame: FloatArray): Float = silero.probability(frame)

    override fun resetModel() = silero.reset()
}

/**
 * Silero VAD v5 on ONNX Runtime: 512-sample frames at 16 kHz with the previous frame's last 64
 * samples in front (as silero-vad's own wrapper and vad-web do), and its recurrent state carried
 * from frame to frame.
 */
private class Silero(context: Context) {
    private val environment = OrtEnvironment.getEnvironment()
    private val session: OrtSession = environment.createSession(
        context.assets.open("vad/silero_vad_v5.onnx").use { it.readBytes() },
        OrtSession.SessionOptions().apply { setIntraOpNumThreads(1) },
    )
    private val rate = OnnxTensor.createTensor(environment, LongBuffer.wrap(longArrayOf(SpeechDetector.SAMPLE_RATE.toLong())), longArrayOf())
    private var state = FloatArray(2 * 1 * 128)
    private var context = FloatArray(CONTEXT)

    @Synchronized
    fun probability(frame: FloatArray): Float {
        val input = FloatArray(CONTEXT + frame.size)
        context.copyInto(input)
        frame.copyInto(input, CONTEXT)
        context = frame.copyOfRange(frame.size - CONTEXT, frame.size)

        OnnxTensor.createTensor(environment, FloatBuffer.wrap(input), longArrayOf(1, input.size.toLong())).use { audio ->
            OnnxTensor.createTensor(environment, FloatBuffer.wrap(state), longArrayOf(2, 1, 128)).use { stateIn ->
                session.run(mapOf("input" to audio, "state" to stateIn, "sr" to rate)).use { out ->
                    @Suppress("UNCHECKED_CAST")
                    val next = (out.get("stateN").get().value as Array<Array<FloatArray>>)
                    state = FloatArray(2 * 128) { next[it / 128][0][it % 128] }
                    @Suppress("UNCHECKED_CAST")
                    return (out.get("output").get().value as Array<FloatArray>)[0][0]
                }
            }
        }
    }

    @Synchronized
    fun reset() {
        state = FloatArray(2 * 128)
        context = FloatArray(CONTEXT)
    }

    private companion object {
        const val CONTEXT = 64
    }
}
