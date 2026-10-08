using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Familiar.Agent.Llm;
using Familiar.Server.Conversation;
using Familiar.Server.Devices;
using Familiar.Server.Tests.Infrastructure;

namespace Familiar.Server.Tests;

/// <summary>Texts, calls, directions and where the phone is: done by the phone (BACKLOG item 21).</summary>
[Collection(PostgresCollection.Name)]
public sealed class PhoneToolsTests(PostgresFixture postgres)
{
    private static async Task<(HttpClient Browser, HttpClient Phone)> SignedInAsync(FamiliarAppFactory app)
    {
        var browser = app.CreateClient();
        (await browser.RegisterAsync("pat")).EnsureSuccessStatusCode();
        var login = await app.CreateClient().DeviceLoginAsync("pat", device: "Pixel 10");
        return (browser, app.DeviceClient(login.Token));
    }

    private static IEnumerable<string> Offered(FamiliarAppFactory app) => app.Llm.LastTools!.Select(t => t.Name);

    [Fact]
    public async Task Phone_tools_are_offered_only_while_a_phone_that_can_is_connected()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Llm.Fragments = ["[neutral:0.3] Okay."];
        var (browser, phone) = await SignedInAsync(app);

        await using var stream = await SseClient.ConnectAsync(browser);
        await stream.ReadUntilAsync(EventTypes.Ready);
        await browser.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("hello", "UTC"));
        await stream.ReadUntilStateAsync(FamiliarState.Idle);
        Assert.DoesNotContain("send_text", Offered(app));

        await using var phoneStream = await SseClient.ConnectAsync(phone, capabilities: "phone.text,maps.navigate");
        await phoneStream.ReadUntilAsync(EventTypes.Ready);
        await browser.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("hello again", "UTC"));
        await stream.ReadUntilStateAsync(FamiliarState.Idle);

        Assert.Contains("send_text", Offered(app));
        Assert.Contains("navigate", Offered(app));
        Assert.DoesNotContain("call", Offered(app));
        Assert.DoesNotContain("where_am_i", Offered(app));
    }

    [Fact]
    public async Task A_text_is_written_on_the_phone_and_only_the_contacts_name_comes_back()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Llm.ThenCall("send_text", """{"to": "Sam", "message": "Running ten minutes late"}""").ThenSay("[joy:0.4] It's ready to send.");
        var (browser, phone) = await SignedInAsync(app);

        await using var phoneStream = await SseClient.ConnectAsync(phone, capabilities: "phone.text");
        await phoneStream.ReadUntilAsync(EventTypes.Ready);
        await browser.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("text Sam I'm running ten minutes late", "UTC"));

        var invoke = (await phoneStream.ReadUntilAsync(EventTypes.ToolInvoke))[^1].Payload<ToolInvokeEvent>();
        Assert.Equal("phone.text", invoke.Tool);
        Assert.Equal(("Sam", "Running ten minutes late"), (invoke.Arguments["to"]!.GetValue<string>(), invoke.Arguments["message"]!.GetValue<string>()));

        Assert.Equal(HttpStatusCode.NoContent, (await phone.PostAsJsonAsync("/api/conversation/tool-result",
            new ToolResultRequest(invoke.CallId, true, new JsonObject { ["to"] = "Sam Kim", ["opened"] = "Messages" }))).StatusCode);
        await phoneStream.ReadUntilStateAsync(FamiliarState.Idle);

        var told = app.Llm.LastPrompt!.Single(m => m.Role == ChatRole.Tool).Content;
        Assert.Contains("Sam Kim", told, StringComparison.Ordinal);
        Assert.Contains("Messages", told, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_the_phone_couldnt_do_is_passed_on()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Llm.ThenCall("call", """{"to": "Sam"}""").ThenSay("[thinking:0.4] Which Sam?");
        var (browser, phone) = await SignedInAsync(app);

        await using var phoneStream = await SseClient.ConnectAsync(phone, capabilities: "phone.call");
        await phoneStream.ReadUntilAsync(EventTypes.Ready);
        await browser.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("call Sam", "UTC"));

        var invoke = (await phoneStream.ReadUntilAsync(EventTypes.ToolInvoke))[^1].Payload<ToolInvokeEvent>();
        await phone.PostAsJsonAsync("/api/conversation/tool-result",
            new ToolResultRequest(invoke.CallId, false, Error: "More than one contact matches: Sam Kim, Sam Patel. Ask which."));
        await phoneStream.ReadUntilStateAsync(FamiliarState.Idle);

        Assert.Contains("Sam Kim, Sam Patel", app.Llm.LastPrompt!.Single(m => m.Role == ChatRole.Tool).Content, StringComparison.Ordinal);
    }

    private static async Task LookUpsAsync(HttpClient browser, bool on)
    {
        var profile = await (await browser.GetAsync("/api/familiar")).ReadAsync<Familiars.FamiliarView>();
        (await browser.PutAsJsonAsync("/api/familiar", profile with { LookUps = on, Proactive = false })).EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_browser_can_say_where_it_is_with_look_ups_on(bool lookUps)
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Llm.Fragments = ["[neutral:0.3] Okay."];
        var (browser, _) = await SignedInAsync(app);
        await LookUpsAsync(browser, lookUps);

        // A browser may offer where it is, and nothing a phone does.
        await using var stream = await SseClient.ConnectAsync(browser, capabilities: "location.read,phone.text");
        Assert.NotNull((await stream.ReadUntilAsync(EventTypes.Ready))[^1].Payload<ReadyEvent>().ConnectionId);
        await browser.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("hello", "UTC"));
        await stream.ReadUntilStateAsync(FamiliarState.Idle);

        Assert.Equal(lookUps, Offered(app).Contains("where_am_i"));
        Assert.DoesNotContain("send_text", Offered(app));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Where_am_i_asks_the_device_the_message_came_from(bool fromPhone)
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Llm.ThenCall("where_am_i", "{}").ThenSay("[neutral:0.4] Got it.");
        var (browser, phone) = await SignedInAsync(app);
        await LookUpsAsync(browser, true);

        await using var tab = await SseClient.ConnectAsync(browser, capabilities: "location.read");
        var tabConnection = (await tab.ReadUntilAsync(EventTypes.Ready))[^1].Payload<ReadyEvent>().ConnectionId;
        await using var phoneStream = await SseClient.ConnectAsync(phone, capabilities: "location.read");
        var phoneConnection = (await phoneStream.ReadUntilAsync(EventTypes.Ready))[^1].Payload<ReadyEvent>().ConnectionId;

        await (fromPhone ? phone : browser).PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("where am I?", "UTC"));

        // Every stream of the account hears it; it names the one that should answer.
        var invoke = (await tab.ReadUntilAsync(EventTypes.ToolInvoke))[^1].Payload<ToolInvokeEvent>();
        Assert.Equal(fromPhone ? phoneConnection : tabConnection, invoke.ConnectionId);
        Assert.Equal(fromPhone, invoke.DeviceId is not null);
        await (fromPhone ? phone : browser).PostAsJsonAsync("/api/conversation/tool-result",
            new ToolResultRequest(invoke.CallId, true, fromPhone ? new JsonObject { ["place"] = "Headingley" } : new JsonObject { ["coordinates"] = "53.82,-1.58" }));
        await tab.ReadUntilStateAsync(FamiliarState.Idle);

        Assert.Contains(fromPhone ? "Headingley" : "53.82,-1.58", app.Llm.LastPrompt!.Single(m => m.Role == ChatRole.Tool).Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Contact_details_are_offered_only_by_a_phone_that_can()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Llm.Fragments = ["[neutral:0.3] Okay."];
        var (browser, phone) = await SignedInAsync(app);
        await LookUpsAsync(browser, true);

        await using var tab = await SseClient.ConnectAsync(browser, capabilities: "location.read,contacts.read");
        await tab.ReadUntilAsync(EventTypes.Ready);
        await browser.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("hello", "UTC"));
        await tab.ReadUntilStateAsync(FamiliarState.Idle);
        Assert.DoesNotContain("contact_details", Offered(app));

        await using var phoneStream = await SseClient.ConnectAsync(phone, capabilities: "contacts.read");
        await phoneStream.ReadUntilAsync(EventTypes.Ready);
        await phone.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("hello again", "UTC"));
        await phoneStream.ReadUntilStateAsync(FamiliarState.Idle);
        Assert.Contains("contact_details", Offered(app));
    }

    [Fact]
    public async Task Directions_to_a_contact_send_the_name_not_an_address()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Llm.ThenCall("navigate", """{"to_contact": "Mum", "mode": "walking"}""").ThenSay("[joy:0.4] Off you go.");
        var (browser, phone) = await SignedInAsync(app);

        await using var phoneStream = await SseClient.ConnectAsync(phone, capabilities: "maps.navigate");
        await phoneStream.ReadUntilAsync(EventTypes.Ready);
        await phone.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("take me to Mum's", "UTC"));

        var invoke = (await phoneStream.ReadUntilAsync(EventTypes.ToolInvoke))[^1].Payload<ToolInvokeEvent>();
        Assert.Equal(("maps.navigate", "Mum", null), (invoke.Tool, invoke.Arguments["to_contact"]?.GetValue<string>(), invoke.Arguments["destination"]));
        await phone.PostAsJsonAsync("/api/conversation/tool-result",
            new ToolResultRequest(invoke.CallId, true, new JsonObject { ["to"] = "Mum", ["how"] = "Maps opened with directions to Mum's." }));
        await phoneStream.ReadUntilStateAsync(FamiliarState.Idle);

        Assert.Contains("Mum's", app.Llm.LastPrompt!.Single(m => m.Role == ChatRole.Tool).Content, StringComparison.Ordinal);
    }
}
