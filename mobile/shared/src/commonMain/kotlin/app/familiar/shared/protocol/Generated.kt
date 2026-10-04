// Generated from protocol/familiar.schema.json by client/scripts/protocol.mjs - don't edit.
// To change it, change the server's records, then regenerate (see ProtocolSchemaTests).

package app.familiar.shared.protocol

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonObject

@Serializable
data class AccountResponse(
    val id: String,
    val username: String,
    val email: String,
    val role: String,
)

/** What an account may do on this server. */
object AccountRole {
    const val USER = "user"
    const val ADMIN = "admin"
}

@Serializable
data class AddFeedRequest(
    /** What to call it. */
    val name: String? = null,
    /** Its secret iCal address (https:// or webcal://). */
    val url: String? = null,
)

@Serializable
data class AdminUserView(
    /** The account. */
    val id: String,
    /** Its username. */
    val username: String,
    /** Its email. */
    val email: String,
    /** user or admin. */
    val role: String,
    /** When it was made. */
    val createdAt: String,
    /** When it last signed in, if ever. */
    val lastLoginAt: String? = null,
    /** It can't sign in, and its sessions and devices have stopped working. */
    val isDisabled: Boolean,
    /** How many phones it has signed in. */
    val devices: Int,
    /** The admin looking at the list. */
    val isYou: Boolean,
)

@Serializable
data class AnnounceRequest(
    /** What to say. */
    val message: String? = null,
    /** "say" (word for word, the default) or "tell" (the familiar puts it in its own words). */
    val style: String? = null,
    /** For "tell": the user's IANA time zone, if the caller knows it. */
    val timeZone: String? = null,
)

/**
 * How a familiar looks (PLAN.md §5.5). Either the built-in plush llama, coloured and dressed, or an
 * uploaded VRM model.
 */
@Serializable
data class AppearanceView(
    /** A built-in character (llama, fox, hedgehog, monster, otter, giraffe, spot) or "vrm". */
    val model: String,
    /** For "vrm", which uploaded model. */
    val vrmId: String? = null,
    /** The character's colours (what each paints depends on the character). */
    val colours: LlamaColours,
    /** What it wears: none, bow, scarf, glasses, flower. The spot wears nothing. */
    val accessory: String,
    /** How a built-in character is drawn: "2d" (flat, the default) or "3d" (modelled and lit). */
    val style: String = "2d",
)

/** The reply has no more clips coming: Clips were announced in total. */
@Serializable
data class AudioEndEvent(
    val replyId: String,
    val clips: Int,
)

/** A spoken sentence is ready to fetch. */
@Serializable
data class AudioEvent(
    /** The reply it belongs to. */
    val replyId: String,
    /** Its position in the reply; clips are played in this order. */
    val seq: Int,
    /** Where to fetch the audio. */
    val url: String,
    /** What it says. */
    val text: String,
    /** When each word is spoken, if the TTS service says - for mouth shapes. */
    val words: List<SpokenWord>? = null,
)

@Serializable
data class AvatarView(
    val id: String,
    val name: String,
    val sizeBytes: Int,
    val meta: VrmInfo,
    val createdAt: String,
)

object CalendarKind {
    const val LOCAL = "local"
    const val ICS = "ics"
    const val DEVICE = "device"
}

/** A calendar as Settings shows it. Never the feed's address: that's a secret. */
@Serializable
data class CalendarView(
    val id: String,
    val kind: String,
    val name: String,
    val lastSyncedAt: String? = null,
    val lastError: String? = null,
)

@Serializable
data class ChangePasswordRequest(
    val currentPassword: String? = null,
    val newPassword: String? = null,
)

@Serializable
data class ConfirmRequest(
    /** The tool call being answered, from the tool.confirm event. */
    val callId: String? = null,
    /** Whether the user allows it. */
    val approve: Boolean,
)

@Serializable
data class CreateUserRequest(
    /** 3-24 letters, digits or underscores. */
    val username: String? = null,
    /** Their email. */
    val email: String? = null,
    /** A first password, for them to change. */
    val password: String? = null,
    /** user (the default) or admin. */
    val role: String? = null,
)

@Serializable
data class DeviceCalendarUpload(
    /** Every event in the window the phone covers (the next couple of weeks). */
    val events: List<DeviceEventView>? = null,
)

/** One event (or occurrence) from a phone's calendar. */
@Serializable
data class DeviceEventView(
    /** The phone's id for it (instance id plus start), stable across uploads. */
    val externalId: String? = null,
    /** Its title. */
    val title: String? = null,
    /** Where, if it says. */
    val location: String? = null,
    /** When it starts; for an all-day event, UTC midnight of its date. */
    val startsAt: String,
    /** When it ends. */
    val endsAt: String,
    /** A whole-day event. */
    val allDay: Boolean,
    /** Which of the phone's calendars ("Work", "sam@gmail.com"). */
    val calendar: String? = null,
)

@Serializable
data class DeviceLoginRequest(
    val username: String? = null,
    val password: String? = null,
    val deviceName: String? = null,
)

/** Returned once, at sign-in. The token is never retrievable again. */
@Serializable
data class DeviceLoginResponse(
    val token: String,
    val device: DeviceSummary,
    val account: AccountResponse,
)

@Serializable
data class DeviceSummary(
    val id: String,
    val name: String,
    val createdAt: String,
    val expiresAt: String,
    val lastUsedAt: String? = null,
    val isCurrent: Boolean,
)

/**
 * What the familiar is feeling about what it's saying. Maps one-to-one onto VRM's preset
 * expressions (happy, sad, angry, surprised, relaxed) plus neutral and thinking (PLAN.md §4.4).
 */
object Emotion {
    const val NEUTRAL = "neutral"
    const val JOY = "joy"
    const val SAD = "sad"
    const val ANGRY = "angry"
    const val SURPRISED = "surprised"
    const val RELAXED = "relaxed"
    const val THINKING = "thinking"
}

@Serializable
data class ErrorEvent(
    val code: String,
    val message: String,
)

@Serializable
data class ErrorResponse(
    val error: String,
)

@Serializable
data class EventView(
    val id: String,
    val calendarId: String,
    val title: String,
    val location: String? = null,
    val startsAt: String,
    val endsAt: String,
    val allDay: Boolean,
)

@Serializable
data class ExpressionEvent(
    val emotion: String,
    val intensity: Double,
)

object FamiliarState {
    const val IDLE = "idle"
    const val THINKING = "thinking"
    const val SPEAKING = "speaking"
    const val RESTING = "resting"
}

@Serializable
data class FamiliarSummary(
    val name: String,
    val expressiveness: Double,
)

@Serializable
data class FamiliarView(
    val name: String,
    val userNickname: String? = null,
    val warmth: Double,
    val playfulness: Double,
    val verbosity: Double,
    val formality: Double,
    val expressiveness: Double,
    val voice: String? = null,
    val voiceSpeed: Double = 1.0,
    val proactive: Boolean? = null,
    val appearance: AppearanceView? = null,
    val lookUps: Boolean? = null,
    val homePlace: String? = null,
)

/** A one-off body movement the familiar makes mid-sentence. */
object Gesture {
    const val NOD = "nod"
    const val SHAKE = "shake"
    const val TILT = "tilt"
    const val WAVE = "wave"
    const val SHRUG = "shrug"
    const val LAUGH = "laugh"
    const val WINK = "wink"
    const val SIGH = "sigh"
    const val YAWN = "yawn"
    const val EXCITED = "excited"
}

@Serializable
data class GestureEvent(
    val gesture: String,
)

@Serializable
data class HookTokenView(
    val id: String,
    val name: String,
    val createdAt: String,
    val lastUsedAt: String? = null,
)

@Serializable
data class InterruptRequest(
    /** The reply that was playing when the user interrupted. */
    val replyId: String? = null,
    /** What of it they heard: the reply is cut to this. */
    val heard: String? = null,
)

object JobKind {
    const val TIMER = "timer"
    const val REMINDER = "reminder"
}

/** A pending timer or reminder, as the client shows it. */
@Serializable
data class JobView(
    /** The job. */
    val id: String,
    /** Timer or reminder. */
    val kind: String,
    /** What it's for. */
    val label: String,
    /** When it goes off. */
    val dueAt: String,
    /** For a timer, how long it was set for. */
    val durationSeconds: Int? = null,
    /** When it was set. */
    val createdAt: String,
    /** It rings until dismissed (timers, and alarm reminders) rather than notifying. */
    val ring: Boolean,
)

/**
 * The account's pending jobs, sent whenever they change, so every device's timer list stays
 * current.
 */
@Serializable
data class JobsEvent(
    val jobs: List<JobView>,
)

/** The plush llama's colours, as #rrggbb. Shading is derived from these by the client. */
@Serializable
data class LlamaColours(
    /** The fleece. */
    val wool: String,
    /** The bare face and snout. */
    val face: String,
    /** Inside the ears, and the hooves. */
    val ear: String,
    /** The cheeks. */
    val blush: String,
    /** The accessory. */
    val accent: String,
)

@Serializable
data class LoginRequest(
    val username: String? = null,
    val password: String? = null,
)

@Serializable
data class McpServerRequest(
    /** Short and lower-case: it prefixes the tool names the model sees. */
    val name: String? = null,
    /** The server's streamable-HTTP endpoint. */
    val url: String? = null,
    /** A bearer token, if the server needs one. */
    val token: String? = null,
)

@Serializable
data class McpServerView(
    val id: String,
    val name: String,
    val urlHost: String,
    val tools: List<McpToolView>,
    val refreshedAt: String? = null,
    val lastError: String? = null,
)

@Serializable
data class McpToolView(
    val name: String,
    val modelName: String,
    val description: String? = null,
    val readOnly: Boolean,
    val enabled: Boolean,
)

@Serializable
data class McpToolsRequest(
    /** The tool names (as the server calls them) to switch on. */
    val enabled: List<String>? = null,
)

@Serializable
data class MemoryRequest(
    /** The memory, one short sentence. */
    val text: String? = null,
)

object MemorySource {
    const val CONVERSATION = "conversation"
    const val TOLD = "told"
    const val SETTINGS = "settings"
}

@Serializable
data class MemoryView(
    val id: String,
    val text: String,
    val source: String,
    val createdAt: String,
    val updatedAt: String,
)

object MessageRole {
    const val USER = "user"
    const val ASSISTANT = "assistant"
}

/** A stored message, as both the history endpoint and the message event show it. */
@Serializable
data class MessageView(
    val id: String,
    val role: String,
    val text: String,
    val emotion: String? = null,
    val intensity: Double? = null,
    val interrupted: Boolean,
    val createdAt: String,
)

@Serializable
data class NameRequest(
    val name: String? = null,
)

@Serializable
data class NewHookTokenView(
    /** The token's id. */
    val id: String,
    /** What uses it. */
    val name: String,
    /** Shown once, at creation. */
    val token: String,
)

@Serializable
data class NewWebhookView(
    /** The webhook. */
    val webhook: WebhookView,
    /** The signing secret, shown once, at creation. */
    val secret: String,
)

/** Something that just happened, for the service worker to show as a notification. */
@Serializable
data class NotificationView(
    val id: String,
    val kind: String,
    val title: String,
    val body: String,
    val at: String,
)

/**
 * Something went off or arrived: a timer, a reminder, an announcement from a hook. The reply that
 * says it arrives as usual; this is for the chime.
 */
@Serializable
data class NotifyEvent(
    /** The job or announcement. */
    val id: String,
    /** One of NotifyKinds. */
    val kind: String,
    /** What it was for, or who sent it. */
    val label: String,
    /** What the familiar says about it. */
    val text: String,
)

@Serializable
data class PushKeyResponse(
    val publicKey: String,
)

@Serializable
data class ReadyEvent(
    /** Its name and expressiveness. */
    val familiar: FamiliarSummary,
    /** The model server answers. */
    val modelAvailable: Boolean,
    /** Speech-to-text and text-to-speech both answer. */
    val speechAvailable: Boolean,
    /** For a phone that listed capabilities: its connection, which tool.invoke events address. */
    val connectionId: String? = null,
)

@Serializable
data class RegisterRequest(
    val username: String? = null,
    val email: String? = null,
    val password: String? = null,
)

@Serializable
data class ReplyDeltaEvent(
    val id: String,
    val text: String,
)

@Serializable
data class ReplyEndEvent(
    val id: String,
    val text: String,
    val emotion: String,
    val intensity: Double,
    val interrupted: Boolean,
)

@Serializable
data class ReplyStartEvent(
    val id: String,
)

/**
 * The user talked over a reply, so it was cut to what they actually heard (the model had usually
 * finished writing it long before the familiar finished saying it).
 */
@Serializable
data class ReplyTruncatedEvent(
    val id: String,
    val text: String,
)

@Serializable
data class ResetPasswordRequest(
    /** The new password. */
    val password: String? = null,
)

@Serializable
data class SendMessageRequest(
    /** What the user typed. */
    val text: String? = null,
    /** The user's IANA time zone, for the situation in the prompt. */
    val timeZone: String? = null,
    /** Speak the reply as well as showing it. Spoken messages are always answered aloud. */
    val speak: Boolean? = null,
)

/**
 * A sound effect, not a voice: made by the clients themselves (synthesised, so nothing to fetch),
 * the same whichever voice the familiar has. Vocal sounds (a giggle, a gasp) come later, in its
 * voice.
 */
object SoundEffect {
    const val POP = "pop"
    const val BOING = "boing"
    const val SPARKLE = "sparkle"
    const val TADA = "tada"
    const val WHOOSH = "whoosh"
    const val WOMP = "womp"
}

/** A sound effect to play now, made by the client (no audio to fetch). */
@Serializable
data class SoundEvent(
    val sound: String,
)

@Serializable
data class SpokenWord(
    val word: String,
    val start: Double,
    val end: Double,
)

@Serializable
data class StateEvent(
    val state: String,
)

@Serializable
data class SubscribeRequest(
    /** The push service URL from the browser's PushSubscription. */
    val endpoint: String? = null,
    /** The browser's keys; stored, though content-free pushes don't need them. */
    val keys: SubscriptionKeys? = null,
)

@Serializable
data class SubscriptionKeys(
    /** The browser's public key. */
    val p256dh: String? = null,
    /** The browser's auth secret. */
    val auth: String? = null,
)

/** The familiar wants to do something outside Familiar and is asking first. */
@Serializable
data class ToolConfirmEvent(
    /** The reply it's part of. */
    val replyId: String,
    /** Answer with this. */
    val callId: String,
    /** The tool's name. */
    val tool: String,
    /** What it will do, for the user to allow or not. */
    val description: String,
    /** After this it counts as declined. */
    val expiresAt: String,
)

/** A confirmation request was answered (by any device), so the others can put theirs away. */
@Serializable
data class ToolConfirmedEvent(
    val callId: String,
    val approved: Boolean,
)

@Serializable
data class ToolFinishedEvent(
    val replyId: String,
    val callId: String,
    val tool: String,
    val ok: Boolean,
    val summary: String? = null,
)

/** A tool call for one particular phone to carry out. */
@Serializable
data class ToolInvokeEvent(
    /** Answer with this. */
    val callId: String,
    /** Which connection should act; every other device ignores it. */
    val connectionId: String,
    /** What to do, e.g. "calendar.add". */
    val tool: String,
    /** Its arguments. */
    val arguments: JsonObject,
    /** After this, don't: the server has stopped waiting (a replayed invoke must not run twice). */
    val expiresAt: String,
)

@Serializable
data class ToolResultRequest(
    /** The tool.invoke being answered. */
    val callId: String? = null,
    /** Whether it worked. */
    val ok: Boolean,
    /** What happened, for the model (and the summary). */
    val result: JsonObject? = null,
    /** Why not, when it didn't. */
    val error: String? = null,
)

@Serializable
data class ToolStartedEvent(
    val replyId: String,
    val callId: String,
    val tool: String,
)

@Serializable
data class UnsubscribeRequest(
    /** The subscription to remove. */
    val endpoint: String? = null,
)

@Serializable
data class UpdateUserRequest(
    /** A new email, or null to leave it. */
    val email: String? = null,
    /** A new role, or null to leave it. */
    val role: String? = null,
    /** Disable or re-enable, or null to leave it. */
    val isDisabled: Boolean? = null,
)

@Serializable
data class VoicesView(
    /** What the TTS service offers; empty if it isn't reachable. */
    val voices: List<String>,
    /** The voice used when a familiar hasn't chosen one. */
    val default: String,
)

/** What a VRM model says about itself. */
@Serializable
data class VrmInfo(
    /** "0.x" or "1.0". */
    val version: String,
    /** Its title, if it has one. */
    val name: String? = null,
    /** Who made it. */
    val authors: List<String>,
    /** Its licence, as the model states it (a name or a URL). */
    val licence: String? = null,
    /** What it says about commercial use, if anything. */
    val commercialUse: String? = null,
)

@Serializable
data class WebhookRequest(
    val name: String? = null,
    val url: String? = null,
    val events: List<String>? = null,
    val enabled: Boolean? = null,
)

/** How a test delivery went. */
@Serializable
data class WebhookTestResult(
    /** The receiver answered with a success. */
    val ok: Boolean,
    /** What happened: the status it answered with, or why it couldn't be reached. */
    val result: String,
)

@Serializable
data class WebhookView(
    val id: String,
    val name: String,
    val urlHost: String,
    val events: List<String>,
    val enabled: Boolean,
    val lastDeliveredAt: String? = null,
    val lastResult: String? = null,
)

/** The stream's event names, as the server calls them (EventTypes). */
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
    const val SOUND = "sound"
    const val AUDIO = "audio"
    const val AUDIO_END = "audio.end"
    const val ERROR = "turn.error"
    const val TOOL_STARTED = "tool.started"
    const val TOOL_FINISHED = "tool.finished"
    const val TOOL_CONFIRM = "tool.confirm"
    const val TOOL_CONFIRMED = "tool.confirmed"
    const val TOOL_INVOKE = "tool.invoke"
    const val JOBS = "jobs"
    const val NOTIFY = "notify"
    const val CLEARED = "cleared"
    const val RESYNC = "resync"
}
