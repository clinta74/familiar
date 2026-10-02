package app.familiar.shared.conversation

import app.familiar.shared.protocol.MessageView
import app.familiar.shared.protocol.ServerEvent

/** A tool the familiar used during a reply, shown as a chip on it. */
data class ToolChip(val callId: String, val tool: String, val running: Boolean, val ok: Boolean? = null, val summary: String? = null)

data class ChatMessage(
    val id: String,
    val fromUser: Boolean,
    val text: String,
    val emotion: String? = null,
    val interrupted: Boolean = false,
    /** The reply is still arriving. */
    val streaming: Boolean = false,
    val tools: List<ToolChip> = emptyList(),
)

enum class Connection { Connecting, Open, Reconnecting }

data class ConversationState(
    val connection: Connection = Connection.Connecting,
    val familiarName: String = "your familiar",
    val modelAvailable: Boolean = true,
    val speechAvailable: Boolean = false,
    /** idle, thinking, speaking or resting, as the server says. */
    val state: String = "idle",
    val messages: List<ChatMessage> = emptyList(),
    val error: String? = null,
)

private fun MessageView.toChat() = ChatMessage(
    id = id,
    fromUser = role == "user",
    text = text,
    emotion = emotion,
    interrupted = interrupted,
)

/**
 * The conversation as events arrive - the same rules as the web client's reducer
 * (client/src/conversation/reducer.ts). Every event is idempotent by id: a reconnect replays events
 * already applied, and history loaded at startup can overlap the stream.
 */
object ConversationReducer {
    fun history(state: ConversationState, messages: List<MessageView>): ConversationState {
        val known = messages.map { it.id }.toSet()
        val live = state.messages.filter { it.id !in known }
        return state.copy(messages = messages.map { it.toChat() } + live)
    }

    fun apply(state: ConversationState, event: ServerEvent): ConversationState = when (event) {
        is ServerEvent.Ready -> state.copy(
            connection = Connection.Open,
            familiarName = event.ready.familiar.name,
            modelAvailable = event.ready.modelAvailable,
            speechAvailable = event.ready.speechAvailable,
            state = if (event.ready.modelAvailable) state.state else "resting",
        )

        is ServerEvent.State -> state.copy(
            state = event.state,
            // Idle means nothing more is coming: a reply that only ran a tool is done.
            messages = if (event.state == "idle") state.messages.map { if (it.streaming) it.copy(streaming = false) else it } else state.messages,
            modelAvailable = when (event.state) {
                "resting" -> false
                "speaking" -> true
                else -> state.modelAvailable
            },
            error = if (event.state == "thinking") null else state.error,
        )

        is ServerEvent.Message ->
            if (state.messages.any { it.id == event.message.id }) state
            else state.copy(messages = state.messages + event.message.toChat())

        is ServerEvent.ReplyStart ->
            if (state.messages.any { it.id == event.id }) state
            else state.copy(messages = state.messages + ChatMessage(event.id, fromUser = false, text = "", streaming = true))

        is ServerEvent.ReplyDelta -> state.copy(
            messages = state.messages.map { if (it.id == event.id && it.streaming) it.copy(text = it.text + event.text) else it },
        )

        is ServerEvent.ReplyEnd -> {
            val end = event.end
            val existing = state.messages.find { it.id == end.id }
            val done = ChatMessage(end.id, fromUser = false, text = end.text, emotion = end.emotion, interrupted = end.interrupted, tools = existing?.tools.orEmpty())
            state.copy(messages = if (existing != null) state.messages.map { if (it.id == end.id) done else it } else state.messages + done)
        }

        is ServerEvent.ReplyTruncated -> state.copy(
            messages = state.messages.map { if (it.id == event.id) it.copy(text = event.text, interrupted = true, streaming = false) else it },
        )

        is ServerEvent.ToolStarted -> withChip(state, event.event.replyId) { chips ->
            if (chips.any { it.callId == event.event.callId }) chips else chips + ToolChip(event.event.callId, event.event.tool, running = true)
        }

        is ServerEvent.ToolFinished -> withChip(state, event.event.replyId) { chips ->
            val done = ToolChip(event.event.callId, event.event.tool, running = false, ok = event.event.ok, summary = event.event.summary)
            if (chips.any { it.callId == done.callId }) chips.map { if (it.callId == done.callId) done else it } else chips + done
        }

        is ServerEvent.Error -> state.copy(error = event.message)
        ServerEvent.Cleared -> state.copy(messages = emptyList(), error = null)
        else -> state
    }

    /** The reply's bubble, created if a tool ran before it said anything (the usual order). */
    private fun withChip(state: ConversationState, replyId: String, update: (List<ToolChip>) -> List<ToolChip>): ConversationState {
        val messages = if (state.messages.any { it.id == replyId }) state.messages
        else state.messages + ChatMessage(replyId, fromUser = false, text = "", streaming = true)
        return state.copy(messages = messages.map { if (it.id == replyId) it.copy(tools = update(it.tools)) else it })
    }
}
