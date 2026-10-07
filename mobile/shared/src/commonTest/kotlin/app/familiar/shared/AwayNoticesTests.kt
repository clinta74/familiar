package app.familiar.shared

import app.familiar.shared.protocol.NotifyEvent
import app.familiar.shared.protocol.ReplyEndEvent
import app.familiar.shared.protocol.ServerEvent
import app.familiar.shared.session.AwayNotices
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.time.Clock
import kotlin.time.Duration
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Instant

class AwayNoticesTests {
    private class FakeClock : Clock {
        var now = Instant.parse("2026-10-06T12:00:00Z")
        override fun now() = now
        fun pass(by: Duration) { now += by }
    }

    private fun end(id: String, text: String) = ServerEvent.ReplyEnd(ReplyEndEvent(id, text, "neutral", 0.5, interrupted = false))

    @Test
    fun a_reply_is_a_notice_from_the_familiar() {
        val notice = AwayNotices(FakeClock()).on(end("r1", "Dinner's at seven."), "Pip")
        assertEquals(AwayNotices.Notice("r1", "Pip", "Dinner's at seven."), notice)
    }

    @Test
    fun an_announcement_and_the_reply_saying_it_are_one_notification() {
        val notices = AwayNotices(FakeClock())
        val first = notices.on(ServerEvent.Notify(NotifyEvent("n1", "announcement", "Home Assistant", "The washing's done")), "Pip")
        val then = notices.on(end("r1", "Your washing's finished!"), "Pip")

        assertEquals(AwayNotices.Notice("n1", "Pip: from Home Assistant", "The washing's done"), first)
        // The same id, so it replaces the first - and matches the push's, so that doesn't add another.
        assertEquals(AwayNotices.Notice("n1", "Pip: from Home Assistant", "Your washing's finished!"), then)
    }

    @Test
    fun timers_and_reminders_are_left_to_the_phones_alarms() {
        val notices = AwayNotices(FakeClock())
        assertNull(notices.on(ServerEvent.Notify(NotifyEvent("j1", "timer", "pasta", "Pasta's done")), "Pip"))
        assertNull(notices.on(end("r1", "Your pasta timer's done!"), "Pip"))
    }

    @Test
    fun a_reply_long_after_an_announcement_is_its_own() {
        val clock = FakeClock()
        val notices = AwayNotices(clock)
        notices.on(ServerEvent.Notify(NotifyEvent("n1", "announcement", "Home Assistant", "Door open")), "Pip")
        clock.pass(5.minutes)
        assertEquals("r2", notices.on(end("r2", "Anything else?"), "Pip")?.id)
    }

    @Test
    fun an_empty_reply_is_nothing_to_show() {
        assertNull(AwayNotices(FakeClock()).on(end("r1", "  "), "Pip"))
    }
}
