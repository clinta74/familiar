package app.familiar.shared.api

import app.familiar.shared.protocol.DeviceCalendarUpload
import app.familiar.shared.protocol.DeviceEventView
import app.familiar.shared.protocol.DeviceLoginRequest
import app.familiar.shared.protocol.DeviceLoginResponse
import app.familiar.shared.protocol.ErrorBody
import app.familiar.shared.protocol.FamiliarJson
import app.familiar.shared.protocol.InterruptRequest
import app.familiar.shared.protocol.JobView
import app.familiar.shared.protocol.NotificationView
import app.familiar.shared.protocol.PushKeyResponse
import app.familiar.shared.protocol.SubscribeRequest
import app.familiar.shared.protocol.MessageView
import app.familiar.shared.protocol.SendMessageRequest
import app.familiar.shared.protocol.ServerEvent
import app.familiar.shared.protocol.ToolResultRequest
import io.ktor.client.HttpClient
import io.ktor.client.call.body
import io.ktor.client.plugins.HttpTimeout
import io.ktor.client.plugins.contentnegotiation.ContentNegotiation
import io.ktor.client.plugins.defaultRequest
import io.ktor.client.plugins.sse.SSE
import io.ktor.client.plugins.sse.sse
import io.ktor.client.request.bearerAuth
import io.ktor.client.request.delete
import io.ktor.client.request.get
import io.ktor.client.request.header
import io.ktor.client.request.post
import io.ktor.client.request.put
import io.ktor.client.request.setBody
import io.ktor.client.statement.HttpResponse
import io.ktor.client.statement.bodyAsText
import io.ktor.http.ContentType
import io.ktor.http.content.ByteArrayContent
import io.ktor.http.HttpStatusCode
import io.ktor.http.contentType
import io.ktor.http.isSuccess
import io.ktor.serialization.kotlinx.json.json
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow

/** The server said no, with a reason fit to show the user. */
class ApiException(val status: Int, message: String) : Exception(message) {
    val unauthorized get() = status == HttpStatusCode.Unauthorized.value
}

/** The server couldn't be reached at all. */
class UnreachableException(message: String, cause: Throwable? = null) : Exception(message, cause)

/** One event as it arrived: its id (for Last-Event-ID on reconnect) and what it says. */
data class StreamEvent(val id: String?, val event: ServerEvent)

/**
 * Familiar's HTTP API for a phone: sign-in with a device token (PLAN.md §2), then everything with
 * `Authorization: Bearer` - the event stream included, which a browser's EventSource can't do.
 */
class FamiliarApi(
    val baseUrl: String,
    private val token: String? = null,
    client: HttpClient? = null,
) {
    private val http = (client ?: HttpClient()).config {
        install(ContentNegotiation) { json(FamiliarJson) }
        install(SSE)
        install(HttpTimeout) {
            connectTimeoutMillis = 10_000
            // The stream stays open indefinitely; the server pings every 15 s, so a dead
            // connection is noticed by the read timeout.
            socketTimeoutMillis = 45_000
        }
        expectSuccess = false
        defaultRequest {
            url(baseUrl.trimEnd('/') + "/")
            token?.let { bearerAuth(it) }
        }
    }

    fun withToken(token: String) = FamiliarApi(baseUrl, token, http)

    /** Signs in as this device; the response carries the bearer token to keep. */
    suspend fun deviceLogin(username: String, password: String, deviceName: String): DeviceLoginResponse =
        call {
            http.post("api/auth/devices/login") {
                contentType(ContentType.Application.Json)
                setBody(DeviceLoginRequest(username, password, deviceName))
            }
        }

    /** Signs this device out: the token stops working everywhere. */
    suspend fun revokeDevice(deviceId: String) {
        call<Unit> { http.delete("api/auth/devices/$deviceId") }
    }

    suspend fun history(limit: Int = 50): List<MessageView> = call { http.get("api/conversation/messages?limit=$limit") }

    suspend fun send(text: String, timeZone: String, speak: Boolean = false): MessageView =
        call {
            http.post("api/conversation/messages") {
                contentType(ContentType.Application.Json)
                setBody(SendMessageRequest(text, timeZone, speak))
            }
        }

    /**
     * Something said aloud (push-to-talk), as a WAV. The transcribed message, or null when nothing
     * was heard - the server says so on the stream (`error`, no-speech). The reply is spoken.
     */
    suspend fun sendSpoken(wav: ByteArray, timeZone: String): MessageView? {
        val response = checked {
            http.post("api/conversation/audio?timeZone=$timeZone") {
                setBody(ByteArrayContent(wav, ContentType("audio", "wav")))
            }
        }
        return if (response.status == HttpStatusCode.NoContent) null else response.body()
    }

    suspend fun interrupt(request: InterruptRequest? = null) {
        call<Unit> {
            http.post("api/conversation/interrupt") {
                contentType(ContentType.Application.Json)
                setBody(request ?: InterruptRequest())
            }
        }
    }

    suspend fun startOver() {
        call<Unit> { http.post("api/conversation/new") }
    }

    /** The familiar's profile; the app uses its `appearance` (how the avatar looks). */
    suspend fun familiar(): kotlinx.serialization.json.JsonObject = call { http.get("api/familiar") }

    /** An uploaded VRM model, for the avatar page, which can't fetch it itself. */
    suspend fun avatarModel(id: String): ByteArray = call { http.get("api/avatars/$id/model") }

    /** A spoken sentence, by the url its `audio` event gave. */
    suspend fun clip(url: String): ByteArray = call { http.get(url.trimStart('/')) }

    /** The pending timers and reminders. */
    suspend fun jobs(): List<JobView> = call { http.get("api/jobs") }

    /** What went off in the last few minutes (for a push, which says only that something did). */
    suspend fun recentNotifications(): List<NotificationView> = call { http.get("api/notifications/recent") }

    /** The server's VAPID key: only pushes signed with it reach this phone. */
    suspend fun pushKey(): String = call<PushKeyResponse> { http.get("api/push/key") }.publicKey

    /** This phone's push endpoint (on the household's ntfy); replaces the last one. */
    suspend fun subscribePush(endpoint: String) {
        call<Unit> {
            http.post("api/push/subscriptions") {
                contentType(ContentType.Application.Json)
                setBody(SubscribeRequest(endpoint))
            }
        }
    }

    /** The phone's calendars for the next while, replacing what it uploaded last time. */
    suspend fun uploadDeviceCalendar(events: List<DeviceEventView>) {
        call<Unit> {
            http.put("api/calendars/device") {
                contentType(ContentType.Application.Json)
                setBody(DeviceCalendarUpload(events))
            }
        }
    }

    /** This phone's answer to a `tool.invoke`. */
    suspend fun toolResult(result: ToolResultRequest) {
        call<Unit> {
            http.post("api/conversation/tool-result") {
                contentType(ContentType.Application.Json)
                setBody(result)
            }
        }
    }

    /**
     * The conversation stream, until it ends or fails. With [lastEventId] (a reconnect), the server
     * replays what was missed, or sends `resync` when it can't.
     */
    fun events(
        timeZone: String,
        lastEventId: String?,
        capabilities: Set<String> = emptySet(),
        /** Speak the hello, if there is one. */
        speak: Boolean = false,
    ): Flow<StreamEvent> = flow {
        val caps = (if (capabilities.isEmpty()) "" else "&capabilities=" + capabilities.sorted().joinToString(",")) +
            (if (speak) "&speak=true" else "")
        try {
            http.sse(
                urlString = "api/conversation/stream?timeZone=$timeZone$caps",
                request = { lastEventId?.let { header("Last-Event-ID", it) } },
            ) {
                incoming.collect { event ->
                    emit(StreamEvent(event.id, ServerEvent.parse(event.event, event.data)))
                }
            }
        } catch (exception: io.ktor.client.plugins.sse.SSEClientException) {
            val status = exception.response?.status?.value
            if (status != null && status >= 400) throw ApiException(status, describe(status, exception.response!!.bodyAsTextOrNull()))
            throw UnreachableException("Lost the connection to the server.", exception)
        }
    }

    private suspend inline fun <reified T> call(request: () -> HttpResponse): T {
        val response = checked(request)

        @Suppress("UNCHECKED_CAST")
        return if (T::class == Unit::class) Unit as T else response.body()
    }

    /** The response, once it's known to be a success; otherwise the server's reason, thrown. */
    private suspend inline fun checked(request: () -> HttpResponse): HttpResponse {
        val response = try {
            request()
        } catch (exception: ApiException) {
            throw exception
        } catch (exception: Exception) {
            throw UnreachableException("Couldn't reach the server at $baseUrl.", exception)
        }

        if (!response.status.isSuccess()) {
            throw ApiException(response.status.value, describe(response.status.value, response.bodyAsTextOrNull()))
        }
        return response
    }

    private suspend fun HttpResponse.bodyAsTextOrNull(): String? = try {
        bodyAsText()
    } catch (_: Exception) {
        null
    }

    companion object {
        /** The server's own explanation if it gave one, else a plain one for the status. */
        fun describe(status: Int, body: String?): String {
            val explained = body?.let {
                try {
                    FamiliarJson.decodeFromString<ErrorBody>(it).let { e -> e.error ?: e.title }
                } catch (_: Exception) {
                    null
                }
            }
            return explained ?: when (status) {
                401 -> "Please sign in again."
                403 -> "You can't do that."
                404 -> "That isn't a Familiar server (nothing at that address)."
                429 -> "Too many attempts. Wait a moment and try again."
                in 500..599 -> "The server had a problem ($status)."
                else -> "Something went wrong ($status)."
            }
        }

        /**
         * What the user typed, as a base URL: a scheme added if missing (https - an address
         * without one is assumed to be the NAS), and no trailing slash.
         */
        fun normaliseServerUrl(input: String): String? {
            val trimmed = input.trim().trimEnd('/')
            if (trimmed.isEmpty() || trimmed.contains(' ')) return null
            val withScheme = if (trimmed.contains("://")) trimmed else "https://$trimmed"
            val scheme = withScheme.substringBefore("://").lowercase()
            if (scheme != "http" && scheme != "https") return null
            val host = withScheme.substringAfter("://").substringBefore('/').substringBefore(':')
            return if (host.isEmpty()) null else "$scheme://${withScheme.substringAfter("://")}"
        }
    }
}
