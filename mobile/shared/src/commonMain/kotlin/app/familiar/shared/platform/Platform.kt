package app.familiar.shared.platform

/** The user's IANA time zone ("Europe/London"), for the situation the server gives the model. */
expect fun currentTimeZoneId(): String

/**
 * An instant from the server (ISO-8601) as the phone's locale writes it, in its time zone: "3 Oct
 * 2026, 14:05", or just the date. With [utc], read in UTC: all-day events are UTC midnights, and
 * shown in the phone's zone they'd land on the day before west of Greenwich.
 */
expect fun formatInstant(iso: String, withTime: Boolean = true, utc: Boolean = false): String

/** A file the user picked (an uploaded VRM model, in the studio). */
class PickedFile(val name: String, val bytes: ByteArray)

/**
 * Pictures for the chat (BACKLOG item 35): the system's photo picker, and turning a JPEG into
 * something to draw. What's picked comes back shrunk to 1024 pixels at most and re-saved as JPEG,
 * so what a camera writes beside it (its GPS position) is left behind.
 */
interface PicturePicker {
    /** The picture chosen, shrunk; null if they backed out. Throws with a message to show if it can't be read. */
    suspend fun pick(): ByteArray?

    /** A JPEG to draw; null if it can't be read. */
    fun decode(jpeg: ByteArray): androidx.compose.ui.graphics.ImageBitmap?
}

/** A picture shared to Familiar from another app, shrunk, waiting for the chat to pick it up. */
object SharedPictures {
    val pending = kotlinx.coroutines.flow.MutableStateFlow<ByteArray?>(null)
}

/** The system's file chooser. Null on a platform that doesn't have one wired up yet. */
interface FilePicker {
    /** What the user picked, or null if they backed out. */
    suspend fun pick(): PickedFile?
}

/**
 * Where the access token and server address are kept: the Android Keystore, the iOS Keychain - never
 * plain preferences (docs/MOBILE.md §1).
 */
/**
 * The phone keeping its own connection to the server while the app's closed, so timers set elsewhere
 * and announcements reach it (BACKLOG item 38). On unless turned off.
 */
interface BackgroundConnection {
    fun isOn(): Boolean

    fun set(on: Boolean)
}

interface SecureStore {
    fun read(key: String): String?

    /** Writes, or with null, removes. */
    fun write(key: String, value: String?)
}

/**
 * The phone's own calendars - every account it syncs (docs/MOBILE.md §2). Null on a platform or
 * build that doesn't have it yet.
 */
interface DeviceCalendar {
    /** May read events (the user granted it). */
    fun canRead(): Boolean

    /** May add events. */
    fun canWrite(): Boolean

    /** Asks the user for access (the system prompt); true when reading, at least, was allowed. */
    suspend fun requestAccess(): Boolean

    /** Events and occurrences between two instants (epoch milliseconds), across visible calendars. */
    suspend fun upcoming(fromMillis: Long, toMillis: Long): List<app.familiar.shared.protocol.DeviceEventView>

    /**
     * Adds an event to the phone's main calendar; returns that calendar's name. Times are ISO-8601
     * instants; an all-day event's are UTC midnights.
     * @throws IllegalStateException when no calendar on the phone can take it.
     */
    suspend fun add(title: String, startsAt: String, endsAt: String, allDay: Boolean, timeZone: String?): String
}

/**
 * The phone's own alarms for the familiar's timers and reminders (docs/MOBILE.md, M3): they ring
 * with no connection at all - in airplane mode, with the server down. Kept in step with the server's
 * list whenever it changes.
 */
interface DeviceAlarms {
    /** Replaces the alarms with these (the account's pending jobs). */
    fun sync(jobs: List<app.familiar.shared.protocol.JobView>, familiarName: String)

    /** A notification for something said while the app is in the background; the same [id] replaces it. */
    fun message(id: String, title: String, body: String) = Unit

    /** Takes such a notification away: the app is back in front, showing it. */
    fun dismissMessage(id: String) = Unit
}

/**
 * The phone's contacts, messages, dialler, maps and location (BACKLOG item 21). It hands off rather
 * than acting: the user taps Send or Call. A failure throws with a message for the familiar to pass on.
 */
interface DevicePhone {
    /** Contacts whose names match [name], with their numbers; null when contacts can't be read (not allowed). */
    suspend fun contacts(name: String): List<PhoneContact>?

    /** Contacts whose names match [name], with their postal addresses; null when contacts can't be read. */
    suspend fun addresses(name: String): List<ContactAddress>?

    /**
     * Opens Messages, the dialler or Maps ready to go - or, when the app isn't on screen (Android won't
     * open another app from the background), puts that in a notification to tap.
     */
    fun handOff(handOff: HandOff): HandedOff

    /** Roughly where the phone is, as a place ("Headingley, Leeds"). */
    suspend fun place(): String

    /**
     * Where the phone is, as latitude and longitude, for picking home in Settings (BACKLOG item 32).
     * Throws with a message to show when it can't say (not allowed, location off).
     */
    suspend fun position(): Pair<Double, Double>

    /** Whether this phone could send texts itself (it has a phone line), for Settings to offer it. */
    val canSendTexts: Boolean get() = false

    /**
     * Whether it sends texts itself, without Messages (BACKLOG item 39): switched on, and allowed. The
     * familiar reads each one back and sends it only when the user says yes.
     */
    fun sendsTexts(): Boolean = false

    /** Switches sending texts itself on (asking for the permission) or off; whether it's on now. */
    suspend fun sendTexts(on: Boolean): Boolean = false

    /** Sends a text; throws with what to tell the user if it didn't go. */
    suspend fun sendText(number: String, message: String): Unit = throw IllegalStateException("This phone can't send texts itself.")
}

/** A contact's number, and what kind it is ("mobile", "work"). */
data class PhoneContact(val name: String, val number: String, val mobile: Boolean, val label: String = if (mobile) "mobile" else "")

/** A contact's postal address, and which it is ("home", "work"). */
data class ContactAddress(val name: String, val address: String, val label: String = "")

sealed interface HandOff {
    data class Text(val to: String, val number: String, val message: String) : HandOff
    data class Call(val to: String, val number: String) : HandOff
    /** [shownAs] is what a notification calls it: "Mum's", not her address. */
    data class Navigate(val destination: String, val mode: String, val shownAs: String = destination) : HandOff
}

enum class HandedOff { Opened, Notified }

/** A store in memory, for previews and tests. */
class InMemoryStore : SecureStore {
    private val values = mutableMapOf<String, String>()

    override fun read(key: String) = values[key]

    override fun write(key: String, value: String?) {
        if (value == null) values.remove(key) else values[key] = value
    }
}
