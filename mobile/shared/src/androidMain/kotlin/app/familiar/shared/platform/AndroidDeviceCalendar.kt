package app.familiar.shared.platform

import android.Manifest
import android.content.ContentUris
import android.content.ContentValues
import android.content.Context
import android.content.pm.PackageManager
import android.provider.CalendarContract
import app.familiar.shared.protocol.DeviceEventView
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.time.Instant

/**
 * The phone's calendars through the system calendar provider: every account the phone syncs
 * (Google, Exchange, a local one) without signing in to any of them here.
 */
class AndroidDeviceCalendar(
    context: Context,
    /** Shows the system permission prompt for READ_CALENDAR and WRITE_CALENDAR. */
    private val askPermission: suspend () -> Unit,
) : DeviceCalendar {
    private val context = context.applicationContext
    private val resolver = this.context.contentResolver

    override fun canRead() = granted(Manifest.permission.READ_CALENDAR)

    override fun canWrite() = granted(Manifest.permission.WRITE_CALENDAR)

    override suspend fun requestAccess(): Boolean {
        if (!canRead() || !canWrite()) askPermission()
        return canRead()
    }

    override suspend fun upcoming(fromMillis: Long, toMillis: Long): List<DeviceEventView> = withContext(Dispatchers.IO) {
        if (!canRead()) return@withContext emptyList()
        val uri = CalendarContract.Instances.CONTENT_URI.buildUpon().also {
            ContentUris.appendId(it, fromMillis)
            ContentUris.appendId(it, toMillis)
        }.build()
        val projection = arrayOf(
            CalendarContract.Instances.EVENT_ID,
            CalendarContract.Instances.BEGIN,
            CalendarContract.Instances.END,
            CalendarContract.Instances.TITLE,
            CalendarContract.Instances.EVENT_LOCATION,
            CalendarContract.Instances.ALL_DAY,
            CalendarContract.Instances.CALENDAR_DISPLAY_NAME,
        )
        // Visible calendars only, and not what the user declined or was cancelled.
        val selection = "${CalendarContract.Instances.VISIBLE} = 1" +
            " AND ${CalendarContract.Instances.SELF_ATTENDEE_STATUS} != ${CalendarContract.Attendees.ATTENDEE_STATUS_DECLINED}" +
            " AND (${CalendarContract.Instances.STATUS} IS NULL OR ${CalendarContract.Instances.STATUS} != ${CalendarContract.Instances.STATUS_CANCELED})"

        val events = mutableListOf<DeviceEventView>()
        resolver.query(uri, projection, selection, null, "${CalendarContract.Instances.BEGIN} ASC")?.use { c ->
            while (c.moveToNext() && events.size < MAX_EVENTS) {
                val begin = c.getLong(1)
                // All-day instances are already UTC midnights, as the server keeps them.
                events += DeviceEventView(
                    externalId = "${c.getLong(0)}:$begin",
                    title = c.getString(3)?.takeIf { it.isNotBlank() } ?: "(untitled)",
                    location = c.getString(4)?.takeIf { it.isNotBlank() },
                    startsAt = Instant.ofEpochMilli(begin).toString(),
                    endsAt = Instant.ofEpochMilli(maxOf(c.getLong(2), begin)).toString(),
                    allDay = c.getInt(5) == 1,
                    calendar = c.getString(6),
                )
            }
        }
        events
    }

    override suspend fun add(title: String, startsAt: String, endsAt: String, allDay: Boolean, timeZone: String?): String =
        withContext(Dispatchers.IO) {
            check(canWrite()) { "Familiar isn't allowed to add to this phone's calendar." }
            val (id, name) = primaryCalendar() ?: throw IllegalStateException("There's no calendar on this phone to add to.")
            val values = ContentValues().apply {
                put(CalendarContract.Events.CALENDAR_ID, id)
                put(CalendarContract.Events.TITLE, title)
                put(CalendarContract.Events.DTSTART, Instant.parse(startsAt).toEpochMilli())
                put(CalendarContract.Events.DTEND, Instant.parse(endsAt).toEpochMilli())
                put(CalendarContract.Events.ALL_DAY, if (allDay) 1 else 0)
                // The provider requires UTC for all-day events.
                put(CalendarContract.Events.EVENT_TIMEZONE, if (allDay) "UTC" else timeZone ?: currentTimeZoneId())
            }
            resolver.insert(CalendarContract.Events.CONTENT_URI, values)
                ?: throw IllegalStateException("The phone's calendar didn't take the event.")
            name
        }

    /** The calendar new events belong in: the user's primary one, Google's first, among those that can be written. */
    private fun primaryCalendar(): Pair<Long, String>? {
        val projection = arrayOf(
            CalendarContract.Calendars._ID,
            CalendarContract.Calendars.CALENDAR_DISPLAY_NAME,
            CalendarContract.Calendars.IS_PRIMARY,
            CalendarContract.Calendars.ACCOUNT_TYPE,
        )
        val selection = "${CalendarContract.Calendars.VISIBLE} = 1" +
            " AND ${CalendarContract.Calendars.CALENDAR_ACCESS_LEVEL} >= ${CalendarContract.Calendars.CAL_ACCESS_CONTRIBUTOR}"
        var best: Triple<Int, Long, String>? = null
        resolver.query(CalendarContract.Calendars.CONTENT_URI, projection, selection, null, null)?.use { c ->
            while (c.moveToNext()) {
                val score = (if (c.getInt(2) == 1) 2 else 0) + (if (c.getString(3) == "com.google") 1 else 0)
                if (best == null || score > best!!.first) best = Triple(score, c.getLong(0), c.getString(1) ?: "Calendar")
            }
        }
        return best?.let { it.second to it.third }
    }

    private fun granted(permission: String) =
        context.checkSelfPermission(permission) == PackageManager.PERMISSION_GRANTED

    private companion object {
        /** The server takes up to 2000 (IcsFeed.MaxOccurrences); two weeks is rarely near it. */
        const val MAX_EVENTS = 1000
    }
}
