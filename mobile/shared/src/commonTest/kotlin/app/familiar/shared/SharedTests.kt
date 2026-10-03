package app.familiar.shared

import app.familiar.shared.api.ApiException
import app.familiar.shared.api.FamiliarApi
import app.familiar.shared.auth.Session
import app.familiar.shared.auth.SignedIn
import app.familiar.shared.conversation.Connection
import app.familiar.shared.conversation.ConversationReducer
import app.familiar.shared.conversation.ConversationState
import app.familiar.shared.platform.InMemoryStore
import app.familiar.shared.protocol.MessageView
import app.familiar.shared.protocol.ServerEvent
import io.ktor.client.HttpClient
import io.ktor.client.engine.mock.MockEngine
import io.ktor.client.engine.mock.respond
import io.ktor.http.HttpHeaders
import io.ktor.http.HttpStatusCode
import io.ktor.http.headersOf
import io.ktor.utils.io.ByteReadChannel
import kotlinx.coroutines.test.runTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertIs
import kotlin.test.assertNull
import kotlin.test.assertTrue

class ProtocolTests {
    @Test
    fun events_decode_and_ignore_fields_this_app_does_not_know() {
        val ready = ServerEvent.parse("ready", """{"familiar":{"name":"Pip","expressiveness":0.8},"modelAvailable":true,"speechAvailable":false,"newThing":1}""")
        assertEquals("Pip", assertIs<ServerEvent.Ready>(ready).ready.familiar.name)

        val end = ServerEvent.parse("reply.end", """{"id":"r1","text":"Hi!","emotion":"joy","intensity":0.8,"interrupted":false}""")
        assertEquals("joy", assertIs<ServerEvent.ReplyEnd>(end).end.emotion)

        val audio = ServerEvent.parse("audio", """{"replyId":"r1","seq":0,"url":"/api/conversation/audio/c1","text":"Hi.","words":[{"word":"Hi","start":0.0,"end":0.3}]}""")
        assertEquals("Hi", assertIs<ServerEvent.Audio>(audio).clip.words!!.single().word)

        assertIs<ServerEvent.Other>(ServerEvent.parse("something.new", """{"x":1}"""))
        val jobs = ServerEvent.parse("jobs", """{"jobs":[{"id":"j1","kind":"timer","label":"pasta","dueAt":"2026-10-03T18:00:00Z","durationSeconds":600,"createdAt":"2026-10-03T17:50:00Z","ring":true}]}""")
        val timer = assertIs<ServerEvent.Jobs>(jobs).jobs.single()
        assertEquals(Triple("pasta", true, true), Triple(timer.label, timer.isTimer, timer.ring))
        assertIs<ServerEvent.Resync>(ServerEvent.parse("resync", "{}"))
    }
}

class ReducerTests {
    private fun run(vararg events: ServerEvent) = events.fold(ConversationState()) { s, e -> ConversationReducer.apply(s, e) }

    @Test
    fun a_streamed_reply_builds_from_deltas_and_settles_on_end() {
        val state = run(
            ServerEvent.ReplyStart("r1"),
            ServerEvent.ReplyDelta("r1", "Hello"),
            ServerEvent.ReplyDelta("r1", " there"),
        )
        assertEquals("Hello there", state.messages.single().text)
        assertTrue(state.messages.single().streaming)

        val done = ConversationReducer.apply(state, ServerEvent.ReplyEnd(app.familiar.shared.protocol.ReplyEndEvent("r1", "Hello there!", "joy")))
        assertEquals("Hello there!", done.messages.single().text)
        assertEquals(false, done.messages.single().streaming)
    }

    @Test
    fun replayed_events_change_nothing() {
        val message = ServerEvent.Message(MessageView("m1", "user", "hi"))
        assertEquals(1, run(message, message, ServerEvent.ReplyStart("r1"), ServerEvent.ReplyStart("r1")).messages.count { it.id == "m1" })
        assertEquals(2, run(message, message, ServerEvent.ReplyStart("r1"), ServerEvent.ReplyStart("r1")).messages.size)
    }

    @Test
    fun history_keeps_what_the_stream_already_delivered() {
        val live = run(ServerEvent.ReplyStart("r2"))
        val merged = ConversationReducer.history(live, listOf(MessageView("m1", "user", "hi"), MessageView("r1", "assistant", "hello")))
        assertEquals(listOf("m1", "r1", "r2"), merged.messages.map { it.id })
    }

    @Test
    fun ready_opens_the_connection_and_resting_shows_when_the_model_is_away() {
        val ready = ServerEvent.parse("ready", """{"familiar":{"name":"Pip"},"modelAvailable":false,"speechAvailable":false}""")
        val state = run(ready)
        assertEquals(Connection.Open, state.connection)
        assertEquals("resting", state.state)
    }
}

class ServerUrlTests {
    @Test
    fun addresses_are_tidied_and_default_to_https() {
        assertEquals("https://familiar.home:7280", FamiliarApi.normaliseServerUrl(" familiar.home:7280/ "))
        assertEquals("http://192.168.1.20:5280", FamiliarApi.normaliseServerUrl("http://192.168.1.20:5280"))
        assertNull(FamiliarApi.normaliseServerUrl("ftp://nas"))
        assertNull(FamiliarApi.normaliseServerUrl("not a url"))
        assertNull(FamiliarApi.normaliseServerUrl(""))
    }
}

class SessionTests {
    @Test
    fun signing_out_forgets_the_token_but_remembers_the_server() {
        val session = Session(InMemoryStore())
        session.save(SignedIn("https://nas:7280", "fam_x_y", "d1", "sam"))
        assertEquals("sam", session.current()?.username)

        session.clear()
        assertNull(session.current())
        assertEquals("https://nas:7280", session.lastServer())
    }
}

class ApiTests {
    private fun api(status: HttpStatusCode, body: String): FamiliarApi {
        val engine = MockEngine { respond(ByteReadChannel(body), status, headersOf(HttpHeaders.ContentType, "application/json")) }
        return FamiliarApi("https://nas:7280", client = HttpClient(engine))
    }

    @Test
    fun a_device_login_returns_the_token() = runTest {
        val response = api(HttpStatusCode.OK, """
            {"token":"fam_abc_def","device":{"id":"d1","name":"Pixel","createdAt":"2026-10-02T00:00:00Z","expiresAt":"2027-04-02T00:00:00Z","isCurrent":true},
             "account":{"id":"a1","username":"sam","email":"sam@example.test","role":"user"}}
        """.trimIndent()).deviceLogin("sam", "secret", "Pixel")
        assertEquals("fam_abc_def", response.token)
        assertEquals("sam", response.account.username)
    }

    @Test
    fun the_servers_reason_is_what_the_user_sees() = runTest {
        val failure = assertFailsWith<ApiException> {
            api(HttpStatusCode.Unauthorized, """{"error":"Wrong username or password."}""").deviceLogin("sam", "nope", "Pixel")
        }
        assertEquals("Wrong username or password.", failure.message)
        assertTrue(failure.unauthorized)
    }
}
