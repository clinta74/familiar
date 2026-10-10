using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Familiar.Agent.Llm;
using Familiar.Server.Conversation;
using Familiar.Server.Home;
using Familiar.Server.Tests.Infrastructure;
using Familiar.Server.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Familiar.Server.Tests;

/// <summary>BACKLOG item 29: the familiar works the devices exposed to Assist in Home Assistant.</summary>
[Collection(PostgresCollection.Name)]
public sealed partial class HomeAssistantTests(PostgresFixture postgres)
{
    private const string Token = "ha-test-token";

    /// <summary>
    /// A small house, as Home Assistant would answer: a kitchen light and a front door lock exposed
    /// to Assist, a heater that isn't. Assist knows the light and the lock by name.
    /// </summary>
    private sealed class House : IAsyncDisposable
    {
        private readonly WebApplication _app;

        public List<(string Text, string? Agent)> Commands { get; } = [];

        private House(WebApplication app) => _app = app;

        public TestServer Server => _app.GetTestServer();

        public static async Task<House> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            var app = builder.Build();
            var house = new House(app);
            app.UseWebSockets();

            bool Authorized(HttpContext http) => http.Request.Headers.Authorization == $"Bearer {Token}";
            var states = new Dictionary<string, JsonObject>
            {
                ["light.kitchen"] = new() { ["id"] = "light.kitchen", ["name"] = "Kitchen light", ["area"] = "Kitchen", ["state"] = "on", ["brightness"] = 102 },
                ["lock.front_door"] = new() { ["id"] = "lock.front_door", ["name"] = "Front door", ["area"] = "Hall", ["state"] = "locked" },
                ["switch.heater"] = new() { ["id"] = "switch.heater", ["name"] = "Heater", ["area"] = "Shed", ["state"] = "off" },
            };

            app.MapGet("/api/", (HttpContext http) => Authorized(http) ? Results.Ok(new { message = "API running." }) : Results.Unauthorized());
            app.MapPost("/api/conversation/process", async (HttpContext http) =>
            {
                if (!Authorized(http))
                {
                    return Results.Unauthorized();
                }

                var body = (await http.Request.ReadFromJsonAsync<JsonObject>())!;
                var text = body["text"]!.GetValue<string>();
                lock (house.Commands)
                {
                    house.Commands.Add((text, body["agent_id"]?.GetValue<string>()));
                }

                return text.Contains("kitchen", StringComparison.OrdinalIgnoreCase) || text.Contains("front door", StringComparison.OrdinalIgnoreCase)
                    ? Results.Json(Answer("action_done", "Done", new JsonObject { ["success"] = new JsonArray(new JsonObject { ["name"] = "Kitchen light", ["type"] = "entity" }), ["failed"] = new JsonArray() }))
                    : Results.Json(Answer("error", "Sorry, I am not aware of any device called lounge lamp", new JsonObject { ["code"] = "no_valid_targets" }));
            });

            // No Jinja here: the ids from the template's first line, and the shape from what it asks for.
            app.MapPost("/api/template", async (HttpContext http) =>
            {
                if (!Authorized(http))
                {
                    return Results.Unauthorized();
                }

                var template = (await http.Request.ReadFromJsonAsync<JsonObject>())!["template"]!.GetValue<string>();
                var ids = JsonNode.Parse(SetIds().Match(template).Groups[1].Value)!.AsArray().Select(i => i!.GetValue<string>()).ToHashSet();
                var wantsState = template.Contains("s.state", StringComparison.Ordinal);
                var found = new JsonArray([.. states.Values.Where(s => ids.Contains(s["id"]!.GetValue<string>())).Select(s => wantsState
                    ? new JsonObject { ["id"] = s["id"]!.DeepClone(), ["state"] = s["state"]!.DeepClone(), ["brightness"] = s["brightness"]?.DeepClone(), ["unit"] = null }
                    : new JsonObject { ["id"] = s["id"]!.DeepClone(), ["name"] = s["name"]!.DeepClone(), ["area"] = s["area"]!.DeepClone() })]);
                return Results.Text(found.ToJsonString(), "text/plain");
            });

            app.Map("/api/websocket", async (HttpContext http) =>
            {
                using var socket = await http.WebSockets.AcceptWebSocketAsync();
                await SendAsync(socket, new JsonObject { ["type"] = "auth_required" });
                var auth = await ReceiveAsync(socket);
                if (auth["access_token"]?.GetValue<string>() != Token)
                {
                    await SendAsync(socket, new JsonObject { ["type"] = "auth_invalid" });
                    return;
                }

                await SendAsync(socket, new JsonObject { ["type"] = "auth_ok" });
                var ask = await ReceiveAsync(socket);
                await SendAsync(socket, new JsonObject
                {
                    ["id"] = ask["id"]!.DeepClone(),
                    ["type"] = "result",
                    ["success"] = true,
                    ["result"] = new JsonObject
                    {
                        ["exposed_entities"] = new JsonObject
                        {
                            ["light.kitchen"] = new JsonObject { ["conversation"] = true, ["cloud.alexa"] = false },
                            ["lock.front_door"] = new JsonObject { ["conversation"] = true },
                            ["switch.heater"] = new JsonObject { ["conversation"] = false, ["cloud.alexa"] = true },
                        },
                    },
                });
                await socket.ReceiveAsync(new byte[1024], CancellationToken.None);
            });

            await app.StartAsync();
            return house;
        }

        private static JsonObject Answer(string type, string speech, JsonObject data) => new()
        {
            ["response"] = new JsonObject
            {
                ["speech"] = new JsonObject { ["plain"] = new JsonObject { ["speech"] = speech, ["extra_data"] = null } },
                ["response_type"] = type,
                ["data"] = data,
            },
            ["conversation_id"] = "abc",
        };

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }

    private sealed class TestSockets(TestServer server) : HomeAssistantSockets
    {
        public override Task<WebSocket> ConnectAsync(Uri uri, CancellationToken ct) => server.CreateWebSocketClient().ConnectAsync(uri, ct);
    }

    private static Task SendAsync(WebSocket socket, JsonObject message) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonObject> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[16 * 1024];
        var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
        return JsonNode.Parse(buffer.AsSpan(0, received.Count))!.AsObject();
    }

    [GeneratedRegex(@"\{% set ids = (\[.*?\]) %\}")]
    private static partial Regex SetIds();

    [Fact]
    public async Task The_familiar_works_the_devices_exposed_to_assist()
    {
        await using var house = await House.StartAsync();
        var handler = house.Server.CreateHandler();
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync(), services: s =>
        {
            s.AddHttpClient(nameof(HomeAssistant)).ConfigurePrimaryHttpMessageHandler(() => handler);
            s.AddSingleton<HomeAssistantSockets>(new TestSockets(house.Server));
        });
        app.Llm
            .ThenCall("home", """{"what": "do", "command": "turn off the kitchen light"}""").ThenSay("[joy:0.5] Kitchen's dark.")
            .ThenSay("[joy:0.5] I'm setting the kitchen light to fifty percent for you now.")
            .ThenCall("home", """{"what": "do", "command": "set the kitchen light to 50%"}""").ThenSay("[joy:0.5] Done.")
            .ThenCall("home", """{"what": "check everywhere"}""").ThenSay("[relaxed:0.5] The kitchen light's on.")
            .ThenCall("home", """{"what": "do", "command": "turn on the lounge lamp"}""").ThenSay("[sad:0.4] There's no lounge lamp.")
            .ThenCall("home", """{"what": "do", "command": "unlock the front door"}""").ThenSay("[relaxed:0.5] Unlocked.");
        var client = app.CreateClient();
        (await client.RegisterAsync("homeowner")).EnsureSuccessStatusCode();

        Assert.False((await client.GetFromJsonAsync<HomeAssistantView>("/api/home-assistant"))!.Connected);

        // A wrong token is refused when connecting, not found out later.
        var refused = await client.PutAsJsonAsync("/api/home-assistant", new HomeAssistantRequest("http://ha.test:8123", "wrong"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("refused the token", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A pasted profile address is trimmed back; only what's exposed to Assist is listed.
        var connected = await (await client.PutAsJsonAsync("/api/home-assistant", new HomeAssistantRequest("http://ha.test:8123/profile/security", Token))).ReadAsync<HomeAssistantView>();
        Assert.Null(connected.LastError);
        Assert.Equal("ha.test:8123", connected.UrlHost);
        Assert.Equal([new HomeDeviceView("Front door", "Hall"), new HomeDeviceView("Kitchen light", "Kitchen")], connected.Devices);

        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);

        // A command goes to Assist's own agent, without asking first.
        await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("kitchen light off", "UTC"));
        await stream.ReadUntilStateAsync(FamiliarState.Idle);
        var home = app.Llm.LastTools!.Single(t => t.Name == "home");
        Assert.Contains("Hall: Front door. Kitchen: Kitchen light.", home.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("Heater", home.Description, StringComparison.Ordinal);
        Assert.Equal(("turn off the kitchen light", HomeAssistant.AgentId), house.Commands[^1]);
        Assert.Contains("Kitchen light", app.Llm.LastPrompt!.Last(m => m.Role == ChatRole.Tool).Content, StringComparison.Ordinal);

        // Said it was doing it, called nothing (BACKLOG item 30): it's told, and does.
        await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("set it to 50 percent", "UTC"));
        await stream.ReadUntilStateAsync(FamiliarState.Idle);
        Assert.Equal("set the kitchen light to 50%", house.Commands[^1].Text);
        Assert.Contains(app.Llm.LastPrompt!, m => m.Role == ChatRole.System && m.Content == Familiar.Agent.Turns.TurnRunner.ClaimedNote);

        // Checking reads the exposed devices' states - and the model often puts it all in "what".
        await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("did I leave anything on?", "UTC"));
        await stream.ReadUntilStateAsync(FamiliarState.Idle);
        var checkedStates = app.Llm.LastPrompt!.Last(m => m.Role == ChatRole.Tool).Content;
        Assert.Contains("Kitchen light (Kitchen): on, 40%", checkedStates, StringComparison.Ordinal);
        Assert.Contains("Front door (Hall): locked", checkedStates, StringComparison.Ordinal);

        // A name Assist doesn't know comes back with the names it does, for one more try.
        await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("lounge lamp on", "UTC"));
        await stream.ReadUntilStateAsync(FamiliarState.Idle);
        var unknown = app.Llm.LastPrompt!.Last(m => m.Role == ChatRole.Tool).Content;
        Assert.Contains("not aware of any device called lounge lamp", unknown, StringComparison.Ordinal);
        Assert.Contains("Kitchen: Kitchen light", unknown, StringComparison.Ordinal);

        // What lets someone in asks first.
        var before = house.Commands.Count;
        await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("unlock the front door", "UTC"));
        var confirm = (await stream.ReadUntilAsync(EventTypes.ToolConfirm))[^1].Payload<ToolConfirmEvent>();
        Assert.Equal("Tell Home Assistant: “unlock the front door”", confirm.Description);
        Assert.Equal(before, house.Commands.Count);
        await client.PostAsJsonAsync("/api/conversation/confirm", new ConfirmRequest(confirm.CallId, true));
        await stream.ReadUntilStateAsync(FamiliarState.Idle);
        Assert.Equal("unlock the front door", house.Commands[^1].Text);

        // Disconnected, it isn't offered.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/home-assistant")).StatusCode);
        await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("anything", "UTC"));
        await stream.ReadUntilStateAsync(FamiliarState.Idle);
        Assert.DoesNotContain(app.Llm.LastTools!, t => t.Name == "home");
    }
}
