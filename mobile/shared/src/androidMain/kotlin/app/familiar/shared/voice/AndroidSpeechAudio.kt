package app.familiar.shared.voice

import android.content.Context
import android.media.AudioAttributes
import android.media.AudioFocusRequest
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioTrack
import android.media.MediaCodec
import android.media.MediaDataSource
import android.media.MediaExtractor
import android.media.MediaFormat
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.nio.ByteOrder
import kotlin.math.min
import kotlin.math.sqrt

/**
 * Speech through an [AudioTrack] fed with PCM decoded by [MediaCodec]: one track for a run of
 * sentences, so they play back to back, and the loudness is measured from the samples at the
 * playback head - no microphone permission, as a Visualizer would need.
 */
class AndroidSpeechAudio(context: Context) : SpeechAudio {
    private val audioManager = context.getSystemService(AudioManager::class.java)
    private val attributes = AudioAttributes.Builder()
        .setUsage(AudioAttributes.USAGE_ASSISTANT)
        .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
        .build()
    private val focusRequest = AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN_TRANSIENT_MAY_DUCK)
        .setAudioAttributes(attributes)
        .build()

    private var track: AudioTrack? = null
    private var trackRate = 0
    private var trackChannels = 0

    /** Frames handed to the track since it was made: where the next clip starts on its clock. */
    private var written = 0L

    override suspend fun decode(bytes: ByteArray): Pcm? = withContext(Dispatchers.Default) {
        runCatching { decodeNow(bytes) }.getOrNull()
    }

    override suspend fun play(pcm: Pcm, onFrame: (seconds: Double, level: Double) -> Unit) = coroutineScope {
        val output = trackFor(pcm.sampleRate, pcm.channels)
        val frames = pcm.samples.size / pcm.channels
        val startsAt = written
        written += frames

        launch(Dispatchers.IO) {
            var offset = 0
            while (offset < pcm.samples.size && isActive) {
                val count = output.write(pcm.samples, offset, min(4096, pcm.samples.size - offset))
                if (count <= 0) break
                offset += count
            }
        }
        output.play()

        // The playback head, on the caller's thread: how far in, and how loud it is there.
        val window = pcm.sampleRate / 30 * pcm.channels
        while (true) {
            val head = (output.playbackHeadPosition.toLong() and 0xFFFFFFFFL) - startsAt
            if (head >= frames || output.playState != AudioTrack.PLAYSTATE_PLAYING) break
            if (head >= 0) onFrame(head.toDouble() / pcm.sampleRate, level(pcm.samples, (head * pcm.channels).toInt(), window))
            delay(33)
        }
    }

    override fun stop() {
        track?.let {
            runCatching { it.pause() }
            runCatching { it.flush() }
            it.release()
        }
        track = null
        written = 0
    }

    override fun focus(speaking: Boolean) {
        if (speaking) audioManager.requestAudioFocus(focusRequest) else audioManager.abandonAudioFocusRequest(focusRequest)
    }

    private fun trackFor(rate: Int, channels: Int): AudioTrack {
        track?.let { if (trackRate == rate && trackChannels == channels) return it }
        stop()
        val mask = if (channels == 1) AudioFormat.CHANNEL_OUT_MONO else AudioFormat.CHANNEL_OUT_STEREO
        val format = AudioFormat.Builder().setEncoding(AudioFormat.ENCODING_PCM_16BIT).setSampleRate(rate).setChannelMask(mask).build()
        val buffer = AudioTrack.getMinBufferSize(rate, mask, AudioFormat.ENCODING_PCM_16BIT) * 2
        return AudioTrack.Builder()
            .setAudioAttributes(attributes)
            .setAudioFormat(format)
            .setBufferSizeInBytes(buffer)
            .setTransferMode(AudioTrack.MODE_STREAM)
            .build()
            .also {
                track = it
                trackRate = rate
                trackChannels = channels
            }
    }

    private companion object {
        /** RMS of the samples around [at], scaled as the web client scales its analyser's. */
        fun level(samples: ShortArray, at: Int, window: Int): Double {
            val from = at.coerceIn(0, samples.size)
            val to = min(samples.size, from + window)
            if (to <= from) return 0.0
            var sum = 0.0
            for (i in from until to) {
                val s = samples[i] / 32768.0
                sum += s * s
            }
            return min(1.0, sqrt(sum / (to - from)) * 5)
        }

        fun decodeNow(bytes: ByteArray): Pcm {
            val extractor = MediaExtractor()
            extractor.setDataSource(object : MediaDataSource() {
                override fun readAt(position: Long, buffer: ByteArray, offset: Int, size: Int): Int {
                    if (position >= bytes.size) return -1
                    val count = min(size.toLong(), bytes.size - position).toInt()
                    System.arraycopy(bytes, position.toInt(), buffer, offset, count)
                    return count
                }

                override fun getSize() = bytes.size.toLong()

                override fun close() = Unit
            })

            val trackIndex = (0 until extractor.trackCount).first {
                extractor.getTrackFormat(it).getString(MediaFormat.KEY_MIME)?.startsWith("audio/") == true
            }
            extractor.selectTrack(trackIndex)
            val format = extractor.getTrackFormat(trackIndex)
            var rate = format.getInteger(MediaFormat.KEY_SAMPLE_RATE)
            var channels = format.getInteger(MediaFormat.KEY_CHANNEL_COUNT)

            val codec = MediaCodec.createDecoderByType(format.getString(MediaFormat.KEY_MIME)!!)
            val out = ShortArrayBuilder()
            try {
                codec.configure(format, null, null, 0)
                codec.start()
                val info = MediaCodec.BufferInfo()
                var inputDone = false
                while (true) {
                    if (!inputDone) {
                        val index = codec.dequeueInputBuffer(10_000)
                        if (index >= 0) {
                            val size = extractor.readSampleData(codec.getInputBuffer(index)!!, 0)
                            if (size < 0) {
                                codec.queueInputBuffer(index, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM)
                                inputDone = true
                            } else {
                                codec.queueInputBuffer(index, 0, size, extractor.sampleTime, 0)
                                extractor.advance()
                            }
                        }
                    }

                    val index = codec.dequeueOutputBuffer(info, 10_000)
                    if (index == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                        rate = codec.outputFormat.getInteger(MediaFormat.KEY_SAMPLE_RATE)
                        channels = codec.outputFormat.getInteger(MediaFormat.KEY_CHANNEL_COUNT)
                    } else if (index >= 0) {
                        val buffer = codec.getOutputBuffer(index)!!
                        buffer.position(info.offset)
                        buffer.limit(info.offset + info.size)
                        out.append(buffer.order(ByteOrder.nativeOrder()).asShortBuffer())
                        codec.releaseOutputBuffer(index, false)
                        if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) break
                    }
                }
            } finally {
                runCatching { codec.stop() }
                codec.release()
                extractor.release()
            }
            return Pcm(out.toArray(), rate, channels)
        }
    }
}

/** A growable ShortArray, for decoder output of unknown length. */
private class ShortArrayBuilder {
    private var data = ShortArray(48_000)
    private var size = 0

    fun append(source: java.nio.ShortBuffer) {
        val count = source.remaining()
        if (size + count > data.size) data = data.copyOf(maxOf(data.size * 2, size + count))
        source.get(data, size, count)
        size += count
    }

    fun toArray(): ShortArray = data.copyOf(size)
}
