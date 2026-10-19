package app.familiar.shared.session

import app.familiar.shared.api.FamiliarApi
import app.familiar.shared.auth.SignedIn
import app.familiar.shared.avatar.AvatarController
import app.familiar.shared.conversation.Connection
import app.familiar.shared.conversation.ConversationController
import app.familiar.shared.platform.DeviceAlarms
import app.familiar.shared.phone.PhoneActions
import app.familiar.shared.platform.DeviceCalendar
import app.familiar.shared.platform.DevicePhone
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
import kotlin.time.Clock
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds
import kotlin.time.Instant

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
    /** Its contacts, messages, dialler, maps and location, for the familiar to hand off to. */
    val phone: DevicePhone? = null,
) {
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    val api = FamiliarApi(signedIn.server, signedIn.token)

    private val _ended = MutableStateFlow(false)

    /**
     * Signed out - here, or elsewhere (the token was revoked). The app goes back to signing in;
     * whichever screen is showing then, since this outlives screens.
     */
    val ended: StateFlow<Boolean> = _ended.asStateFlow()

    val controller = ConversationController(
        api, scope, { _ended.value = true }, calendar,
        phone = phone?.let(::PhoneActions),
        deviceId = signedIn.deviceId.ifEmpty { null },
    )
    val avatar = AvatarController().also { avatar ->
        store.read(lookKey(signedIn.username))
            ?.let { runCatching { Json.parseToJsonElement(it) as? JsonObject }.getOrNull() }
            ?.let(avatar::look)
    }

    private val _voiceOn = MutableStateFlow(store.read(VOICE_KEY) != "off")

    /** Replies are spoken (and there's a speaker to speak them). */
    val voiceOn: StateFlow<Boolean> = _voiceOn.asStateFlow()

    private val _effectsOn = MutableStateFlow(store.read(EFFECTS_KEY) != "off")

    /** Sound effects (a pop, a fanfare) play, besides the voice. */
    val effectsOn: StateFlow<Boolean> = _effectsOn.asStateFlow()

    private val _audible = MutableStateFlow(false)

    /** A reply is playing aloud right now. */
    val audible: StateFlow<Boolean> = _audible.asStateFlow()

    val player: SpeechPlayer? = speechAudio?.let { audio ->
        SpeechPlayer(audio, scope) { url -> api.clip(url) }.apply {
            // With word timings the page makes the words' shapes; without, the mouth follows loudness.
            onClip = { words -> avatar.clip(words, 0) }
            onLevel = avatar::level
            onPlayingChange = { playing ->
                if (!playing && _audible.value) quietSince = Clock.System.now()
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

    /** When the familiar last stopped talking aloud. */
    private var quietSince: Instant? = null

    /** Whether the utterance being heard began while the familiar was talking, or just after. */
    private var speechOverReply = false

    /** When the app went into the background; null while it's in front. */
    private var awaySince: Instant? = null
    private val notices = AwayNotices()

    /** Notifications shown while away, taken down on coming back. */
    private val shownAway = mutableListOf<String>()

    /** Listening for one thing said (opened as the assistant), not hands-free until switched off. */
    private var once = false

    init {
        // Hands-free keeps the app running in the background, and needs the stream; otherwise there's
        // no point reconnecting until the app's back in front.
        controller.holdWhileAway = { awaySince != null && !_handsFreeOn.value }
        controller.speak = _voiceOn.value && player != null
        controller.onEvent = { event ->
            avatar.handle(event, audible = player?.isPlaying == true)
            when (event) {
                is ServerEvent.Ready -> scope.launch { refreshLook() }
                is ServerEvent.Jobs -> alarms?.sync(event.jobs, controller.state.value.familiarName)
                is ServerEvent.Audio -> if (controller.speak) player?.enqueue(event.clip)
                is ServerEvent.Sound -> if (_effectsOn.value) avatar.sound(event.sound)
                is ServerEvent.ReplyStart, ServerEvent.Cleared -> player?.stop()
                else -> Unit
            }
            // In the background, what it says comes up as a notification - unless they're talking to
            // it hands-free, when they hear it.
            val notice = notices.on(event, controller.state.value.familiarName)
            if (notice != null && awaySince != null && !_handsFreeOn.value) {
                alarms?.message(notice.id, notice.title, notice.body)
                if (notice.id !in shownAway) shownAway += notice.id
            }
        }
        handsFree?.apply {
            onSpeechStart = {
                _userSpeaking.value = true
                // Before the reply's stopped: whether this began over its voice (or its last words).
                speechOverReply = _audible.value || quietSince?.let { Clock.System.now() - it < ECHO_TAIL } == true
                if (busy) stopReply()
            }
            onUtterance = { utterance ->
                _userSpeaking.value = false
                controller.sendSpoken(utterance, overReply = speechOverReply)
                if (once) {
                    once = false
                    scope.launch { setHandsFree(false) }
                }
            }
            onMisfire = { _userSpeaking.value = false }
            onStopped = {
                _handsFreeOn.value = false
                _userSpeaking.value = false
                if (awaySince != null) letGoLater()
            }
        }
    }

    /**
     * The app went into the background ([visible] false) or came back. Back after more than a few
     * seconds - or with the connection down - it checks the stream: if it's gone quiet it reconnects
     * at once, since it may have died without anything having noticed; if it's still hearing the
     * server, it carries on ([ConversationController.resume]).
     */
    fun foreground(visible: Boolean) {
        if (!visible) {
            if (awaySince == null) awaySince = Clock.System.now()
            letGoLater()
            return
        }
        letGo?.cancel()
        letGo = null
        val away = awaySince ?: return
        awaySince = null
        shownAway.forEach { alarms?.dismissMessage(it) }
        shownAway.clear()
        if (Clock.System.now() - away > 5.seconds || controller.state.value.connection != Connection.Open) controller.resume()
    }

    private var letGo: kotlinx.coroutines.Job? = null

    /**
     * Away a while and not talking hands-free, the conversation stream goes - once any reply's done -
     * and comes back with the app. The phone's push connection keeps the app running in the
     * background now (BACKLOG item 38), so otherwise this stream would ping every 15 s all day; the
     * push connection is what hears of things meanwhile.
     */
    private fun letGoLater() {
        letGo?.cancel()
        letGo = scope.launch {
            delay(LET_GO_AFTER)
            while (busy) delay(5.seconds)
            if (awaySince != null && !_handsFreeOn.value) controller.letGo()
        }
    }

    /**
     * Switches the phone sending texts itself on or off (BACKLOG item 39), and reconnects so the
     * server knows; whether it's on now (it asks for the permission, which may be refused).
     */
    suspend fun sendTexts(on: Boolean): Boolean {
        val now = phone?.sendTexts(on) ?: false
        controller.reconnect()
        return now
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

    fun setEffects(on: Boolean) {
        _effectsOn.value = on
        store.write(EFFECTS_KEY, if (on) null else "off")
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

        /** "off" when the user turned sound effects off; on by default. */
        const val EFFECTS_KEY = "effects"

        /** How long listening once waits for the user to start talking. */
        const val ONCE_GIVE_UP_MS = 12_000L

        /** Shorter than this is a tap on the mic button, not something said. */
        const val MIN_SPOKEN_SECONDS = 0.35

        /** How long after the familiar stops talking an utterance can still be its own voice (the speaker's tail). */
        val ECHO_TAIL = 2.seconds

        /** Long enough to come back from a glance at another app, still connected. */
        val LET_GO_AFTER = 2.minutes

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
