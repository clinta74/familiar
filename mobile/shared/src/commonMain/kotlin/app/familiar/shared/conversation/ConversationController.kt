package app.familiar.shared.conversation

import app.familiar.shared.api.ApiException
import app.familiar.shared.api.FamiliarApi
import app.familiar.shared.platform.DeviceCalendar
import app.familiar.shared.platform.currentTimeZoneId
import app.familiar.shared.protocol.Capabilities
import app.familiar.shared.protocol.ServerEvent
import app.familiar.shared.protocol.ToolInvokeEvent
import app.familiar.shared.protocol.ToolResultRequest
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
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
) {
    private val _state = MutableStateFlow(ConversationState())
    val state: StateFlow<ConversationState> = _state.asStateFlow()

    /** Expressions and gestures, for the avatar - moments rather than state. */
    var onEvent: (ServerEvent) -> Unit = {}

    private val _calendarOn = MutableStateFlow(calendar?.canRead() == true)

    /** Whether the phone's calendar is shared with the familiar. */
    val calendarOn: StateFlow<Boolean> = _calendarOn.asStateFlow()

    private var stream: Job? = null
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
                try {
                    connectionId = null
                    api.events(currentTimeZoneId(), lastEventId, capabilities()).collect { (id, event) ->
                        if (id != null) lastEventId = id
                        backoff = 1_000L
                        when (event) {
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
                } catch (exception: CancellationException) {
                    throw exception
                } catch (exception: ApiException) {
                    if (exception.unauthorized) {
                        onSignedOut()
                        return@launch
                    }
                } catch (_: Exception) {
                    // Unreachable or dropped: try again shortly.
                }

                _state.update { it.copy(connection = Connection.Reconnecting) }
                delay(backoff)
                backoff = (backoff * 2).coerceAtMost(15_000L)
            }
        }
        startCalendarSync()
    }

    fun stop() {
        stream?.cancel()
        stream = null
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

    /** Uploads the phone's calendar now rather than at the next quarter-hour. */
    fun syncCalendar() {
        scope.launch { uploadCalendar() }
    }

    private fun capabilities(): Set<String> =
        if (calendar?.canWrite() == true) setOf(Capabilities.CALENDAR_WRITE) else emptySet()

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
        if (invoke.connectionId != connectionId || invoke.callId in answered) return
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
        else -> throw IllegalArgumentException("This phone can't do ${invoke.tool}.")
    }

    fun send(text: String) {
        val trimmed = text.trim()
        if (trimmed.isEmpty()) return
        scope.launch {
            _state.update { it.copy(error = null) }
            try {
                val message = api.send(trimmed, currentTimeZoneId())
                _state.update { ConversationReducer.apply(it, ServerEvent.Message(message)) }
            } catch (exception: Exception) {
                if (exception is CancellationException) throw exception
                if (exception is ApiException && exception.unauthorized) onSignedOut()
                _state.update { it.copy(error = exception.message ?: "Couldn't send that.") }
            }
        }
    }

    fun interrupt() {
        scope.launch { runCatching { api.interrupt() } }
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
        const val CALENDAR_ADD = "calendar.add"
        val SYNC_EVERY = 15.minutes

        // Something that started this morning still matters; two weeks ahead is plenty to plan with.
        val LOOK_BACK = 12.hours
        val LOOK_AHEAD = 14.days
    }
}
