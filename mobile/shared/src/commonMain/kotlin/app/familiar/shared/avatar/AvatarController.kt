package app.familiar.shared.avatar

import app.familiar.shared.protocol.ServerEvent
import app.familiar.shared.protocol.SpokenWord
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive

/**
 * Drives the avatar page (client/avatar.html, bundled into the app): each call becomes one
 * `familiarAvatar.*(…)` for the WebView to run (docs/MOBILE.md §1.1). The page only draws; what it
 * shows comes from here. The look and the state are remembered, so a page that (re)loads catches up.
 */
class AvatarController {
    private val _calls = MutableSharedFlow<String>(extraBufferCapacity = 256, onBufferOverflow = BufferOverflow.DROP_OLDEST)

    /** JavaScript to run in the page, in order. */
    val calls: SharedFlow<String> = _calls.asSharedFlow()

    private var look: JsonObject? = null
    private var state = "idle"

    /** Whether it has been told how it looks yet (until then the page draws nothing). */
    val hasLook: Boolean get() = look != null

    /** What a freshly loaded page needs: the look, then the state. */
    fun snapshot(): List<String> = listOfNotNull(look?.let { call("look", it) }, call("state", JsonPrimitive(state)))

    /** The familiar profile's `appearance`. */
    fun look(appearance: JsonObject) {
        if (appearance == look) return
        look = appearance
        emit(call("look", appearance))
    }

    /** idle, listening, thinking, speaking or resting. */
    fun state(value: String) {
        if (value == state) return
        state = value
        emit(call("state", JsonPrimitive(value)))
    }

    fun expression(emotion: String, intensity: Double) = emit(call("expression", JsonPrimitive(emotion), JsonPrimitive(intensity)))

    fun gesture(gesture: String) = emit(call("gesture", JsonPrimitive(gesture)))

    /** A piece of reply text with no audio playing: the mouth moves with the text. */
    fun text(fragment: String) = emit(call("text", JsonPrimitive(fragment)))

    /** A clip began playing [elapsedMillis] ago; with word timings the page shapes the mouth itself. */
    fun clip(words: List<SpokenWord>?, elapsedMillis: Long) {
        val timings = words?.let { list ->
            JsonArray(list.map { JsonObject(mapOf("word" to JsonPrimitive(it.word), "start" to JsonPrimitive(it.start), "end" to JsonPrimitive(it.end))) })
        }
        emit(call("clip", timings ?: JsonPrimitive(null as String?), JsonPrimitive(elapsedMillis)))
    }

    /** The voice's loudness, 0-1, while a clip plays (~30 times a second). */
    fun level(level: Double) = emit(call("level", JsonPrimitive(level)))

    fun silence() = emit(call("silence"))

    /**
     * The stream's moments: expressions, gestures, and - when nothing is being played aloud - the
     * reply text moving the mouth. State is set separately, since it also depends on the phone
     * (listening, playing audio).
     */
    fun handle(event: ServerEvent, audible: Boolean = false) {
        when (event) {
            is ServerEvent.Expression -> expression(event.emotion, event.intensity)
            is ServerEvent.Gesture -> gesture(event.gesture)
            is ServerEvent.ReplyDelta -> if (!audible) text(event.text)
            else -> Unit
        }
    }

    private fun emit(js: String) {
        _calls.tryEmit(js)
    }

    private fun call(name: String, vararg args: JsonElement) = "familiarAvatar.$name(${args.joinToString(",")})"
}
