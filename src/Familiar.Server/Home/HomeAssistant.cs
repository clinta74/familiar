using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Familiar.Agent.Tools;
using Familiar.Domain.Hooks;
using Familiar.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Familiar.Server.Home;

/// <summary>A device exposed to Assist in Home Assistant.</summary>
/// <param name="Id">Its entity id, e.g. light.kitchen.</param>
/// <param name="Name">What it's called there.</param>
/// <param name="Area">The room it's in, if it's in one.</param>
public sealed record HomeDevice(string Id, string Name, string? Area);

/// <summary>What Assist made of a command.</summary>
/// <param name="Ok">It understood and did it (or answered).</param>
/// <param name="Speech">What Assist said back, e.g. "Turned on the light".</param>
/// <param name="Code">Why not, when it didn't: no_intent_match, no_valid_targets, failed_to_handle.</param>
/// <param name="Done">The devices or areas it worked.</param>
/// <param name="Failed">The ones it couldn't.</param>
public sealed record AssistAnswer(bool Ok, string Speech, string? Code, IReadOnlyList<string> Done, IReadOnlyList<string> Failed);

/// <summary>Couldn't reach Home Assistant, or it refused: the message says which, for the user.</summary>
public sealed class HomeAssistantException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Opens Home Assistant's websocket: its own seam so tests can hand one over.</summary>
public class HomeAssistantSockets
{
    public virtual async Task<WebSocket> ConnectAsync(Uri uri, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(uri, ct);
        return socket;
    }
}

/// <summary>
/// The account's Home Assistant (BACKLOG item 29). Commands go to Assist - Home Assistant's own
/// voice-command handling, its built-in agent rather than any LLM one set up there - so it reaches
/// exactly the devices exposed to Assist, by their names, aliases and rooms, and understands
/// "dim the lounge to 30%" or "is the garage door open" without the familiar knowing entity ids.
/// The exposed list (the websocket; it needs an administrator's token) is only for the model's
/// sake, so it knows what's there, and for "check", which reads their states.
/// </summary>
public sealed partial class HomeAssistant(
    IDbContextFactory<FamiliarDbContext> db,
    IDataProtectionProvider protection,
    IHttpClientFactory httpFactory,
    HomeAssistantSockets sockets)
{
    /// <summary>At most this many devices are remembered: the list goes into the tool's description.</summary>
    public const int MaxDevices = 80;

    /// <summary>Assist's built-in agent: local, quick, and the same answer every time.</summary>
    public const string AgentId = "conversation.home_assistant";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal IDataProtector Protector { get; } = protection.CreateProtector("Familiar.Home.Assistant");

    public static IReadOnlyList<HomeDevice> DevicesOf(HomeAssistantLink link) =>
        JsonSerializer.Deserialize<List<HomeDevice>>(link.DevicesJson, Json) ?? [];

    /// <summary>The account's Home Assistant, if it has one.</summary>
    public async Task<HomeAssistantLink?> LinkAsync(Guid accountId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        return await context.HomeAssistantLinks.AsNoTracking().FirstOrDefaultAsync(h => h.AccountId == accountId, ct);
    }

    /// <summary>The <c>home</c> tool, for an account that's connected Home Assistant.</summary>
    public async Task<IReadOnlyList<ITool>> ToolsForAsync(Guid accountId, CancellationToken ct) =>
        await LinkAsync(accountId, ct) is { } link ? [new HomeTool(this, link)] : [];

    /// <summary>Checks the address and token answer: throws with what's wrong if they don't.</summary>
    public async Task CheckAsync(Uri url, string token, CancellationToken ct)
    {
        using var response = await SendAsync(url, token, HttpMethod.Get, "api/", null, ct);
    }

    /// <summary>
    /// The devices exposed to Assist, named and in their rooms. Throws if it can't see which are
    /// exposed (a token from a user who isn't an administrator can't).
    /// </summary>
    public async Task<IReadOnlyList<HomeDevice>> ExposedAsync(Uri url, string token, CancellationToken ct)
    {
        var ids = (await ExposedIdsAsync(url, token, ct)).Take(MaxDevices).ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var found = await TemplateAsync(url, token, ids,
            """{"id": s.entity_id, "name": s.name, "area": area_name(s.entity_id)}""", ct);
        return [.. found
            .Select(d => new HomeDevice(d["id"]!.GetValue<string>(), d["name"]?.GetValue<string>() ?? d["id"]!.GetValue<string>(), d["area"]?.GetValue<string>()))
            .OrderBy(d => d.Area ?? "￿", StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Hands a command to Assist, as if said to Home Assistant's own voice assistant.</summary>
    public async Task<AssistAnswer> AssistAsync(HomeAssistantLink link, string text, CancellationToken ct)
    {
        var (url, token) = Open(link);
        using var response = await SendAsync(url, token, HttpMethod.Post, "api/conversation/process",
            new JsonObject { ["text"] = text, ["agent_id"] = AgentId }, ct);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>(ct) ?? [];
        var answer = body["response"] as JsonObject ?? [];
        var speech = answer["speech"]?["plain"]?["speech"]?.GetValue<string>() ?? "";
        var type = answer["response_type"]?.GetValue<string>();
        var data = answer["data"] as JsonObject;
        static IReadOnlyList<string> Names(JsonNode? list) =>
            list is JsonArray items ? [.. items.Select(i => i?["name"]?.GetValue<string>()).OfType<string>()] : [];

        return type == "error"
            ? new AssistAnswer(false, speech, data?["code"]?.GetValue<string>() ?? "unknown", [], [])
            : new AssistAnswer(true, speech, null, Names(data?["success"]), Names(data?["failed"]));
    }

    /// <summary>How the given devices are now: their state and the few details worth saying.</summary>
    public async Task<IReadOnlyList<JsonObject>> StatesAsync(HomeAssistantLink link, IReadOnlyList<HomeDevice> devices, CancellationToken ct)
    {
        var (url, token) = Open(link);
        return await TemplateAsync(url, token, [.. devices.Select(d => d.Id)],
            """
            {"id": s.entity_id, "state": s.state, "unit": s.attributes.get('unit_of_measurement'),
             "brightness": s.attributes.get('brightness'), "target": s.attributes.get('temperature'),
             "current": s.attributes.get('current_temperature'), "position": s.attributes.get('current_position'),
             "playing": s.attributes.get('media_title')}
            """, ct);
    }

    private (Uri Url, string Token) Open(HomeAssistantLink link) =>
        (new Uri(Protector.Unprotect(link.ProtectedUrl)), Protector.Unprotect(link.ProtectedToken));

    /// <summary>
    /// Renders a template over the given entities, each as [shape], and reads the JSON list back:
    /// names, rooms and states in one request rather than every entity in the house.
    /// </summary>
    private async Task<List<JsonObject>> TemplateAsync(Uri url, string token, IReadOnlyList<string> ids, string shape, CancellationToken ct)
    {
        // Entity ids are domain.object_id, letters, digits and underscores: safe to write into it.
        var safe = ids.Where(id => EntityId().IsMatch(id)).ToList();
        var template = "{% set ids = " + JsonSerializer.Serialize(safe) + " %}"
            + "[{% for s in states if s.entity_id in ids %}{{ " + shape.ReplaceLineEndings(" ") + " | tojson }}"
            + "{{ ',' if not loop.last }}{% endfor %}]";
        using var response = await SendAsync(url, token, HttpMethod.Post, "api/template", new JsonObject { ["template"] = template }, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return JsonNode.Parse(text) is JsonArray items ? [.. items.OfType<JsonObject>()] : [];
        }
        catch (JsonException exception)
        {
            throw new HomeAssistantException("Home Assistant's answer didn't make sense.", exception);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri url, string token, HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        HttpResponseMessage response;
        try
        {
            response = await httpFactory.CreateClient(nameof(HomeAssistant)).SendAsync(request, ct);
        }
        catch (Exception exception) when (exception is HttpRequestException || (exception is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new HomeAssistantException($"Couldn't reach Home Assistant at {url.Host}: {exception.Message}", exception);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        response.Dispose();
        throw new HomeAssistantException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Home Assistant refused the token. Make a new long-lived access token in your Home Assistant profile.",
            HttpStatusCode.NotFound => "That address answers, but not as Home Assistant. Give its address without a path, e.g. http://homeassistant.local:8123.",
            _ => $"Home Assistant answered {(int)response.StatusCode}.",
        });
    }

    /// <summary>The entity ids exposed to Assist, from the websocket API.</summary>
    private async Task<IReadOnlyList<string>> ExposedIdsAsync(Uri url, string token, CancellationToken ct)
    {
        var socketUri = new UriBuilder(new Uri(url, "api/websocket")) { Scheme = url.Scheme == Uri.UriSchemeHttps ? "wss" : "ws" }.Uri;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var socket = await sockets.ConnectAsync(socketUri, timeout.Token);
            await ReceiveAsync(socket, timeout.Token); // auth_required
            await SendAsync(socket, new JsonObject { ["type"] = "auth", ["access_token"] = token }, timeout.Token);
            var auth = await ReceiveAsync(socket, timeout.Token);
            if (auth["type"]?.GetValue<string>() != "auth_ok")
            {
                throw new HomeAssistantException("Home Assistant refused the token. Make a new long-lived access token in your Home Assistant profile.");
            }

            await SendAsync(socket, new JsonObject { ["id"] = 1, ["type"] = "homeassistant/expose_entity/list" }, timeout.Token);
            JsonObject result;
            do
            {
                result = await ReceiveAsync(socket, timeout.Token);
            }
            while (result["id"]?.GetValue<int>() != 1);

            if (result["success"]?.GetValue<bool>() != true)
            {
                throw new HomeAssistantException(result["error"]?["code"]?.GetValue<string>() == "unauthorized"
                    ? "Familiar can work your devices, but can't see which are exposed: that needs a token from an administrator."
                    : $"Home Assistant wouldn't say which devices are exposed: {result["error"]?["message"]?.GetValue<string>()}");
            }

            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            return [.. (result["result"]?["exposed_entities"] as JsonObject ?? [])
                .Where(e => e.Value?["conversation"]?.GetValue<bool>() == true)
                .Select(e => e.Key)
                .Order(StringComparer.Ordinal)];
        }
        catch (Exception exception) when (exception is WebSocketException or JsonException
            || (exception is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new HomeAssistantException($"Couldn't ask Home Assistant which devices are exposed: {exception.Message}", exception);
        }
    }

    private static Task SendAsync(WebSocket socket, JsonObject message, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, ct);

    private static async Task<JsonObject> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(chunk, ct);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("Home Assistant closed the connection.");
            }

            buffer.Write(chunk, 0, received.Count);
            if (buffer.Length > 4 * 1024 * 1024)
            {
                throw new WebSocketException("Home Assistant's answer was too long.");
            }
        }
        while (!received.EndOfMessage);

        return JsonNode.Parse(buffer.ToArray()) as JsonObject ?? throw new JsonException("Not a JSON object.");
    }

    [GeneratedRegex("^[a-z0-9_]+\\.[a-z0-9_]+$")]
    private static partial Regex EntityId();
}
