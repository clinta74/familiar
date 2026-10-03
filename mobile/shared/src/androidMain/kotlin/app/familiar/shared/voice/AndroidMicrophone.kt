package app.familiar.shared.voice

import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.content.pm.PackageManager
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import kotlin.concurrent.thread
import kotlin.math.sqrt
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/**
 * The microphone through [AudioRecord]: 16 kHz mono 16-bit, what speech recognition wants, from the
 * voice-recognition source (tuned for speech, without the call processing that thins a voice).
 * A thread reads it into a growing buffer while the button is held; at most [MAX_SECONDS].
 */
class AndroidMicrophone(
    context: Context,
    /** Shows the system permission prompt for RECORD_AUDIO. */
    private val askPermission: suspend () -> Unit,
) : Microphone {
    private val context = context.applicationContext

    private var record: AudioRecord? = null
    private var reader: Thread? = null
    private var buffer = ShortArray(0)
    private var length = 0

    @Volatile
    private var running = false

    override fun canRecord() = context.checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED

    override suspend fun requestAccess(): Boolean {
        if (!canRecord()) askPermission()
        return canRecord()
    }

    @SuppressLint("MissingPermission") // canRecord() is checked first
    override fun start(onLevel: (Double) -> Unit) {
        cancel()
        check(canRecord()) { "The microphone isn't allowed." }
        val minimum = AudioRecord.getMinBufferSize(RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
        val recorder = AudioRecord(
            MediaRecorder.AudioSource.VOICE_RECOGNITION,
            RATE,
            AudioFormat.CHANNEL_IN_MONO,
            AudioFormat.ENCODING_PCM_16BIT,
            maxOf(minimum, RATE / 5 * 2),
        )
        if (recorder.state != AudioRecord.STATE_INITIALIZED) {
            recorder.release()
            throw IllegalStateException("Couldn't open the microphone.")
        }

        buffer = ShortArray(RATE * 4)
        length = 0
        running = true
        record = recorder
        recorder.startRecording()
        reader = thread(name = "familiar-mic") {
            // Reads a twentieth of a second at a time: that's how often the level is reported.
            val chunk = ShortArray(RATE / 20)
            while (running) {
                val read = recorder.read(chunk, 0, chunk.size)
                if (read <= 0) continue
                synchronized(this) {
                    if (length + read > RATE * MAX_SECONDS) {
                        running = false
                        return@synchronized
                    }
                    if (length + read > buffer.size) buffer = buffer.copyOf(maxOf(buffer.size * 2, length + read))
                    chunk.copyInto(buffer, length, 0, read)
                    length += read
                }
                var sum = 0.0
                for (i in 0 until read) sum += chunk[i].toDouble() * chunk[i]
                // Speech sits around a tenth of full scale; scaled so talking reads as most of the way.
                onLevel((sqrt(sum / read) / 32768.0 * 6).coerceIn(0.0, 1.0))
            }
        }
    }

    override suspend fun stop(): Pcm? = withContext(Dispatchers.IO) {
        if (record == null) return@withContext null
        finish()
        val samples = synchronized(this@AndroidMicrophone) { buffer.copyOf(length) }
        buffer = ShortArray(0)
        length = 0
        if (samples.isEmpty()) null else Pcm(samples, RATE, 1)
    }

    override fun cancel() {
        finish()
        buffer = ShortArray(0)
        length = 0
    }

    private fun finish() {
        running = false
        reader?.join(500)
        reader = null
        record?.let {
            runCatching { it.stop() }
            it.release()
        }
        record = null
    }

    private companion object {
        const val RATE = 16_000
        const val MAX_SECONDS = 60
    }
}
