package app.familiar.shared.session

import app.familiar.shared.protocol.NotifyEvent
import app.familiar.shared.protocol.ServerEvent
import kotlin.time.Clock
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Instant

/**
 * What to show as a notification when something arrives on the stream while the app is in the
 * background: a reply, or an announcement (a hook's "speak"). Timers and reminders aren't shown
 * here - the phone's own alarms ring for those.
 *
 * An announcement comes as a `notify` and then the familiar saying it; both are one notification,
 * with the same id the push uses, so a push about the same thing doesn't show it again.
 */
class AwayNotices(private val clock: Clock = Clock.System) {
    data class Notice(val id: String, val title: String, val body: String)

    /** The last `notify`, for the reply that says it. */
    private var lastNotify: Pair<NotifyEvent, Instant>? = null

    fun on(event: ServerEvent, familiar: String): Notice? = when (event) {
        is ServerEvent.Notify -> {
            lastNotify = event.event to clock.now()
            if (event.event.kind == ANNOUNCEMENT) announcement(event.event, familiar, event.event.text) else null
        }

        is ServerEvent.ReplyEnd -> {
            val text = event.end.text.trim()
            val notify = lastNotify?.takeIf { clock.now() - it.second < 1.minutes }?.first
            lastNotify = null
            when {
                text.isEmpty() -> null
                notify == null -> Notice(event.end.id, familiar, text)
                // Its own words for the announcement, in place of the bare message.
                notify.kind == ANNOUNCEMENT -> announcement(notify, familiar, text)
                else -> null
            }
        }

        else -> null
    }

    /** Titled as the server titles it for a push ("Pip: from Home Assistant"). */
    private fun announcement(notify: NotifyEvent, familiar: String, text: String) = Notice(notify.id, "$familiar: from ${notify.label}", text)

    private companion object {
        const val ANNOUNCEMENT = "announcement"
    }
}
