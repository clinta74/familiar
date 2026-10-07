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

/** The system's file chooser. Null on a platform that doesn't have one wired up yet. */
interface FilePicker {
    /** What the user picked, or null if they backed out. */
    suspend fun pick(): PickedFile?
}

/**
 * Where the access token and server address are kept: the Android Keystore, the iOS Keychain - never
 * plain preferences (docs/MOBILE.md §1).
 */
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

/** A store in memory, for previews and tests. */
class InMemoryStore : SecureStore {
    private val values = mutableMapOf<String, String>()

    override fun read(key: String) = values[key]

    override fun write(key: String, value: String?) {
        if (value == null) values.remove(key) else values[key] = value
    }
}
