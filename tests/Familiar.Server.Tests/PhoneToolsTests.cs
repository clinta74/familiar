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
}
