using System.Net;
using System.Net.Http.Json;
using Familiar.Domain.Conversations;
using Familiar.Domain.Expression;
using Familiar.Server.Conversation;
using Familiar.Server.Familiars;
using Familiar.Server.Tests.Infrastructure;

namespace Familiar.Server.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ConversationTests(PostgresFixture postgres)
{
    private static async Task<HttpClient> SignedInAsync(FamiliarAppFactory app, string username = "talker")
    {
        var client = app.CreateClient();
        (await client.RegisterAsync(username)).EnsureSuccessStatusCode();
        return client;
    }

    private static Task<HttpResponseMessage> SayAsync(HttpClient client, string text) =>
        client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest(text, "America/Chicago"));

    [Fact]
    public async Task A_typed_message_streams_a_reply_with_its_expression()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var client = await SignedInAsync(app);
        await using var stream = await SseClient.ConnectAsync(client);

        var ready = (await stream.ReadUntilAsync(EventTypes.Ready))[^1].Payload<ReadyEvent>();
        Assert.Equal("Pip", ready.Familiar.Name);
        Assert.True(ready.ModelAvailable);

        Assert.Equal(HttpStatusCode.Accepted, (await SayAsync(client, "I got the job!")).StatusCode);
        var events = await stream.ReadUntilStateAsync(FamiliarState.Idle);
        var types = events.Select(e => e.Type).ToList();

        Assert.Equal(EventTypes.Message, types[0]);
        Assert.True(types.IndexOf(EventTypes.Expression) < types.IndexOf(EventTypes.ReplyDelta), "expression before speech");
        Assert.Equal("Hello there!", string.Concat(events.Where(e => e.Type == EventTypes.ReplyDelta).Select(e => e.Payload<ReplyDeltaEvent>().Text)));

        // Scaled by the default expressiveness (0.8).
        Assert.Equal(new ExpressionEvent(Emotion.Joy, 0.64), events.Single(e => e.Type == EventTypes.Expression).Payload<ExpressionEvent>());

        var end = events.Single(e => e.Type == EventTypes.ReplyEnd).Payload<ReplyEndEvent>();
        Assert.Equal((Emotion.Joy, 0.8, false), (end.Emotion, end.Intensity, end.Interrupted));

        var history = await (await client.GetAsync("/api/conversation/messages")).ReadAsync<List<MessageView>>();
        Assert.Equal([MessageRole.User, MessageRole.Assistant], history.Select(m => m.Role));
        Assert.Equal("Hello there!", history[1].Text);
        Assert.Equal(Emotion.Joy, history[1].Emotion);
    }

    [Fact]
    public async Task The_prompt_carries_the_persona_and_the_users_local_time()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var client = await SignedInAsync(app, "alice");
        (await client.PutAsJsonAsync("/api/familiar", new FamiliarView("Bramble", "Al", 0.9, 0.9, 0.2, 0.1, 1.0))).EnsureSuccessStatusCode();
        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);

        await SayAsync(client, "Hello");
        await stream.ReadUntilStateAsync(FamiliarState.Idle);

        var prompt = app.Llm.LastPrompt!;
        Assert.Contains("You are Bramble, Al's familiar", prompt[0].Content, StringComparison.Ordinal);
        Assert.Matches(@"^\(\w+ \w+, \d+ \w+ \d{4}, \d\d:\d\d\) Hello$", prompt[^1].Content);
    }

    [Fact]
    public async Task Interrupting_stops_the_reply_and_keeps_what_was_said()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Llm.Fragments = ["[thinking:0.5] Let me", " think", " about", " that", " for", " a", " while."];
        app.Llm.FragmentDelay = TimeSpan.FromMilliseconds(150);
        var client = await SignedInAsync(app);
        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);

        await SayAsync(client, "Tell me a long story");
        await stream.ReadUntilAsync(EventTypes.ReplyDelta);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/conversation/interrupt", null)).StatusCode);

        var end = (await stream.ReadUntilAsync(EventTypes.ReplyEnd))[^1].Payload<ReplyEndEvent>();
        Assert.True(end.Interrupted);
        Assert.DoesNotContain("while.", end.Text, StringComparison.Ordinal);

        var history = await (await client.GetAsync("/api/conversation/messages")).ReadAsync<List<MessageView>>();
        Assert.True(history[^1].Interrupted);
    }

    [Fact]
    public async Task A_new_message_interrupts_the_reply_in_progress()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Llm.Fragments = ["[neutral:0.5] One", " two", " three", " four", " five."];
        app.Llm.FragmentDelay = TimeSpan.FromMilliseconds(150);
        var client = await SignedInAsync(app);
        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);

        await SayAsync(client, "Count to five");
        await stream.ReadUntilAsync(EventTypes.ReplyDelta);
        await SayAsync(client, "Actually, stop");

        var events = await stream.ReadUntilAsync(EventTypes.Message);
        Assert.True(events.Single(e => e.Type == EventTypes.ReplyEnd).Payload<ReplyEndEvent>().Interrupted);
        Assert.Equal("Actually, stop", events[^1].Payload<MessageView>().Text);
    }

    [Fact]
    public async Task When_the_model_is_down_the_familiar_rests_and_the_message_is_kept()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Llm.Down = true;
        var client = await SignedInAsync(app);
        await using var stream = await SseClient.ConnectAsync(client);
        Assert.False((await stream.ReadUntilAsync(EventTypes.Ready))[^1].Payload<ReadyEvent>().ModelAvailable);

        await SayAsync(client, "Are you there?");
        var events = await stream.ReadUntilStateAsync(FamiliarState.Resting);

        Assert.Equal("resting", events.Single(e => e.Type == EventTypes.Error).Payload<ErrorEvent>().Code);
        var history = await (await client.GetAsync("/api/conversation/messages")).ReadAsync<List<MessageView>>();
        Assert.Equal(MessageRole.User, Assert.Single(history).Role);
    }

    [Fact]
    public async Task Every_device_on_the_account_sees_the_conversation()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var browser = await SignedInAsync(app, "multi");
        var phone = app.DeviceClient((await app.CreateClient().DeviceLoginAsync("multi")).Token);

        await using var browserStream = await SseClient.ConnectAsync(browser);
        await using var phoneStream = await SseClient.ConnectAsync(phone);
        await browserStream.ReadUntilAsync(EventTypes.Ready);
        await phoneStream.ReadUntilAsync(EventTypes.Ready);

        await SayAsync(phone, "Typed on my phone");

        var seenByBrowser = await browserStream.ReadUntilAsync(EventTypes.ReplyEnd);
        Assert.Equal("Typed on my phone", seenByBrowser.First(e => e.Type == EventTypes.Message).Payload<MessageView>().Text);
    }

    [Fact]
    public async Task A_reconnect_replays_what_was_missed()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var client = await SignedInAsync(app);

        string lastSeen;
        await using (var first = await SseClient.ConnectAsync(client))
        {
            await first.ReadUntilAsync(EventTypes.Ready);
            await SayAsync(client, "First");
            lastSeen = (await first.ReadUntilStateAsync(FamiliarState.Idle))[^1].Id!;
        }

        // Disconnected while the second exchange happens.
        await using (var observer = await SseClient.ConnectAsync(client))
        {
            await observer.ReadUntilAsync(EventTypes.Ready);
            await SayAsync(client, "Second");
            await observer.ReadUntilStateAsync(FamiliarState.Idle);
        }

        await using var resumed = await SseClient.ConnectAsync(client, lastSeen);
        var replayed = await resumed.ReadUntilAsync(EventTypes.Ready);

        Assert.Equal("Second", replayed.First(e => e.Type == EventTypes.Message).Payload<MessageView>().Text);
        Assert.Contains(replayed, e => e.Type == EventTypes.ReplyEnd);
    }

    [Fact]
    public async Task An_unknown_last_event_id_asks_the_client_to_resync()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var client = await SignedInAsync(app);

        await using var stream = await SseClient.ConnectAsync(client, "deadbeef-42");

        Assert.Equal(EventTypes.Resync, (await stream.NextAsync()).Type);
    }

    [Fact]
    public async Task Starting_over_clears_the_history()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var client = await SignedInAsync(app);
        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);
        await SayAsync(client, "Remember this");
        await stream.ReadUntilStateAsync(FamiliarState.Idle);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/conversation/new", null)).StatusCode);

        await stream.ReadUntilAsync(EventTypes.Cleared);
        Assert.Empty(await (await client.GetAsync("/api/conversation/messages")).ReadAsync<List<MessageView>>());
    }

    [Fact]
    public async Task The_stream_and_messages_require_sign_in()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var anonymous = app.CreateClient();

        await using var stream = await SseClient.ConnectAsync(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, stream.Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SayAsync(anonymous, "hi")).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_messages_are_refused(string text)
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var client = await SignedInAsync(app);

        Assert.Equal(HttpStatusCode.BadRequest, (await SayAsync(client, text)).StatusCode);
    }

    [Fact]
    public async Task Familiar_settings_are_validated_and_clamped()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var client = await SignedInAsync(app);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync("/api/familiar", new FamiliarView("", null, 0.5, 0.5, 0.5, 0.5, 0.5))).StatusCode);

        var saved = await (await client.PutAsJsonAsync("/api/familiar", new FamiliarView("  Moss  ", " ", 7, -1, 0.5, 0.5, 0.5)))
            .ReadAsync<FamiliarView>();

        Assert.Equal(("Moss", (string?)null, 1.0, 0.0), (saved.Name, saved.UserNickname, saved.Warmth, saved.Playfulness));
        Assert.Equal("Moss", (await (await client.GetAsync("/api/familiar")).ReadAsync<FamiliarView>()).Name);
    }
}

[Collection(PostgresCollection.Name)]
public sealed class WireFormatTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Enums_are_camelCase_over_http_exactly_as_on_the_stream()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var client = app.CreateClient();
        var registered = await client.RegisterAsync("wire");
        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);
        await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("Hi", null));
        await stream.ReadUntilStateAsync(FamiliarState.Idle);

        Assert.Contains("\"role\":\"admin\"", await registered.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var history = await (await client.GetAsync("/api/conversation/messages")).Content.ReadAsStringAsync();
        Assert.Contains("\"role\":\"user\"", history, StringComparison.Ordinal);
        Assert.Contains("\"role\":\"assistant\"", history, StringComparison.Ordinal);
        Assert.Contains("\"emotion\":\"joy\"", history, StringComparison.Ordinal);
    }
}
