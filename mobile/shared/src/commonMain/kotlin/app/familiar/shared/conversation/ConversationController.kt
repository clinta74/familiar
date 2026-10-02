package app.familiar.shared.conversation

import app.familiar.shared.api.ApiException
import app.familiar.shared.api.FamiliarApi
import app.familiar.shared.platform.currentTimeZoneId
import app.familiar.shared.protocol.ServerEvent
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

/**
 * The live conversation for one signed-in device: history first, then the event stream, kept
 * connected - on a drop it reconnects with Last-Event-ID so the server replays what was missed.
 */
class ConversationController(
    private val api: FamiliarApi,
    private val scope: CoroutineScope,
    /** Called when the server no longer accepts this device's token: it was signed out elsewhere. */
    private val onSignedOut: () -> Unit,
) {
    private val _state = MutableStateFlow(ConversationState())
    val state: StateFlow<ConversationState> = _state.asStateFlow()

    /** Expressions and gestures, for the avatar - moments rather than state. */
    var onEvent: (ServerEvent) -> Unit = {}

    private var stream: Job? = null
    private var lastEventId: String? = null

    fun start() {
        if (stream != null) return
        stream = scope.launch {
            loadHistory()
            var backoff = 1_000L
            while (true) {
                try {
                    api.events(currentTimeZoneId(), lastEventId).collect { (id, event) ->
                        if (id != null) lastEventId = id
                        backoff = 1_000L
                        when (event) {
                            ServerEvent.Resync -> loadHistory()
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
    }

    fun stop() {
        stream?.cancel()
        stream = null
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
}
