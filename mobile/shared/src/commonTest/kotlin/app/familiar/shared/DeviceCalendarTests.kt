package app.familiar.shared

import app.familiar.shared.api.FamiliarApi
import app.familiar.shared.conversation.ConversationController
import app.familiar.shared.platform.DeviceCalendar
import app.familiar.shared.protocol.DeviceEventView
import io.ktor.client.HttpClient
import io.ktor.client.engine.HttpClientEngine
import io.ktor.client.engine.mock.MockEngine
import io.ktor.client.engine.mock.respond
import io.ktor.client.engine.mock.toByteArray
import io.ktor.client.plugins.sse.DefaultClientSSESession
import io.ktor.client.plugins.sse.SSECapability
import io.ktor.client.plugins.sse.SSEClientContent
import io.ktor.client.request.HttpResponseData
import io.ktor.http.HttpHeaders
import io.ktor.http.HttpStatusCode
import io.ktor.http.headersOf
import io.ktor.utils.io.ByteReadChannel
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/** MockEngine with the SSE capability declared: Ktor's SSE plugin reads the body itself. */
private class SseMockEngine(private val mock: MockEngine) : HttpClientEngine by mock {
    override val supportedCapabilities = mock.supportedCapabilities + SSECapability

    // Installed as itself, not the mock, so the capability check sees the line above.
    @OptIn(io.ktor.utils.io.InternalAPI::class)
    override fun install(client: HttpClient) = super.install(client)
}

class DeviceCalendarTests {
    private class FakeCalendar : DeviceCalendar {
        val added = MutableStateFlow(listOf<String>())

        override fun canRead() = true

        override fun canWrite() = true

        override suspend fun requestAccess() = true

        override suspend fun upcoming(fromMillis: Long, toMillis: Long) =
            listOf(DeviceEventView("1:0", "School run", null, "2026-10-02T08:00:00Z", "2026-10-02T08:30:00Z", allDay = false, calendar = "sam@gmail.com"))

        override suspend fun add(title: String, startsAt: String, endsAt: String, allDay: Boolean, timeZone: String?): String {
            added.update { it + "$title $startsAt" }
            return "sam@gmail.com"
        }
    }

    private fun invoke(callId: String, connection: String, expires: String = "2099-01-01T00:00:00Z", device: String? = null) =
        """{"callId":"$callId","connectionId":"$connection","tool":"calendar.add","arguments":{"title":"Dentist $callId","startsAt":"2026-10-03T10:00:00Z","endsAt":"2026-10-03T11:00:00Z","allDay":false,"timeZone":"Europe/London"},"expiresAt":"$expires"""" +
            (if (device != null) ""","deviceId":"$device"}""" else "}")

    @OptIn(io.ktor.utils.io.InternalAPI::class)
    @Test
    fun the_phone_adds_what_it_is_asked_once_and_keeps_the_server_up_to_date() = runTest {
        val stream = listOf(
            "ready" to """{"familiar":{"name":"Pip","expressiveness":0.8},"modelAvailable":true,"speechAvailable":false,"connectionId":"c1"}""",
            "tool.invoke" to invoke("old", "c0"),
            "tool.invoke" to invoke("late", "c1", expires = "2020-01-01T00:00:00Z"),
            "tool.invoke" to invoke("x1", "c1"),
            "tool.invoke" to invoke("x1", "c1"),
            // Addressed to this phone: acted on, even though it names a connection that's gone.
            "tool.invoke" to invoke("x2", "c0", device = "D1"),
            "tool.invoke" to invoke("other", "c1", device = "d2"),
        ).mapIndexed { i, (type, data) -> "event: $type\nid: ${i + 1}\ndata: $data\n\n" }.joinToString("")

        val capabilities = MutableStateFlow<String?>(null)
        val results = MutableStateFlow(listOf<String>())
        val uploads = MutableStateFlow(listOf<String>())
        val signedOut = MutableStateFlow(false)
        var streams = 0

        val engine = MockEngine { request ->
            val path = request.url.encodedPath
            when {
                path.endsWith("/stream") && streams++ == 0 -> {
                    capabilities.value = request.url.parameters["capabilities"]
                    // What a real engine hands the SSE plugin: a session reading the body.
                    val response = respond(ByteReadChannel(""), HttpStatusCode.OK, headersOf(HttpHeaders.ContentType, "text/event-stream"))
                    val session = DefaultClientSSESession(request.body as SSEClientContent, ByteReadChannel(stream), Job())
                    HttpResponseData(response.statusCode, response.requestTime, response.headers, response.version, session, response.callContext)
                }
                // Then the device is signed out, which ends the test's stream for good.
                path.endsWith("/stream") -> respond("", HttpStatusCode.Unauthorized)
                path.endsWith("/tool-result") -> {
                    results.update { it + request.body.toByteArray().decodeToString() }
                    respond("", HttpStatusCode.NoContent)
                }
                path.endsWith("/calendars/device") -> {
                    uploads.update { it + request.body.toByteArray().decodeToString() }
                    respond("{}", HttpStatusCode.OK, headersOf(HttpHeaders.ContentType, "application/json"))
                }
                else -> respond("[]", HttpStatusCode.OK, headersOf(HttpHeaders.ContentType, "application/json"))
            }
        }

        val calendar = FakeCalendar()
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        val controller = ConversationController(
            FamiliarApi("https://nas:7280", "fam_x_y", HttpClient(SseMockEngine(engine))),
            scope,
            onSignedOut = { signedOut.value = true },
            calendar = calendar,
            deviceId = "d1",
        )

        try {
            controller.start()
            withContext(Dispatchers.Default) {
                withTimeout(10_000) {
                    signedOut.first { it }
                    results.first { it.size >= 2 }
                    // Once on start, once more after adding.
                    uploads.first { it.size >= 2 }
                    delay(200)
                }
            }
        } finally {
            scope.cancel()
        }

        assertEquals("calendar.write", capabilities.value)
        // Each is carried out on its own, so they can finish in either order.
        assertEquals(setOf("Dentist x1 2026-10-03T10:00:00Z", "Dentist x2 2026-10-03T10:00:00Z"), calendar.added.value.toSet())
        assertEquals(2, results.value.size)
        val result = results.value.single { """"callId":"x1"""" in it }
        assertTrue(""""callId":"x1"""" in result && """"ok":true""" in result && "sam@gmail.com" in result, result)
        assertTrue("School run" in uploads.value.first(), uploads.value.first())
    }
}
