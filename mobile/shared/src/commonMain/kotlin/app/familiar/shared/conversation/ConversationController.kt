package app.familiar.shared.conversation

import app.familiar.shared.api.ApiException
import app.familiar.shared.api.FamiliarApi
import app.familiar.shared.phone.PhoneActions
import app.familiar.shared.platform.DeviceCalendar
import app.familiar.shared.platform.currentTimeZoneId
import app.familiar.shared.protocol.Capabilities
import app.familiar.shared.protocol.InterruptRequest
import app.familiar.shared.protocol.ServerEvent
import app.familiar.shared.protocol.ToolInvokeEvent
import app.familiar.shared.protocol.ToolResultRequest
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put
import kotlin.time.Clock
import kotlin.time.Duration.Companion.days
import kotlin.time.Duration.Companion.hours
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds
import kotlin.time.Instant

/**
 * The live conversation for one signed-in device: history first, then the event stream, kept
 * connected - on a drop it reconnects with Last-Event-ID so the server replays what was missed.
 *
 * With the phone's [calendar] allowed, it also keeps the server's copy of it fresh and adds events
 * to it when the familiar asks (`tool.invoke`, docs/MOBILE.md §2).
 */
class ConversationController(
    private val api: FamiliarApi,
    private val scope: CoroutineScope,
    /** Called when the server no longer accepts this device's token: it was signed out elsewhere. */
    private val onSignedOut: () -> Unit,
    private val calendar: DeviceCalendar? = null,
    private val clock: Clock = Clock.System,
    /** Texts, calls, directions and where it is, when the phone can (BACKLOG item 21). */
    private val phone: PhoneActions? = null,
    /** This phone, for the `tool.invoke`s meant for it. */
    private val deviceId: String? = null,
) {
    /** Replies are spoken (the server makes `audio` clips): the voice is on and there's a speaker. */
    var speak = false

    private val _state = MutableStateFlow(ConversationState())
    val state: StateFlow<ConversationState> = _state.asStateFlow()

    /** Expressions and gestures, for the avatar - moments rather than state. */
    var onEvent: (ServerEvent) -> Unit = {}

    private val _calendarOn = MutableStateFlow(calendar?.canRead() == true)

    /** Whether the phone's calendar is shared with the familiar. */
    val calendarOn: StateFlow<Boolean> = _calendarOn.asStateFlow()

    private var stream: Job? = null

    /** The connection being made or held now; cancelling just this is a reconnect. */
    private var connection: Job? = null

    /** Ask for a reconnect straight away, skipping any wait between attempts. */
    private val kicks = Channel<Unit>(Channel.CONFLATED)

    /** The current attempt was ended on purpose ([reconnect]): straight on to the next, quietly. */
    private var kicked = false

    /** When the stream last said anything, a ping included. */
    private var lastHeard: Instant? = null

    /**
     * Whether to hold off reconnecting until [resume]: the app is in the background, where Android
     * soon cuts its network - an attempt then hangs, and is a stray connection when it's let go.
     */
    var holdWhileAway: () -> Boolean = { false }
    private var calendarSync: Job? = null
    private var lastEventId: String? = null

    /** This connection's id from `ready`: invokes for an older connection are not ours to answer. */
    private var connectionId: String? = null
    private val answered = ArrayDeque<String>()

    fun start() {
        if (stream != null) return
        stream = scope.launch {
            loadHistory()
            var backoff = 1_000L
            while (true) {
                kicks.tryReceive()
                // Each attempt runs on its own, so a reconnect can cancel it without ending the loop.
                val ended = CompletableDeferred<Exception?>()
                val attempt = scope.launch {
                    // A reconnect on purpose doesn't say "reconnecting…" unless it's taking a while.
                    val slow = launch {
                        delay(QUIET_RECONNECT)
                        _state.update { if (it.connection == Connection.Open) it.copy(connection = Connection.Reconnecting) else it }
                    }
                    // The server says "ready" as soon as a connection opens. An attempt that's heard
                    // nothing at all by now is stuck - after the phone's been locked, on a connection
                    // the network has silently dropped - and would wait out the 45-second read
                    // timeout: give it up and make a fresh one.
                    val self = coroutineContext.job
                    val stuck = launch {
                        delay(STUCK)
                        if (lastHeard == null) self.cancel()
                    }
                    ended.complete(
                        try {
                            connectionId = null
                            lastHeard = null
                            api.events(currentTimeZoneId(), lastEventId, capabilities(), speak).collect { (id, event) ->
                                if (id != null) lastEventId = id
                                lastHeard = clock.now()
                                backoff = 1_000L
                                if (event is ServerEvent.Ready || event is ServerEvent.Heartbeat) {
                                    slow.cancel()
                                    stuck.cancel()
                                }
                                when (event) {
                                    ServerEvent.Heartbeat -> return@collect
                                    ServerEvent.Resync -> loadHistory()
                                    is ServerEvent.ToolInvoke -> answer(event.event)
                                    is ServerEvent.Ready -> {
                                        connectionId = event.ready.connectionId
                                        _state.update { ConversationReducer.apply(it, event) }
                                    }
                                    else -> _state.update { ConversationReducer.apply(it, event) }
                                }
                                onEvent(event)
                            }
                            null
                        } catch (_: CancellationException) {
                            null
                        } catch (exception: Exception) {
                            // Unreachable or dropped: try again shortly.
                            exception
                        },
                    )
                }
                connection = attempt
                val failure = try {
                    ended.await()
                } finally {
                    attempt.cancel()
                }
                if (failure is ApiException && failure.unauthorized) {
                    onSignedOut()
                    return@launch
                }
                lastHeard = null
                if (kicked) {
                    kicked = false
                    backoff = 1_000L
                    continue
                }

                _state.update { it.copy(connection = Connection.Reconnecting) }
                if (holdWhileAway()) {
                    kicks.receive()
                    backoff = 1_000L
                    continue
                }
                // A kick (the app back in front) skips the wait and starts the backing-off again.
                backoff = if (withTimeoutOrNull(backoff) { kicks.receive() } != null) 1_000L else (backoff * 2).coerceAtMost(15_000L)
            }
        }
        startCalendarSync()
    }

    /**
     * Connects again now, dropping the connection there is. For coming back to the front: while the
     * app was in the background the phone may have frozen it and the network dropped the
     * connection without either end noticing, so it would sit looking connected and hear nothing
     * until the read timed out. Last-Event-ID replays whatever was missed.
     */
    fun reconnect() {
        if (stream == null) return
        kicked = connection?.isActive == true
        kicks.trySend(Unit)
        connection?.cancel()
    }

    /**
     * Back in front: reconnects only if the stream has gone quiet. The server pings every 15 seconds,
     * so a stream that's said something in the last [ALIVE] is still there; one that hasn't may have
     * been dropped while the phone was locked without either end noticing.
     */
    fun resume() {
        val heard = lastHeard
        if (state.value.connection == Connection.Open && heard != null && clock.now() - heard < ALIVE) return
        reconnect()
    }

    fun stop() {
        stream?.cancel()
        stream = null
        connection?.cancel()
        connection = null
        calendarSync?.cancel()
        calendarSync = null
    }

    /** Asks for the phone's calendar; once allowed, shares it and reconnects offering to write to it. */
    fun useCalendar() {
        val calendar = calendar ?: return
        scope.launch {
            if (!calendar.requestAccess()) return@launch
            _calendarOn.value = true
            if (stream != null) {
                stop()
                start()
            }
        }
    }

    /** The familiar was renamed in Settings: the chat says so straight away, not at the next reconnect. */
    fun renamed(name: String) {
        _state.update { it.copy(familiarName = name) }
    }

    /** Uploads the phone's calendar now rather than at the next quarter-hour. */
    fun syncCalendar() {
        scope.launch { uploadCalendar() }
    }

    private fun capabilities(): Set<String> =
        (if (calendar?.canWrite() == true) setOf(Capabilities.CALENDAR_WRITE) else emptySet()) + phone?.capabilities.orEmpty()

    private fun startCalendarSync() {
        if (calendarSync != null || calendar?.canRead() != true) return
        calendarSync = scope.launch {
            while (true) {
                uploadCalendar()
                delay(SYNC_EVERY)
            }
        }
    }

    private suspend fun uploadCalendar() {
        val calendar = calendar?.takeIf { it.canRead() } ?: return
        try {
            val now = clock.now()
            api.uploadDeviceCalendar(calendar.upcoming((now - LOOK_BACK).toEpochMilliseconds(), (now + LOOK_AHEAD).toEpochMilliseconds()))
        } catch (exception: Exception) {
            // Tried again in a quarter of an hour; the stream notices a signed-out token.
            if (exception is CancellationException) throw exception
        }
    }

    /** Does what the server asked of this phone, once, if it's still wanted, and says how it went. */
    private fun answer(invoke: ToolInvokeEvent) {
        // Meant for this phone, on whichever connection it came: back from the background there can
        // briefly be two, and the server may have picked the one that's stuck. Older servers say
        // only the connection.
        val mine = if (invoke.deviceId != null) invoke.deviceId.equals(deviceId, ignoreCase = true) else invoke.connectionId == connectionId
        if (!mine || invoke.callId in answered) return
        val expires = runCatching { Instant.parse(invoke.expiresAt) }.getOrNull()
        if (expires != null && expires < clock.now()) return
        answered.addLast(invoke.callId)
        if (answered.size > 32) answered.removeFirst()

        scope.launch {
            val result = try {
                ToolResultRequest(invoke.callId, ok = true, result = perform(invoke))
            } catch (exception: Exception) {
                if (exception is CancellationException) throw exception
                ToolResultRequest(invoke.callId, ok = false, error = exception.message ?: "The phone couldn't do that.")
            }
            try {
                api.toolResult(result)
            } catch (exception: Exception) {
                // Too late, or unreachable: the server has already moved on without it.
                if (exception is CancellationException) throw exception
            }
            if (result.ok && invoke.tool == CALENDAR_ADD) uploadCalendar()
        }
    }

    private suspend fun perform(invoke: ToolInvokeEvent): JsonObject = when (invoke.tool) {
        CALENDAR_ADD -> {
            val calendar = calendar?.takeIf { it.canWrite() } ?: throw IllegalStateException("This phone's calendar isn't shared.")
            fun text(name: String) = invoke.arguments[name]?.jsonPrimitive?.contentOrNull
            val name = calendar.add(
                title = text("title") ?: throw IllegalArgumentException("The event has no title."),
                startsAt = text("startsAt") ?: throw IllegalArgumentException("The event has no start."),
                endsAt = text("endsAt") ?: throw IllegalArgumentException("The event has no end."),
                allDay = invoke.arguments["allDay"]?.jsonPrimitive?.booleanOrNull == true,
                timeZone = text("timeZone"),
            )
            buildJsonObject { put("calendar", name) }
        }
        in phone?.capabilities.orEmpty() -> phone!!.perform(invoke.tool, invoke.arguments)
        else -> throw IllegalArgumentException("This phone can't do ${invoke.tool}.")
    }

    /** A typed message, with a picture (already shrunk) if there is one: the picture goes up first. */
    fun send(text: String, picture: ByteArray? = null) {
        val trimmed = text.trim()
        if (trimmed.isEmpty() && picture == null) return
        scope.launch {
            _state.update { it.copy(error = null) }
            try {
                val imageId = picture?.let { api.uploadImage(it).id }
                val message = api.send(trimmed, currentTimeZoneId(), speak, imageId)
                _state.update { ConversationReducer.apply(it, ServerEvent.Message(message)) }
            } catch (exception: Exception) {
                if (exception is CancellationException) throw exception
                if (exception is ApiException && exception.unauthorized) onSignedOut()
                _state.update { it.copy(error = exception.message ?: "Couldn't send that.") }
            }
        }
    }

    /** Something said aloud, as a WAV (push-to-talk); the reply comes on the stream, spoken. */
    fun sendSpoken(wav: ByteArray, overReply: Boolean = false) {
        scope.launch {
            _state.update { it.copy(error = null) }
            try {
                api.sendSpoken(wav, currentTimeZoneId(), overReply)?.let { message ->
                    _state.update { ConversationReducer.apply(it, ServerEvent.Message(message)) }
                }
            } catch (exception: Exception) {
                if (exception is CancellationException) throw exception
                if (exception is ApiException && exception.unauthorized) onSignedOut()
                _state.update { it.copy(error = exception.message ?: "Couldn't send that.") }
            }
        }
    }

    /** Shows a problem with the conversation (the microphone, say) where errors go. */
    fun showError(message: String?) {
        _state.update { it.copy(error = message) }
    }

    /** Stop the reply; [heard] says how much of it was played aloud, which is what's kept. */
    fun interrupt(heard: InterruptRequest? = null) {
        scope.launch { runCatching { api.interrupt(heard) } }
    }

    fun startOver() {
        scope.launch { runCatching { api.startOver() } }
    }

    private suspend fun loadHistory() {
        try {
            val messages = api.history()
            _state.update { ConversationReducer.history(it, messages) }
        } catch (exception: Exception) {
            if (exception is CancellationException) throw exception
        }
    }

    private companion object {
        /** A stream that's said something this recently is alive (the server pings every 15 s). */
        val ALIVE = 20.seconds

        /** How long a reconnect on purpose may take before it says "reconnecting…". */
        const val QUIET_RECONNECT = 3_000L

        /** How long a connection may go without a word before it's given up as stuck. */
        const val STUCK = 10_000L

        const val CALENDAR_ADD = "calendar.add"
        val SYNC_EVERY = 15.minutes

        // Something that started this morning still matters; two weeks ahead is plenty to plan with.
        val LOOK_BACK = 12.hours
        val LOOK_AHEAD = 14.days
    }
}
