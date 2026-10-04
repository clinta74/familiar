package app.familiar.shared.session

import app.familiar.shared.api.FamiliarApi
import app.familiar.shared.auth.SignedIn
import app.familiar.shared.avatar.AvatarController
import app.familiar.shared.conversation.ConversationController
import app.familiar.shared.platform.DeviceAlarms
import app.familiar.shared.platform.DeviceCalendar
import app.familiar.shared.platform.SecureStore
import app.familiar.shared.protocol.ServerEvent
import app.familiar.shared.voice.HandsFree
import app.familiar.shared.voice.HandsFreeAudio
import app.familiar.shared.voice.Microphone
import app.familiar.shared.voice.SpeechAudio
import app.familiar.shared.voice.SpeechPlayer
import app.familiar.shared.voice.wav
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import app.familiar.shared.protocol.AppearanceView
import app.familiar.shared.protocol.FamiliarJson
import app.familiar.shared.protocol.FamiliarView
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonObject

/**
 * One signed-in device's familiar: the conversation stream, the voice, the avatar's state, and the
 * microphone (push-to-talk and hands-free). It belongs to the app rather than the screen, so a
 * hands-free conversation carries on with the screen off or the activity recreated
 * ([FamiliarSessions] keeps it); the chat screen only shows it.
 */
class FamiliarSession(
    val signedIn: SignedIn,
    private val store: SecureStore,
    val calendar: DeviceCalendar?,
    private val speechAudio: SpeechAudio?,
    val microphone: Microphone?,
    handsFreeAudio: HandsFreeAudio?,
    /** The phone's alarms, kept in step with the timers and reminders. */
    private val alarms: DeviceAlarms? = null,
) {
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    val api = FamiliarApi(signedIn.server, signedIn.token)

    private val _ended = MutableStateFlow(false)

    /**
     * Signed out - here, or elsewhere (the token was revoked). The app goes back to signing in;
     * whichever screen is showing then, since this outlives screens.
     */
    val ended: StateFlow<Boolean> = _ended.asStateFlow()

    val controller = ConversationController(api, scope, { _ended.value = true }, calendar)
    val avatar = AvatarController().also { avatar ->
        store.read(lookKey(signedIn.username))
            ?.let { runCatching { Json.parseToJsonElement(it) as? JsonObject }.getOrNull() }
            ?.let(avatar::look)
    }

    private val _voiceOn = MutableStateFlow(store.read(VOICE_KEY) != "off")

    /** Replies are spoken (and there's a speaker to speak them). */
    val voiceOn: StateFlow<Boolean> = _voiceOn.asStateFlow()

    private val _audible = MutableStateFlow(false)

    /** A reply is playing aloud right now. */
    val audible: StateFlow<Boolean> = _audible.asStateFlow()

    val player: SpeechPlayer? = speechAudio?.let { audio ->
        SpeechPlayer(audio, scope) { url -> api.clip(url) }.apply {
            // With word timings the page makes the words' shapes; without, the mouth follows loudness.
            onClip = { words -> avatar.clip(words, 0) }
            onLevel = avatar::level
            onPlayingChange = { playing ->
                _audible.value = playing
                handsFree?.familiarSpeaking(playing)
                if (!playing) avatar.silence()
            }
        }
    }

    private val _recording = MutableStateFlow(false)

    /** Push-to-talk: the button is held and the microphone is recording. */
    val recording: StateFlow<Boolean> = _recording.asStateFlow()

    private val _micLevel = MutableStateFlow(0.0)

    /** How loud push-to-talk hears it, 0-1. */
    val micLevel: StateFlow<Double> = _micLevel.asStateFlow()

    val handsFree: HandsFree? = handsFreeAudio?.let { HandsFree(it, scope) }

    private val _handsFreeOn = MutableStateFlow(false)

    /** Hands-free is listening. */
    val handsFreeOn: StateFlow<Boolean> = _handsFreeOn.asStateFlow()

    private val _userSpeaking = MutableStateFlow(false)

    /** Hands-free hears the user talking. */
    val userSpeaking: StateFlow<Boolean> = _userSpeaking.asStateFlow()

    private var started = false

    /** Listening for one thing said (opened as the assistant), not hands-free until switched off. */
    private var once = false

    init {
        controller.speak = _voiceOn.value && player != null
        controller.onEvent = { event ->
            avatar.handle(event, audible = player?.isPlaying == true)
            when (event) {
                is ServerEvent.Ready -> scope.launch { refreshLook() }
                is ServerEvent.Jobs -> alarms?.sync(event.jobs, controller.state.value.familiarName)
                is ServerEvent.Audio -> if (controller.speak) player?.enqueue(event.clip)
                is ServerEvent.ReplyStart, ServerEvent.Cleared -> player?.stop()
                else -> Unit
            }
        }
        handsFree?.apply {
            onSpeechStart = {
                _userSpeaking.value = true
                if (busy) stopReply()
            }
            onUtterance = { utterance ->
                _userSpeaking.value = false
                controller.sendSpoken(utterance)
                if (once) {
                    once = false
                    scope.launch { setHandsFree(false) }
                }
            }
            onMisfire = { _userSpeaking.value = false }
            onStopped = {
                _handsFreeOn.value = false
                _userSpeaking.value = false
            }
        }
    }

    /** Connects (once); the look is read as soon as the stream is ready. */
    fun start() {
        if (started) return
        started = true
        controller.start()
        scope.launch { refreshLook() }
        // The stream sends the list when it changes; this is the list as it is now.
        if (alarms != null) {
            scope.launch {
                runCatching { api.jobs() }.getOrNull()?.let { alarms.sync(it, controller.state.value.familiarName) }
            }
        }
    }

    /** The familiar is thinking or talking. */
    val busy: Boolean
        get() = controller.state.value.state.let { it == "thinking" || it == "speaking" } || _audible.value

    fun setVoice(on: Boolean) {
        _voiceOn.value = on
        controller.speak = on && player != null
        store.write(VOICE_KEY, if (on) null else "off")
        if (!on) player?.stop()
    }

    /** Stop talking, and tell the server how much was heard so the stored reply matches. */
    fun stopReply() {
        val heard = player?.heard()
        player?.stop()
        controller.interrupt(heard)
    }

    /**
     * The mic button went down: talking over the familiar stops it (and keeps what was heard), as
     * on the web. The first time, it asks for the microphone instead of recording.
     */
    suspend fun startTalking(): Boolean {
        val mic = microphone ?: return false
        if (_handsFreeOn.value) return false
        if (!mic.canRecord()) {
            if (!mic.requestAccess()) controller.showError(needsMicrophone())
            return false
        }
        if (busy) stopReply()
        return try {
            mic.start { level -> _micLevel.value = level }
            controller.showError(null)
            _recording.value = true
            true
        } catch (exception: IllegalStateException) {
            controller.showError(exception.message ?: "Couldn't open the microphone.")
            false
        }
    }

    /** Let go: send what was said, unless it was too short to be anything (a tap). */
    suspend fun stopTalking(send: Boolean) {
        val mic = microphone ?: return
        _recording.value = false
        _micLevel.value = 0.0
        if (!send) {
            mic.cancel()
            return
        }
        val pcm = mic.stop() ?: return
        if (pcm.seconds < MIN_SPOKEN_SECONDS) {
            controller.showError("Hold the microphone button while you talk, then let go.")
            return
        }
        controller.sendSpoken(wav(pcm))
    }

    /**
     * Hands-free on or off. On asks for the microphone the first time, and keeps the app listening
     * with the screen off until it's turned off here or from its notification.
     */
    suspend fun setHandsFree(on: Boolean) {
        val listener = handsFree ?: return
        if (!on) {
            once = false
            listener.stop()
            _handsFreeOn.value = false
            _userSpeaking.value = false
            return
        }
        if (listener.active) return
        if (!listener.requestAccess()) {
            controller.showError(needsMicrophone())
            return
        }
        try {
            listener.familiarSpeaking(_audible.value)
            listener.start()
            controller.showError(null)
            _handsFreeOn.value = true
        } catch (exception: IllegalStateException) {
            controller.showError(exception.message ?: "Hands-free couldn't start.")
        }
    }

    /**
     * Opened as the phone's assistant (long-press home, the Quick Settings tile, the shortcut): listen
     * for one thing said, as an assistant does, then stop - or stop after [ONCE_GIVE_UP_MS] of nothing.
     * Hands-free that's already on stays on.
     */
    fun listenOnce() {
        if (_handsFreeOn.value || handsFree == null) return
        scope.launch {
            once = true
            setHandsFree(true)
            if (!_handsFreeOn.value) {
                once = false
                return@launch
            }
            delay(ONCE_GIVE_UP_MS)
            if (once && !_userSpeaking.value) {
                once = false
                setHandsFree(false)
            }
        }
    }

    /** Saved in Settings or the studio: the chat's name and avatar follow at once. */
    fun profileSaved(profile: FamiliarView) {
        controller.renamed(profile.name)
        profile.appearance?.let { appearance ->
            val look = FamiliarJson.encodeToJsonElement(AppearanceView.serializer(), appearance).jsonObject
            avatar.look(look)
            store.write(lookKey(signedIn.username), look.toString())
        }
    }

    /**
     * Plays a sample of [voice] at [speed] through the phone's speaker, cutting off anything the
     * familiar is saying. False when there's no speaker or the sample couldn't be played.
     */
    suspend fun previewVoice(voice: String, speed: Double): Boolean {
        val audio = speechAudio ?: return false
        val pcm = runCatching { audio.decode(api.voicePreview(voice, speed)) }.getOrNull() ?: return false
        player?.stop()
        audio.focus(true)
        try {
            audio.play(pcm) { _, _ -> }
        } finally {
            audio.focus(false)
        }
        return true
    }

    fun signOut() {
        scope.launch {
            runCatching { if (signedIn.deviceId.isNotEmpty()) api.revokeDevice(signedIn.deviceId) }
            _ended.value = true
        }
    }

    /** Disconnects and lets go of everything: signed out, or the app is going away. */
    fun close() {
        handsFree?.stop()
        controller.stop()
        player?.stop()
        microphone?.cancel()
        scope.cancel()
    }

    // How it looks is the studio's business (on the web); here it's read, and again on each
    // reconnect so a change made there shows up. It's remembered for next time; if it can't be read
    // and there's nothing remembered, the default.
    private suspend fun refreshLook() {
        val appearance = runCatching { api.familiar()["appearance"] as? JsonObject }.getOrNull()
        when {
            appearance != null -> {
                avatar.look(appearance)
                store.write(lookKey(signedIn.username), appearance.toString())
            }
            !avatar.hasLook -> avatar.look(JsonObject(emptyMap()))
        }
    }

    private fun needsMicrophone() =
        "${controller.state.value.familiarName} needs the microphone to hear you. You can allow it in the app's settings."

    private companion object {
        /** "off" when the user turned replies' voice off; on by default. */
        const val VOICE_KEY = "voice"

        /** How long listening once waits for the user to start talking. */
        const val ONCE_GIVE_UP_MS = 12_000L

        /** Shorter than this is a tap on the mic button, not something said. */
        const val MIN_SPOKEN_SECONDS = 0.35

        /** How the familiar looked last time, per account: the avatar starts as that, not a stand-in. */
        fun lookKey(username: String) = "look:$username"
    }
}

/**
 * The app's one session, kept for as long as the same device is signed in: the screen can be
 * recreated (rotation, unfolding, coming back from the background) without dropping the stream, and
 * the hands-free service can reach it.
 */
object FamiliarSessions {
    var current: FamiliarSession? = null
        private set

    /** The session for [signedIn], made with [make] if there isn't one for it already. */
    fun open(signedIn: SignedIn, make: () -> FamiliarSession): FamiliarSession {
        current?.let { if (it.signedIn == signedIn) return it }
        current?.close()
        return make().also { current = it }
    }

    /** Signed out: the session ends. */
    fun close() {
        current?.close()
        current = null
    }
}
