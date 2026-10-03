package app.familiar.shared.protocol

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

// The server's request, response and event payloads (PLAN.md §3.1), mirroring
// src/Familiar.Server/**: AuthEndpoints, DeviceEndpoints, ConversationEvents and friends.
// Hand-written for now; generated from the server's schema later (docs/MOBILE.md §6).
// Unknown fields are ignored, so a newer server doesn't break an older app.

/** How the server's JSON is read and written: camelCase names, enums as camelCase strings. */
val FamiliarJson = Json {
    ignoreUnknownKeys = true
    explicitNulls = false
    encodeDefaults = true
}

@Serializable
data class DeviceLoginRequest(val username: String, val password: String, val deviceName: String)

@Serializable
data class AccountResponse(val id: String, val username: String, val email: String, val role: String)

@Serializable
data class DeviceSummary(
    val id: String,
    val name: String,
    val createdAt: String,
    val expiresAt: String,
    val lastUsedAt: String? = null,
    val isCurrent: Boolean = false,
)

@Serializable
data class DeviceLoginResponse(val token: String, val device: DeviceSummary, val account: AccountResponse)

/** The body of a 4xx the server explains: `{ "error": "…" }`. */
@Serializable
data class ErrorBody(val error: String? = null, val title: String? = null)

@Serializable
data class SendMessageRequest(val text: String, val timeZone: String, val speak: Boolean = false)

@Serializable
data class InterruptRequest(val replyId: String? = null, val heard: String? = null)

/** A stored message, as the history endpoint and the `message` event show it. */
@Serializable
data class MessageView(
    val id: String,
    val role: String,
    val text: String,
    val emotion: String? = null,
    val intensity: Double? = null,
    val interrupted: Boolean = false,
    val createdAt: String = "",
)

@Serializable
data class FamiliarSummary(val name: String, val expressiveness: Double = 0.8)

/** [connectionId] is set for a phone that listed capabilities: `tool.invoke` events name it. */
@Serializable
data class ReadyEvent(
    val familiar: FamiliarSummary,
    val modelAvailable: Boolean,
    val speechAvailable: Boolean,
    val connectionId: String? = null,
)

@Serializable
data class StateEvent(val state: String)

@Serializable
data class ReplyStartEvent(val id: String)

@Serializable
data class ReplyDeltaEvent(val id: String, val text: String)

@Serializable
data class ReplyEndEvent(
    val id: String,
    val text: String,
    val emotion: String? = null,
    val intensity: Double? = null,
    val interrupted: Boolean = false,
)

@Serializable
data class ReplyTruncatedEvent(val id: String, val text: String)

@Serializable
data class ExpressionEvent(val emotion: String, val intensity: Double)

@Serializable
data class GestureEvent(val gesture: String)

@Serializable
data class ErrorEvent(val code: String, val message: String)

@Serializable
data class ToolStartedEvent(val replyId: String, val callId: String, val tool: String)

@Serializable
data class ToolFinishedEvent(val replyId: String, val callId: String, val tool: String, val ok: Boolean, val summary: String? = null)

@Serializable
data class NotifyEvent(val id: String, val kind: String, val label: String, val text: String)

/**
 * A pending timer or reminder. [ring]: it rings until dismissed (timers, and reminders set as alarms)
 * rather than notifying. The phone keeps an alarm for each, so they go off with no connection.
 */
@Serializable
data class JobView(
    val id: String,
    val kind: String,
    val label: String,
    val dueAt: String,
    val durationSeconds: Int? = null,
    val ring: Boolean = false,
) {
    val isTimer: Boolean get() = kind.equals("timer", ignoreCase = true)
}

@Serializable
data class JobsEvent(val jobs: List<JobView>)

/** Something that went off recently, for a phone woken by a push to show. */
@Serializable
data class NotificationView(val id: String, val kind: String, val title: String, val body: String, val at: String)

@Serializable
data class PushKeyResponse(val publicKey: String)

@Serializable
data class SubscribeRequest(val endpoint: String)

/** A word as the TTS service timed it, in seconds from the start of its clip. */
@Serializable
data class SpokenWord(val word: String, val start: Double, val end: Double)

/** A spoken sentence is ready to fetch and play, in [seq] order within its reply. */
@Serializable
data class AudioEvent(
    val replyId: String,
    val seq: Int,
    val url: String,
    val text: String,
    val words: List<SpokenWord>? = null,
)

/** The server asking this phone to do something only it can (PLAN.md §6.1). */
@Serializable
data class ToolInvokeEvent(
    val callId: String,
    val connectionId: String,
    val tool: String,
    val arguments: kotlinx.serialization.json.JsonObject,
    val expiresAt: String,
)

@Serializable
data class ToolResultRequest(
    val callId: String,
    val ok: Boolean,
    val result: kotlinx.serialization.json.JsonObject? = null,
    val error: String? = null,
)

/** One event from the phone's calendars, for the familiar's situation awareness. */
@Serializable
data class DeviceEventView(
    val externalId: String,
    val title: String,
    val location: String? = null,
    val startsAt: String,
    val endsAt: String,
    val allDay: Boolean = false,
    val calendar: String? = null,
)

@Serializable
data class DeviceCalendarUpload(val events: List<DeviceEventView>)

/** What phones can do for the familiar, as the server names them. */
object Capabilities {
    const val CALENDAR_WRITE = "calendar.write"
}

/** The event names on the stream: the SSE `event:` field. */
object EventTypes {
    const val READY = "ready"
    const val STATE = "state"
    const val MESSAGE = "message"
    const val REPLY_START = "reply.start"
    const val REPLY_DELTA = "reply.delta"
    const val REPLY_END = "reply.end"
    const val REPLY_TRUNCATED = "reply.truncated"
    const val EXPRESSION = "expression"
    const val GESTURE = "gesture"
    const val ERROR = "turn.error"
    const val CLEARED = "cleared"
    const val RESYNC = "resync"
    const val TOOL_STARTED = "tool.started"
    const val TOOL_FINISHED = "tool.finished"
    const val NOTIFY = "notify"
    const val TOOL_INVOKE = "tool.invoke"
    const val AUDIO = "audio"
    const val JOBS = "jobs"
}

/** One event from the conversation stream, decoded. Events this app doesn't use yet are [Other]. */
sealed interface ServerEvent {
    data class Ready(val ready: ReadyEvent) : ServerEvent
    data class State(val state: String) : ServerEvent
    data class Message(val message: MessageView) : ServerEvent
    data class ReplyStart(val id: String) : ServerEvent
    data class ReplyDelta(val id: String, val text: String) : ServerEvent
    data class ReplyEnd(val end: ReplyEndEvent) : ServerEvent
    data class ReplyTruncated(val id: String, val text: String) : ServerEvent
    data class Expression(val emotion: String, val intensity: Double) : ServerEvent
    data class Gesture(val gesture: String) : ServerEvent
    data class Error(val message: String) : ServerEvent
    data class ToolStarted(val event: ToolStartedEvent) : ServerEvent
    data class ToolFinished(val event: ToolFinishedEvent) : ServerEvent
    data class Notify(val event: NotifyEvent) : ServerEvent
    data class ToolInvoke(val event: ToolInvokeEvent) : ServerEvent
    data class Audio(val clip: AudioEvent) : ServerEvent
    data class Jobs(val jobs: List<JobView>) : ServerEvent
    data object Cleared : ServerEvent
    data object Resync : ServerEvent
    data class Other(val type: String) : ServerEvent

    companion object {
        /** Decodes one SSE event. Throws only on malformed JSON for a known event type. */
        fun parse(type: String?, data: String?): ServerEvent {
            val json = data ?: "{}"
            return when (type) {
                EventTypes.READY -> Ready(FamiliarJson.decodeFromString(json))
                EventTypes.STATE -> State(FamiliarJson.decodeFromString<StateEvent>(json).state)
                EventTypes.MESSAGE -> Message(FamiliarJson.decodeFromString(json))
                EventTypes.REPLY_START -> ReplyStart(FamiliarJson.decodeFromString<ReplyStartEvent>(json).id)
                EventTypes.REPLY_DELTA -> FamiliarJson.decodeFromString<ReplyDeltaEvent>(json).let { ReplyDelta(it.id, it.text) }
                EventTypes.REPLY_END -> ReplyEnd(FamiliarJson.decodeFromString(json))
                EventTypes.REPLY_TRUNCATED -> FamiliarJson.decodeFromString<ReplyTruncatedEvent>(json).let { ReplyTruncated(it.id, it.text) }
                EventTypes.EXPRESSION -> FamiliarJson.decodeFromString<ExpressionEvent>(json).let { Expression(it.emotion, it.intensity) }
                EventTypes.GESTURE -> Gesture(FamiliarJson.decodeFromString<GestureEvent>(json).gesture)
                EventTypes.ERROR -> Error(FamiliarJson.decodeFromString<ErrorEvent>(json).message)
                EventTypes.TOOL_STARTED -> ToolStarted(FamiliarJson.decodeFromString(json))
                EventTypes.TOOL_FINISHED -> ToolFinished(FamiliarJson.decodeFromString(json))
                EventTypes.NOTIFY -> Notify(FamiliarJson.decodeFromString(json))
                EventTypes.TOOL_INVOKE -> ToolInvoke(FamiliarJson.decodeFromString(json))
                EventTypes.AUDIO -> Audio(FamiliarJson.decodeFromString(json))
                EventTypes.JOBS -> Jobs(FamiliarJson.decodeFromString<JobsEvent>(json).jobs)
                EventTypes.CLEARED -> Cleared
                EventTypes.RESYNC -> Resync
                else -> Other(type ?: "message")
            }
        }
    }
}
