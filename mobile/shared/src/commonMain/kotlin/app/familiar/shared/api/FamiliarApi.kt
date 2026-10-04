package app.familiar.shared.api

import app.familiar.shared.protocol.AccountResponse
import app.familiar.shared.protocol.AddFeedRequest
import app.familiar.shared.protocol.AvatarView
import app.familiar.shared.protocol.CalendarView
import app.familiar.shared.protocol.ChangePasswordRequest
import app.familiar.shared.protocol.DeviceCalendarUpload
import app.familiar.shared.protocol.DeviceEventView
import app.familiar.shared.protocol.DeviceLoginRequest
import app.familiar.shared.protocol.DeviceLoginResponse
import app.familiar.shared.protocol.DeviceSummary
import app.familiar.shared.protocol.ErrorBody
import app.familiar.shared.protocol.EventView
import app.familiar.shared.protocol.FamiliarJson
import app.familiar.shared.protocol.FamiliarView
import app.familiar.shared.protocol.HookTokenView
import app.familiar.shared.protocol.InterruptRequest
import app.familiar.shared.protocol.JobView
import app.familiar.shared.protocol.McpServerRequest
import app.familiar.shared.protocol.McpServerView
import app.familiar.shared.protocol.McpToolsRequest
import app.familiar.shared.protocol.MemoryRequest
import app.familiar.shared.protocol.MemoryView
import app.familiar.shared.protocol.NameRequest
import app.familiar.shared.protocol.NewHookTokenView
import app.familiar.shared.protocol.NewWebhookView
import app.familiar.shared.protocol.NotificationView
import app.familiar.shared.protocol.PushKeyResponse
import app.familiar.shared.protocol.SubscribeRequest
import app.familiar.shared.protocol.MessageView
import app.familiar.shared.protocol.SendMessageRequest
import app.familiar.shared.protocol.ServerEvent
import app.familiar.shared.protocol.ToolResultRequest
import app.familiar.shared.protocol.VoicesView
import app.familiar.shared.protocol.WebhookRequest
import app.familiar.shared.protocol.WebhookTestResult
import app.familiar.shared.protocol.WebhookView
import io.ktor.http.encodeURLParameter
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

    // --- Settings and the studio: everything the web's Settings page does but user admin ---------

    /** Who the familiar is, how it sounds and how it looks. */
    suspend fun profile(): FamiliarView = call { http.get("api/familiar") }

    /** Saves the whole profile; the server's copy (validated) comes back. */
    suspend fun saveProfile(profile: FamiliarView): FamiliarView = put("api/familiar", profile)

    suspend fun voices(): VoicesView = call { http.get("api/familiar/voices") }

    /** A sentence in [voice] at [speed], as audio, to hear before choosing it. */
    suspend fun voicePreview(voice: String, speed: Double): ByteArray =
        call { http.get("api/familiar/voice-preview?voice=${voice.encodeURLParameter()}&speed=$speed") }

    /** The VRM models this account uploaded. */
    suspend fun avatars(): List<AvatarView> = call { http.get("api/avatars") }

    /** Uploads a VRM model (up to 40 MB); it's offered in the studio from then on. */
    suspend fun uploadAvatar(fileName: String, bytes: ByteArray): AvatarView =
        call {
            http.post("api/avatars?fileName=${fileName.encodeURLParameter()}") {
                setBody(ByteArrayContent(bytes, ContentType("model", "gltf-binary")))
            }
        }

    /** Deletes an uploaded model; a familiar wearing it goes back to the llama. */
    suspend fun deleteAvatar(id: String) = delete("api/avatars/$id")

    suspend fun account(): AccountResponse = call { http.get("api/auth/me") }

    /** Every other browser and phone is signed out; this one stays signed in. */
    suspend fun changePassword(current: String, new: String) = post("api/auth/password", ChangePasswordRequest(current, new))

    /** The phones signed in to this account, this one marked. */
    suspend fun devices(): List<DeviceSummary> = call { http.get("api/auth/devices") }

    suspend fun memories(): List<MemoryView> = call { http.get("api/memories") }

    suspend fun addMemory(text: String): MemoryView = postFor("api/memories", MemoryRequest(text))

    suspend fun updateMemory(id: String, text: String): MemoryView = put("api/memories/$id", MemoryRequest(text))

    suspend fun forgetMemory(id: String) = delete("api/memories/$id")

    suspend fun forgetEverything() = delete("api/memories")

    suspend fun calendars(): List<CalendarView> = call { http.get("api/calendars") }

    /** Subscribes to a feed by its (secret) iCal address; fetched straight away. */
    suspend fun addCalendar(name: String, url: String): CalendarView = postFor("api/calendars", AddFeedRequest(name, url))

    suspend fun syncCalendar(id: String): CalendarView = call { http.post("api/calendars/$id/sync") }

    suspend fun removeCalendar(id: String) = delete("api/calendars/$id")

    /** The next [days] days, across every calendar. */
    suspend fun events(days: Int = 7): List<EventView> = call { http.get("api/calendars/events?days=$days") }

    /** Deletes an event from the familiar's own calendar. */
    suspend fun deleteEvent(id: String) = delete("api/calendars/events/$id")

    suspend fun hookTokens(): List<HookTokenView> = call { http.get("api/hooks/tokens") }

    /** A new token, shown once. */
    suspend fun createHookToken(name: String): NewHookTokenView = postFor("api/hooks/tokens", NameRequest(name))

    suspend fun revokeHookToken(id: String) = delete("api/hooks/tokens/$id")

    suspend fun webhooks(): List<WebhookView> = call { http.get("api/webhooks") }

    /** A new webhook and its signing secret, shown once. */
    suspend fun createWebhook(request: WebhookRequest): NewWebhookView = postFor("api/webhooks", request)

    suspend fun testWebhook(id: String): WebhookTestResult = call { http.post("api/webhooks/$id/test") }

    suspend fun deleteWebhook(id: String) = delete("api/webhooks/$id")

    suspend fun mcpServers(): List<McpServerView> = call { http.get("api/mcp") }

    /** Connects and lists its tools; a connection problem comes back as the server's `lastError`. */
    suspend fun addMcpServer(request: McpServerRequest): McpServerView = postFor("api/mcp", request)

    suspend fun refreshMcpServer(id: String): McpServerView = call { http.post("api/mcp/$id/refresh") }

    /** Which of its tools the familiar may use. */
    suspend fun setMcpTools(id: String, enabled: List<String>): McpServerView = put("api/mcp/$id/tools", McpToolsRequest(enabled))

    suspend fun deleteMcpServer(id: String) = delete("api/mcp/$id")

    private suspend inline fun <reified B : Any> post(path: String, body: B) {
        call<Unit> {
            http.post(path) {
                contentType(ContentType.Application.Json)
                setBody(body)
            }
        }
    }

    private suspend inline fun <reified B : Any, reified T> postFor(path: String, body: B): T =
        call {
            http.post(path) {
                contentType(ContentType.Application.Json)
                setBody(body)
            }
        }

    private suspend inline fun <reified B : Any, reified T> put(path: String, body: B): T =
        call {
            http.put(path) {
                contentType(ContentType.Application.Json)
                setBody(body)
            }
        }

    private suspend fun delete(path: String) {
        call<Unit> { http.delete(path) }
    }

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
