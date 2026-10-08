package app.familiar.shared.protocol

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

// What the app and the server say to each other (PLAN.md §3.1). The payloads and bodies are
// generated from the server's records (Generated.kt, from protocol/familiar.schema.json - see
// docs/MOBILE.md §6); this is the part that isn't: how JSON is read, and the stream decoded.
// Unknown fields are ignored, so a newer server doesn't break an older app.

/** How the server's JSON is read and written: camelCase names, enums as camelCase strings. */
val FamiliarJson = Json {
    ignoreUnknownKeys = true
    explicitNulls = false
    encodeDefaults = true
}

/** The body of a 4xx the server explains: `{ "error": "…" }`, or a problem's `title`. */
@Serializable
data class ErrorBody(val error: String? = null, val title: String? = null)

/** What phones can do for the familiar, as the server names them. */
object Capabilities {
    const val CALENDAR_WRITE = "calendar.write"

    /** Texts, calls, directions and where the phone is (BACKLOG item 21); the tool has the same name. */
    const val PHONE_TEXT = "phone.text"
    const val PHONE_CALL = "phone.call"
    const val MAPS_NAVIGATE = "maps.navigate"
    const val LOCATION_READ = "location.read"

    /** One contact's number or address, to say it (BACKLOG item 25). */
    const val CONTACTS_READ = "contacts.read"
}

/** A timer, not a reminder. */
val JobView.isTimer: Boolean get() = kind == JobKind.TIMER

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

    /** A sound effect to play now (SoundEffect's names). */
    data class Sound(val sound: String) : ServerEvent
    data class Error(val message: String) : ServerEvent
    data class ToolStarted(val event: ToolStartedEvent) : ServerEvent
    data class ToolFinished(val event: ToolFinishedEvent) : ServerEvent
    data class Notify(val event: NotifyEvent) : ServerEvent
    data class ToolInvoke(val event: ToolInvokeEvent) : ServerEvent
    data class Audio(val clip: AudioEvent) : ServerEvent
    data class Jobs(val jobs: List<JobView>) : ServerEvent
    data object Cleared : ServerEvent
    data object Resync : ServerEvent

    /** The server's keep-alive ping: nothing to show, but the stream is alive. */
    data object Heartbeat : ServerEvent
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
                EventTypes.SOUND -> Sound(FamiliarJson.decodeFromString<SoundEvent>(json).sound)
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
