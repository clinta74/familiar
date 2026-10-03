using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Familiar.Agent.Llm;
using Familiar.Agent.Tools;
using Familiar.Agent.Turns;
using Familiar.Domain.Scheduling;
using Familiar.Domain.Tools;
using Familiar.Persistence;
using Familiar.Server.Conversation;
using Familiar.Server.Notifications;
using Familiar.Server.Scheduling;
using Familiar.Server.Tests.Infrastructure;
using Familiar.Server.Tools;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Familiar.Server.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ToolTests(PostgresFixture postgres)
{
    private static async Task<HttpClient> SignedInAsync(FamiliarAppFactory app, string username = "timekeeper")
    {
        var client = app.CreateClient();
        (await client.RegisterAsync(username)).EnsureSuccessStatusCode();
        return client;
    }

    private static Task<HttpResponseMessage> SayAsync(HttpClient client, string text) =>
        client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest(text, "America/Chicago"));

    [Fact]
    public async Task Asking_for_a_timer_sets_one_and_the_familiar_says_so()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Llm.ThenCall("set_timer", """{"minutes": 10, "label": "pasta"}""").ThenSay("[joy:0.8] Pasta timer's on!");
        var client = await SignedInAsync(app);
        await using var stream = await SseClient.ConnectAsync(client);
        Assert.Empty((await stream.ReadUntilAsync(EventTypes.Jobs))[^1].Payload<JobsEvent>().Jobs);

        await SayAsync(client, "Set a 10 minute timer for the pasta");
        var events = await stream.ReadUntilStateAsync(FamiliarState.Idle);

        var finished = events.Single(e => e.Type == EventTypes.ToolFinished).Payload<ToolFinishedEvent>();
        Assert.Equal(("set_timer", true, "Timer: pasta, 10 minutes"), (finished.Tool, finished.Ok, finished.Summary));
        Assert.True(events.FindIndex(e => e.Type == EventTypes.ToolStarted) < events.FindIndex(e => e.Type == EventTypes.ToolFinished));

        var job = Assert.Single(events.Single(e => e.Type == EventTypes.Jobs).Payload<JobsEvent>().Jobs);
        Assert.Equal((JobKind.Timer, "pasta", 600), (job.Kind, job.Label, job.DurationSeconds));
        Assert.InRange(job.DueAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(9.9), TimeSpan.FromMinutes(10));

        Assert.Equal("Pasta timer's on!", events.Single(e => e.Type == EventTypes.ReplyEnd).Payload<ReplyEndEvent>().Text);

        // The model was offered the tools, and saw the result before it replied.
        Assert.Contains(app.Llm.LastTools!, t => t.Name == "set_timer");
        var toolMessage = app.Llm.LastPrompt!.Single(m => m.Role == ChatRole.Tool);
        Assert.Equal("set_timer", toolMessage.ToolName);
        Assert.Contains("\"ok\":true", toolMessage.Content, StringComparison.Ordinal);

        var listed = await (await client.GetAsync("/api/jobs")).ReadAsync<List<JobView>>();
        Assert.Equal(job.Id, Assert.Single(listed).Id);

        await using var db = await Db(app);
        var audit = await db.ToolInvocations.SingleAsync();
        Assert.Equal(("set_timer", ToolRisk.WriteOwn, ToolOutcome.Succeeded), (audit.Tool, audit.Risk, audit.Outcome));
        Assert.Equal(finished.ReplyId, audit.ReplyId);
    }

    [Fact]
    public async Task A_timer_rings_by_saying_so_and_is_there_for_the_service_worker()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Llm.ThenCall("set_timer", """{"seconds": 1}""").ThenSay("[joy:0.5] One second, starting now.");
        var client = await SignedInAsync(app);
        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);

        await SayAsync(client, "Give me a one second timer");
        await stream.ReadUntilStateAsync(FamiliarState.Idle);

        var events = await stream.ReadUntilAsync(EventTypes.Notify, TimeSpan.FromSeconds(10));
        var notify = events[^1].Payload<NotifyEvent>();
        Assert.Equal("Your 1 second timer is done!", notify.Text);

        var rest = await stream.ReadUntilStateAsync(FamiliarState.Idle);
        Assert.Equal(notify.Text, rest.Single(e => e.Type == EventTypes.ReplyEnd).Payload<ReplyEndEvent>().Text);
        Assert.Contains(rest, e => e.Type == EventTypes.Gesture);

        // Stored like any other reply, and spoken.
        var history = await (await client.GetAsync("/api/conversation/messages")).ReadAsync<List<MessageView>>();
        Assert.Equal(notify.Text, history[^1].Text);
        Assert.Contains(app.Tts.Requests, r => r.Text.Contains("timer is done", StringComparison.Ordinal));

        var recent = await (await client.GetAsync("/api/notifications/recent")).ReadAsync<List<NotificationView>>();
        Assert.Equal(notify.Id, Assert.Single(recent).Id);
        Assert.Empty(await (await client.GetAsync("/api/jobs")).ReadAsync<List<JobView>>());
    }

    [Fact]
    public async Task A_job_that_came_due_while_the_server_was_down_fires_late_and_says_so()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var client = await SignedInAsync(app, "sleeper");

        // As a restart finds it: pending, and due twenty minutes ago.
        await using (var db = await Db(app))
        {
            db.ScheduledJobs.Add(new ScheduledJob
            {
                AccountId = (await db.Accounts.SingleAsync()).Id,
                Kind = JobKind.Reminder,
                Label = "water the plants",
                DueAt = DateTimeOffset.UtcNow.AddMinutes(-20),
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            });
            await db.SaveChangesAsync();
        }

        app.Services.GetRequiredService<SchedulerSignal>().Poke();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        List<MessageView> history;
        do
        {
            await Task.Delay(100);
            history = await (await client.GetAsync("/api/conversation/messages")).ReadAsync<List<MessageView>>();
        }
        while (history.Count == 0 && DateTime.UtcNow < deadline);

        var said = Assert.Single(history).Text;
        Assert.StartsWith("Sorry, I was away when this was due 20 minutes", said, StringComparison.Ordinal);
        Assert.EndsWith("water the plants.", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_from_the_list_stops_the_timer_everywhere()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Llm.ThenCall("set_timer", """{"hours": "1"}""").ThenSay("[joy:0.5] Done.");
        var client = await SignedInAsync(app);
        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Jobs);
        await SayAsync(client, "One hour timer");
        var job = Assert.Single((await stream.ReadUntilAsync(EventTypes.Jobs))[^1].Payload<JobsEvent>().Jobs);
        Assert.Equal(3600, job.DurationSeconds);
        await stream.ReadUntilStateAsync(FamiliarState.Idle);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/jobs/{job.Id}")).StatusCode);
        Assert.Empty((await stream.ReadUntilAsync(EventTypes.Jobs))[^1].Payload<JobsEvent>().Jobs);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/jobs/{job.Id}")).StatusCode);

        // Nobody else's to cancel.
        var other = await SignedInAsync(app, "someoneelse");
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/jobs/{job.Id}")).StatusCode);
    }

    [Fact]
    public async Task The_model_asks_which_one_when_a_cancel_is_ambiguous()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Llm
            .ThenCall("set_timer", """{"minutes": 5, "label": "eggs"}""").ThenSay("[joy:0.5] Eggs.")
            .ThenCall("set_timer", """{"minutes": 9, "label": "toast"}""").ThenSay("[joy:0.5] Toast.")
            .ThenCall("cancel_scheduled", """{"kind": "timer"}""").ThenSay("[thinking:0.5] Which one?")
            .ThenCall("cancel_scheduled", """{"label": "TOAST"}""").ThenSay("[joy:0.5] Toast's off.");
        var client = await SignedInAsync(app);
        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);

        foreach (var said in new[] { "eggs", "toast", "cancel the timer" })
        {
            await SayAsync(client, said);
            await stream.ReadUntilStateAsync(FamiliarState.Idle);
        }

        var asked = JsonNode.Parse(app.Llm.LastPrompt!.Single(m => m.Role == ChatRole.Tool).Content)!;
        Assert.False(asked["ok"]!.GetValue<bool>());
        Assert.Equal(2, asked["matches"]!.AsArray().Count);

        await SayAsync(client, "the toast one");
        await stream.ReadUntilStateAsync(FamiliarState.Idle);
        Assert.Equal("eggs", Assert.Single(await (await client.GetAsync("/api/jobs")).ReadAsync<List<JobView>>()).Label);
    }

    [Theory]
    [InlineData(true, "sent")]
    [InlineData(false, "didn't allow it")]
    public async Task Tools_that_reach_outside_familiar_ask_first(bool approve, string expected)
    {
        var outside = new OutsideTool();
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync(),
            services: s => s.AddSingleton<ITool>(outside));
        app.Llm.ThenCall("send_postcard", """{"to": "Gran"}""").ThenSay("[joy:0.5] OK.");
        var client = await SignedInAsync(app);
        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);

        await SayAsync(client, "Send Gran a postcard");
        var confirm = (await stream.ReadUntilAsync(EventTypes.ToolConfirm))[^1].Payload<ToolConfirmEvent>();
        Assert.Equal("Send a postcard to Gran", confirm.Description);
        Assert.Equal(0, outside.Runs);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/api/conversation/confirm", new ConfirmRequest(confirm.CallId, approve))).StatusCode);
        var events = await stream.ReadUntilStateAsync(FamiliarState.Idle);

        Assert.Equal(approve, events.Single(e => e.Type == EventTypes.ToolConfirmed).Payload<ToolConfirmedEvent>().Approved);
        Assert.Equal(approve ? 1 : 0, outside.Runs);
        Assert.Contains(expected, app.Llm.LastPrompt!.Single(m => m.Role == ChatRole.Tool).Content, StringComparison.OrdinalIgnoreCase);

        // Answered already.
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/conversation/confirm", new ConfirmRequest(confirm.CallId, approve))).StatusCode);

        await using var db = await Db(app);
        var audit = await db.ToolInvocations.SingleAsync();
        Assert.Equal((approve, approve ? ToolOutcome.Succeeded : ToolOutcome.Declined), (audit.Approved!.Value, audit.Outcome));
    }

    [Fact]
    public async Task Push_subscriptions_only_accept_real_push_services()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var client = await SignedInAsync(app);

        var key = await (await client.GetAsync("/api/push/key")).ReadAsync<PushKeyResponse>();
        var point = WebEncoders.Base64UrlDecode(key.PublicKey);
        Assert.Equal((65, (byte)4), (point.Length, point[0]));

        // The same key after a restart: it's in the database, or every subscription would break.
        await using (var db = await Db(app))
        {
            Assert.Single(await db.ServerSecrets.ToListAsync());
        }

        foreach (var bad in new[] { "http://fcm.googleapis.com/x", "https://127.0.0.1/x", "https://localhost/x", "https://printer.local/x", "https://fcm.googleapis.com:8443/x" })
        {
            var response = await client.PostAsJsonAsync("/api/push/subscriptions", new SubscribeRequest(bad, null));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var endpoint = "https://fcm.googleapis.com/fcm/send/abc123";
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/api/push/subscriptions", new SubscribeRequest(endpoint, new SubscriptionKeys("p", "a")))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/api/push/subscriptions", new SubscribeRequest(endpoint, new SubscriptionKeys("p2", "a2")))).StatusCode);

        await using (var db = await Db(app))
        {
            Assert.Equal("p2", (await db.PushSubscriptions.SingleAsync()).P256dh);
        }

        using var remove = new HttpRequestMessage(HttpMethod.Delete, "/api/push/subscriptions") { Content = JsonContent.Create(new UnsubscribeRequest(endpoint)) };
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(remove)).StatusCode);
        await using (var db = await Db(app))
        {
            Assert.Empty(await db.PushSubscriptions.ToListAsync());
        }
    }

    [Fact]
    public async Task A_phone_pushes_through_the_households_ntfy_and_its_endpoint_goes_with_the_device()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        (await app.CreateClient().RegisterAsync("pocket")).EnsureSuccessStatusCode();
        var login = await app.CreateClient().DeviceLoginAsync("pocket");
        var phone = app.DeviceClient(login.Token);

        // ntfy is on the local network: accepted because it's the one configured, on its own origin only.
        foreach (var bad in new[] { "http://192.168.1.20:2587/up1", "https://192.168.1.20:2586/up1", "http://192.168.1.20:2586/", "http://192.168.1.21:2586/up1", "https://printer.local/up1" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await phone.PostAsJsonAsync("/api/push/subscriptions", new SubscribeRequest(bad, null))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NoContent,
            (await phone.PostAsJsonAsync("/api/push/subscriptions", new SubscribeRequest("http://192.168.1.20:2586/upAAA?up=1", null))).StatusCode);
        // A new endpoint from the same phone replaces its old one.
        Assert.Equal(HttpStatusCode.NoContent,
            (await phone.PostAsJsonAsync("/api/push/subscriptions", new SubscribeRequest("http://192.168.1.20:2586/upBBB?up=1", null))).StatusCode);

        await using (var db = await Db(app))
        {
            var subscription = await db.PushSubscriptions.SingleAsync();
            Assert.Equal(("http://192.168.1.20:2586/upBBB?up=1", login.Device.Id), (subscription.Endpoint, subscription.DeviceId));
        }

        // Signing the phone out takes its push endpoint with it.
        Assert.Equal(HttpStatusCode.NoContent, (await phone.DeleteAsync($"/api/auth/devices/{login.Device.Id}")).StatusCode);
        await using (var db = await Db(app))
        {
            Assert.Empty(await db.PushSubscriptions.ToListAsync());
        }
    }

    [Fact]
    public async Task A_reminder_to_wake_up_rings_and_timers_always_do()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Llm
            .ThenCall("create_reminder", """{"text": "wake up", "in_minutes": 600, "alarm": true}""").ThenSay("[joy:0.5] I'll wake you.")
            .ThenCall("create_reminder", """{"text": "call Mum", "in_minutes": 60}""").ThenSay("[joy:0.5] Will do.")
            .ThenCall("set_timer", """{"minutes": 5}""").ThenSay("[joy:0.5] Five minutes.");
        var client = await SignedInAsync(app);
        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Jobs);

        foreach (var ask in new[] { "Wake me at 7", "Remind me to call Mum in an hour", "5 minute timer" })
        {
            await SayAsync(client, ask);
            await stream.ReadUntilStateAsync(FamiliarState.Idle);
        }

        var jobs = await (await client.GetAsync("/api/jobs")).ReadAsync<List<JobView>>();
        Assert.Equal(
            [("wake up", true), ("call Mum", false), ("", true)],
            jobs.OrderByDescending(j => j.DueAt).Select(j => (j.Label, j.Ring)).ToList());
    }

    [Fact]
    public async Task The_vapid_token_verifies_against_the_published_key()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var keys = app.Services.GetRequiredService<VapidKeys>();

        var header = await keys.AuthorizationAsync(new Uri("https://fcm.googleapis.com/fcm/send/abc"), "mailto:test@example.com", default);
        var match = System.Text.RegularExpressions.Regex.Match(header, "^vapid t=([^.]+)\\.([^.]+)\\.([^,]+), k=(.+)$");
        Assert.True(match.Success, header);

        var claims = JsonNode.Parse(WebEncoders.Base64UrlDecode(match.Groups[2].Value))!;
        Assert.Equal("https://fcm.googleapis.com", claims["aud"]!.GetValue<string>());
        Assert.Equal("mailto:test@example.com", claims["sub"]!.GetValue<string>());

        var point = WebEncoders.Base64UrlDecode(match.Groups[4].Value);
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33], Y = point[33..] },
        });
        Assert.True(verifier.VerifyData(
            Encoding.ASCII.GetBytes($"{match.Groups[1].Value}.{match.Groups[2].Value}"),
            WebEncoders.Base64UrlDecode(match.Groups[3].Value),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    private static async Task<FamiliarDbContext> Db(FamiliarAppFactory app) =>
        await app.Services.GetRequiredService<IDbContextFactory<FamiliarDbContext>>().CreateDbContextAsync();

    private sealed class OutsideTool : ITool
    {
        public int Runs { get; private set; }

        public ToolDefinition Definition { get; } = new("send_postcard", "Send a postcard.",
            ToolSchema.Object(("to", ToolSchema.String("Who to"))), ToolRisk.WriteExternal);

        public string Describe(JsonObject arguments) => $"Send a postcard to {ToolArgs.String(arguments, "to")}";

        public Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
        {
            Runs++;
            return Task.FromResult(ToolResult.Success(new JsonObject { ["ok"] = true, ["status"] = "sent" }, "Postcard sent"));
        }
    }
}

public sealed class SchedulingUnitTests
{
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    // Thursday 1 October 2026, 17:30 in Chicago.
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 22, 30, 0, TimeSpan.Zero);

    private static ToolContext Context => new(Guid.NewGuid(), Guid.NewGuid(), Chicago, Now);

    [Theory]
    [InlineData(1, "1 second")]
    [InlineData(45, "45 seconds")]
    [InlineData(90, "1 minute 30 seconds")]
    [InlineData(600, "10 minutes")]
    [InlineData(5400, "1 hour 30 minutes")]
    [InlineData(90061, "1 day 1 hour 1 minute")]
    public void Durations_are_said_the_way_people_say_them(int seconds, string expected) =>
        Assert.Equal(expected, Spoken.Duration(seconds));

    [Theory]
    [InlineData("18:00", "2026-10-01T23:00:00Z")]
    [InlineData("9:00", "2026-10-02T14:00:00Z")]
    [InlineData("2026-10-03T08:15", "2026-10-03T13:15:00Z")]
    [InlineData("2026-11-01 09:00", "2026-11-01T15:00:00Z")]
    [InlineData("Tuesday 15:00", "2026-10-06T20:00:00Z")]
    [InlineData("tomorrow 9:00", "2026-10-02T14:00:00Z")]
    [InlineData("thursday 18:00", "2026-10-01T23:00:00Z")]
    [InlineData("Thursday 09:00", "2026-10-08T14:00:00Z")]
    [InlineData("next Wednesday 12:30", "2026-10-07T17:30:00Z")]
    [InlineData("next Thursday 18:00", "2026-10-08T23:00:00Z")]
    public void Reminder_times_are_the_users_local_time(string text, string expectedUtc) =>
        Assert.Equal(DateTimeOffset.Parse(expectedUtc, CultureInfo.InvariantCulture), CreateReminderTool.ParseLocal(text, Context));

    [Fact]
    public void Unreadable_times_are_refused() => Assert.Null(CreateReminderTool.ParseLocal("half past nine", Context));

    [Theory]
    [InlineData(0, "at 5:30 PM")]
    [InlineData(16, "tomorrow at 9:30 AM")]
    [InlineData(64, "on Sunday at 9:30 AM")]
    [InlineData(24 * 9, "on Saturday 10 October at 5:30 PM")]
    public void Times_are_spoken_relative_to_today(int hoursAhead, string expected) =>
        Assert.Equal(expected, Spoken.When(Now.AddHours(hoursAhead), Chicago, Now));

    [Fact]
    public void Announcements_name_the_timer()
    {
        var job = new ScheduledJob { Kind = JobKind.Timer, Label = "", DurationSeconds = 600, DueAt = Now };
        Assert.Equal("Your 10 minute timer is done!", Spoken.Announcement(job, Now));

        job.Label = "pasta";
        Assert.Equal("Your pasta timer is done!", Spoken.Announcement(job, Now.AddSeconds(2)));
        Assert.Equal("Your pasta timer went off while I was away, about 1 hour ago.", Spoken.Announcement(job, Now.AddHours(1)));
    }

    [Fact]
    public void Arguments_are_read_leniently()
    {
        var args = (JsonObject)JsonNode.Parse("""{"n": "10", "b": "true", "s": 5, "blank": "  ", "label": "  call\n  Mum  "}""")!;

        Assert.Equal(10, ToolArgs.Number(args, "n"));
        Assert.True(ToolArgs.Bool(args, "b"));
        Assert.Equal("5", ToolArgs.String(args, "s"));
        Assert.Null(ToolArgs.String(args, "blank"));
        Assert.Null(ToolArgs.Number(args, "missing"));
        Assert.Equal("call Mum", ToolArgs.Label(args, "label"));
    }
}

public sealed class TurnRunnerToolTests
{
    [Fact]
    public async Task A_model_that_keeps_calling_tools_is_made_to_answer()
    {
        var llm = new FakeLlm { Fragments = ["[neutral:0.5] Fine."] };
        for (var i = 0; i < 10; i++)
        {
            llm.ThenCall("list_scheduled", "{}");
        }

        var executor = new CountingExecutor();
        var events = new List<TurnEvent>();
        var result = await new TurnRunner(llm).RunAsync([], e =>
        {
            events.Add(e);
            return ValueTask.CompletedTask;
        }, default, executor, new ToolContext(Guid.NewGuid(), Guid.NewGuid(), TimeZoneInfo.Utc, DateTimeOffset.UtcNow));

        Assert.Equal(TurnRunner.MaxToolRounds, executor.Calls);
        Assert.Null(llm.LastTools);
        Assert.Equal(TurnRunner.MaxToolRounds, events.OfType<ToolFinished>().Count());
        Assert.False(result.Interrupted);
    }

    [Fact]
    public async Task What_it_says_before_and_after_a_tool_is_one_reply()
    {
        var llm = new FakeLlm();
        llm.Rounds.Enqueue([new TextChunk("[joy:0.5] Sure."), new ToolCallsChunk([new ToolCall("c1", "list_scheduled", [])])]);
        llm.ThenSay("Nothing's running.");

        var result = await new TurnRunner(llm).RunAsync([], _ => ValueTask.CompletedTask, default,
            new CountingExecutor(), new ToolContext(Guid.NewGuid(), Guid.NewGuid(), TimeZoneInfo.Utc, DateTimeOffset.UtcNow));

        Assert.Equal("Sure. Nothing's running.", result.Text);
        var assistant = llm.LastPrompt!.Single(m => m.Role == ChatRole.Assistant);
        Assert.Equal("c1", Assert.Single(assistant.ToolCalls!).Id);
    }

    private sealed class CountingExecutor : IToolExecutor
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<ToolDefinition>> DefinitionsAsync(ToolContext context, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ToolDefinition>>([new("list_scheduled", "List.", ToolSchema.Object(), ToolRisk.Read)]);

        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(ToolResult.Success(new JsonObject { ["ok"] = true }, "Listed"));
        }
    }
}
