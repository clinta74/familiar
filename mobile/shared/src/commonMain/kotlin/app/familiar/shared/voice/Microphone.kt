package app.familiar.shared.voice

/**
 * The phone's microphone, for push-to-talk (docs/MOBILE.md, M1): records while the button is held,
 * then hands the recording over to be sent as a WAV. Asked for the first time it's needed.
 */
interface Microphone {
    /** May record (the user allowed it). */
    fun canRecord(): Boolean

    /** Asks the user (the system prompt); true when it was allowed. */
    suspend fun requestAccess(): Boolean

    /**
     * Starts recording. [onLevel] says how loud it is, 0-1, about 20 times a second, so the screen
     * can show that it's hearing something.
     * @throws IllegalStateException when the microphone can't be opened (another app has it).
     */
    fun start(onLevel: (Double) -> Unit = {})

    /** Stops and returns what was recorded (mono 16-bit), or null if nothing was. */
    suspend fun stop(): Pcm?

    /** Stops and throws the recording away. */
    fun cancel()
}

/**
 * [pcm] as a WAV file: the RIFF header and the samples, little-endian. What the server's speech
 * recognition takes without converting (`audio/wav`).
 */
fun wav(pcm: Pcm): ByteArray {
    val dataBytes = pcm.samples.size * 2
    val out = ByteArray(44 + dataBytes)
    var at = 0
    fun text(value: String) = value.forEach { out[at++] = it.code.toByte() }
    fun int32(value: Int) = repeat(4) { out[at++] = (value shr (8 * it)).toByte() }
    fun int16(value: Int) = repeat(2) { out[at++] = (value shr (8 * it)).toByte() }

    text("RIFF")
    int32(36 + dataBytes)
    text("WAVE")
    text("fmt ")
    int32(16) // the fmt chunk's size
    int16(1) // PCM
    int16(pcm.channels)
    int32(pcm.sampleRate)
    int32(pcm.sampleRate * pcm.channels * 2) // bytes a second
    int16(pcm.channels * 2) // bytes a frame
    int16(16) // bits a sample
    text("data")
    int32(dataBytes)
    for (sample in pcm.samples) int16(sample.toInt())
    return out
}
