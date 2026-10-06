using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Familiar.Agent.Llm;
using Familiar.Agent.Prompting;
using Familiar.Domain.Calendars;
using Familiar.Domain.Familiars;
using Familiar.Server.Awareness;
using Familiar.Server.Calendars;
using Familiar.Server.Conversation;
using Familiar.Server.Familiars;
using Familiar.Server.Memories;
using Familiar.Server.Scheduling;
using Familiar.Server.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Familiar.Server.Tests;

[Collection(PostgresCollection.Name)]
public sealed class MemoryTests(PostgresFixture postgres)
{
    private static readonly Dictionary<string, string> NoBackgroundPass = new() { ["Memory:ExtractFromConversations"] = "false", ["Memory:QuietSeconds"] = "0" };

    private static async Task<HttpClient> SignedInAsync(FamiliarAppFactory app, string username = "rememberer")
    {
        var client = app.CreateClient();
        (await client.RegisterAsync(username)).EnsureSuccessStatusCode();
        return client;
    }

    private static Task<HttpResponseMessage> SayAsync(HttpClient client, string text) =>
        client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest(text, "America/Chicago"));

    private static MemoryExtractor Extractor(FamiliarAppFactory app) =>
        app.Services.GetServices<IHostedService>().OfType<MemoryExtractor>().Single();

    [Fact]
    public async Task A_quiet_conversation_is_read_for_things_worth_remembering_and_they_go_into_the_prompt()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync(), NoBackgroundPass);
        var client = await SignedInAsync(app);
        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);

        await SayAsync(client, "I'm Sam, and my daughter Maya is eight");
        await stream.ReadUntilStateAsync(FamiliarState.Idle);

        app.Llm.JsonAnswers.Enqueue("""{"facts": ["Their name is Sam.", "Their daughter Maya is eight", "Their name is Sam."]}""");
        Assert.Equal(2, await Extractor(app).PassAsync(default));

        // The model saw the conversation and what was already known.
        var asked = app.Llm.LastJsonPrompt![^1].Content;
        Assert.Contains("(nothing yet)", asked, StringComparison.Ordinal);
        Assert.Contains("User: I'm Sam, and my daughter Maya is eight", asked, StringComparison.Ordinal);
        Assert.Contains("Companion: Hello there!", asked, StringComparison.Ordinal);

        var memories = await (await client.GetAsync("/api/memories")).ReadAsync<List<MemoryView>>();
        Assert.Equal(["Their name is Sam.", "Their daughter Maya is eight."], memories.Select(m => m.Text));

        // Nothing new since: no second look.
        app.Llm.JsonAnswers.Enqueue("""{"facts": ["Something else."]}""");
        Assert.Equal(0, await Extractor(app).PassAsync(default));
        Assert.Single(app.Llm.JsonAnswers);

        await SayAsync(client, "What's my name?");
        await stream.ReadUntilStateAsync(FamiliarState.Idle);
        var system = app.Llm.LastPrompt![0].Content;
        Assert.EndsWith("- Their name is Sam.\n- Their daughter Maya is eight.", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Asking_to_remember_and_to_forget_works_by_meaning_not_exact_words()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync(), NoBackgroundPass);
        app.Llm
            .ThenCall("remember", """{"fact": "Their sister Jo lives in Leeds"}""").ThenSay("[joy:0.5] Got it.")
            .ThenCall("remember", """{"fact": "They take tea without sugar"}""").ThenSay("[joy:0.5] Noted.")
            .ThenCall("forget", """{"about": "sister in Leeds"}""").ThenSay("[neutral:0.5] Forgotten.");
        var client = await SignedInAsync(app);
        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);

        foreach (var said in new[] { "remember my sister Jo lives in Leeds", "I take tea without sugar", "forget about my sister" })
        {
            await SayAsync(client, said);
            await stream.ReadUntilStateAsync(FamiliarState.Idle);
        }

        var left = await (await client.GetAsync("/api/memories")).ReadAsync<List<MemoryView>>();
        Assert.Equal("They take tea without sugar.", Assert.Single(left).Text);
        Assert.Contains("Their sister Jo lives in Leeds", app.Llm.LastPrompt!.Single(m => m.Role == ChatRole.Tool).Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_memory_saved_in_settings_does_not_wait_for_its_embedding()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync(), NoBackgroundPass);
        var client = await SignedInAsync(app);
        var store = app.Services.GetRequiredService<MemoryStore>();
        await using var db = await app.Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Familiar.Persistence.FamiliarDbContext>>().CreateDbContextAsync();

        // The embedding model is away: the save still goes through, without one.
        app.Embedder.Down = true;
        var added = await (await client.PostAsJsonAsync("/api/memories", new MemoryRequest("They grow tomatoes"))).ReadAsync<MemoryView>();
        Assert.Null((await db.Memories.FindAsync(added.Id))!.Embedding);

        // Back: the next pass fills it in, and an edit gets a fresh one.
        // Whichever fills it in - this pass, or the save's own attempt starting late - it gets one.
        app.Embedder.Down = false;
        float[]? embedded = null;
        for (var i = 0; i < 50 && embedded is null; i++)
        {
            await store.EmbedMissingAsync(default);
            db.ChangeTracker.Clear();
            embedded = (await db.Memories.FindAsync(added.Id))!.Embedding;
            if (embedded is null)
            {
                await Task.Delay(50);
            }
        }

        Assert.NotNull(embedded);

        // The edit embeds itself just after saving, in the background.
        await client.PutAsJsonAsync($"/api/memories/{added.Id}", new MemoryRequest("They grow chillies"));
        float[]? edited = null;
        for (var i = 0; i < 50 && edited is null; i++)
        {
            await Task.Delay(100);
            db.ChangeTracker.Clear();
            edited = (await db.Memories.FindAsync(added.Id))!.Embedding;
        }

        Assert.NotNull(edited);
        Assert.NotEqual(embedded, edited);
    }

    [Fact]
    public async Task Memories_can_be_read_corrected_and_deleted_and_are_private()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync(), NoBackgroundPass);
        var client = await SignedInAsync(app);

        var added = await (await client.PostAsJsonAsync("/api/memories", new MemoryRequest("They love  hiking"))).ReadAsync<MemoryView>();
        Assert.Equal("They love hiking.", added.Text);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/memories", new MemoryRequest("they love hiking."))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/memories", new MemoryRequest(" "))).StatusCode);

        var edited = await (await client.PutAsJsonAsync($"/api/memories/{added.Id}", new MemoryRequest("They love hill walking"))).ReadAsync<MemoryView>();
        Assert.Equal("They love hill walking.", edited.Text);

        var other = await SignedInAsync(app, "nosy");
        Assert.Empty(await (await other.GetAsync("/api/memories")).ReadAsync<List<MemoryView>>());
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/memories/{added.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/memories/{added.Id}")).StatusCode);
        Assert.Empty(await (await client.GetAsync("/api/memories")).ReadAsync<List<MemoryView>>());
    }

    [Fact]
    public async Task A_memory_is_kept_even_when_it_cannot_be_embedded()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync(), NoBackgroundPass);
        app.Embedder.Down = true;
        var client = await SignedInAsync(app);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/memories", new MemoryRequest("They are left-handed"))).StatusCode);
        Assert.Single(await (await client.GetAsync("/api/memories")).ReadAsync<List<MemoryView>>());
    }
}

[Collection(PostgresCollection.Name)]
public sealed class AwarenessTests(PostgresFixture postgres)
{
    private static async Task<HttpClient> SignedInAsync(FamiliarAppFactory app, string username = "aware")
    {
        var client = app.CreateClient();
        (await client.RegisterAsync(username)).EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<Guid> AccountIdAsync(FamiliarAppFactory app, string username)
    {
        await using var db = await app.Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Persistence.FamiliarDbContext>>().CreateDbContextAsync();
        return db.Accounts.Single(a => a.Username == username).Id;
    }

    [Fact]
    public async Task The_prompt_knows_whats_coming_up_and_whats_running()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Llm.ThenCall("set_timer", """{"minutes": 10, "label": "pasta"}""").ThenSay("[joy:0.5] On it.");
        var client = await SignedInAsync(app);
        var accountId = await AccountIdAsync(app, "aware");
        var now = DateTimeOffset.UtcNow;
        await app.Services.GetRequiredService<CalendarStore>().AddLocalEventAsync(accountId, "Dentist", now.AddMinutes(30), now.AddMinutes(60), false, default);

        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);
        await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("pasta timer please", "America/Chicago"));
        await stream.ReadUntilStateAsync(FamiliarState.Idle);
        var first = app.Llm.LastPrompt!.Last(m => m.Role == ChatRole.User).Content;
        Assert.Contains("On their calendar: Dentist, in 30 minutes", first, StringComparison.Ordinal);

        await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("anything else today?", "America/Chicago"));
        await stream.ReadUntilStateAsync(FamiliarState.Idle);

        // Mid-conversation the calendar isn't news, so it isn't in every message; the timer is.
        var last = app.Llm.LastPrompt![^1].Content;
        Assert.DoesNotContain("Dentist", last, StringComparison.Ordinal);
        Assert.Contains("Running: pasta timer, 10 minutes left", last, StringComparison.Ordinal);
        Assert.DoesNotContain("first time", last, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(40, 45, false)] // Not yet within half an hour.
    [InlineData(25, 35, true)] // Came within it since they last spoke: mentioned once...
    [InlineData(20, 25, false)] // ...and not again.
    [InlineData(20, null, true)]
    public void Something_about_to_start_is_noted_once(int startsIn, int? startedInWhenLastSpoke, bool expected)
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var at = now.AddMinutes(startsIn);
        DateTimeOffset? lastSpoke = startedInWhenLastSpoke is { } m ? at.AddMinutes(-m) : null;

        Assert.Equal(expected, SituationProvider.JustImminent(at, now, lastSpoke));
    }

    [Fact]
    public async Task Coming_back_after_a_while_the_familiar_greets_first_once()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync(), new Dictionary<string, string> { ["Awareness:Greet"] = "true" });
        app.Llm.Fragments = ["[joy:0.7] Hi! Your dentist is in half an hour."];
        var client = await SignedInAsync(app, "greeted");
        var accountId = await AccountIdAsync(app, "greeted");
        var now = DateTimeOffset.UtcNow;
        await app.Services.GetRequiredService<CalendarStore>().AddLocalEventAsync(accountId, "Dentist", now.AddMinutes(30), now.AddMinutes(60), false, default);

        await using (var stream = await SseClient.ConnectAsync(client, timeZone: "America/Chicago"))
        {
            var end = (await stream.ReadUntilAsync(EventTypes.ReplyEnd))[^1].Payload<ReplyEndEvent>();
            Assert.Equal("Hi! Your dentist is in half an hour.", end.Text);
        }

        var prompt = app.Llm.LastPrompt!;
        Assert.EndsWith(PromptBuilder.GreetingCue, prompt[^1].Content, StringComparison.Ordinal);
        Assert.Contains("Dentist, in 30 minutes", prompt[^1].Content, StringComparison.Ordinal);
        Assert.Null(app.Llm.LastTools);

        // Stored as a reply with no message before it; and not repeated on the next visit.
        var history = await (await client.GetAsync("/api/conversation/messages")).ReadAsync<List<MessageView>>();
        Assert.Equal(Domain.Conversations.MessageRole.Assistant, Assert.Single(history).Role);

        app.Llm.Fragments = ["[joy:0.7] Hello again!"];
        await using (var again = await SseClient.ConnectAsync(client, timeZone: "America/Chicago"))
        {
            await again.ReadUntilAsync(EventTypes.Ready);
            await Assert.ThrowsAnyAsync<Exception>(() => again.ReadUntilAsync(EventTypes.ReplyStart, TimeSpan.FromSeconds(1)));
        }
    }

    [Fact]
    public async Task A_familiar_set_not_to_speak_first_doesnt()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync(), new Dictionary<string, string> { ["Awareness:Greet"] = "true" });
        var client = await SignedInAsync(app, "quiet");
        var familiar = await (await client.GetAsync("/api/familiar")).ReadAsync<FamiliarView>();
        (await client.PutAsJsonAsync("/api/familiar", familiar with { Proactive = false })).EnsureSuccessStatusCode();

        await using var stream = await SseClient.ConnectAsync(client, timeZone: "America/Chicago");
        await stream.ReadUntilAsync(EventTypes.Ready);
        await Assert.ThrowsAnyAsync<Exception>(() => stream.ReadUntilAsync(EventTypes.ReplyStart, TimeSpan.FromSeconds(1)));
        Assert.False((await (await client.GetAsync("/api/familiar")).ReadAsync<FamiliarView>()).Proactive);
    }

    [Fact]
    public async Task The_familiar_reads_and_writes_the_calendar()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var tomorrow = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        app.Llm
            .ThenCall("create_event", """{"title": "Lunch with Jo", "day": "tomorrow", "time": "12:30", "duration_minutes": 90}""").ThenSay("[joy:0.5] Added.")
            .ThenCall("list_events", $$"""{"date": "{{tomorrow}}"}""").ThenSay("[joy:0.5] Lunch with Jo.");
        var client = await SignedInAsync(app);
        await using var stream = await SseClient.ConnectAsync(client);
        await stream.ReadUntilAsync(EventTypes.Ready);

        await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("lunch with Jo tomorrow at half twelve", "UTC"));
        var finished = (await stream.ReadUntilAsync(EventTypes.ToolFinished))[^1].Payload<Tools.ToolFinishedEvent>();
        Assert.True(finished.Ok, finished.Summary);
        await stream.ReadUntilStateAsync(FamiliarState.Idle);

        await client.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("what's on tomorrow?", "UTC"));
        await stream.ReadUntilStateAsync(FamiliarState.Idle);
        var listed = app.Llm.LastPrompt!.Single(m => m.Role == ChatRole.Tool).Content;
        Assert.Contains("\"title\":\"Lunch with Jo\"", listed, StringComparison.Ordinal);
        Assert.Contains("tomorrow at 12:30 PM", listed, StringComparison.Ordinal);

        var events = await (await client.GetAsync("/api/calendars/events")).ReadAsync<List<EventView>>();
        var lunch = Assert.Single(events);
        Assert.Equal(TimeSpan.FromMinutes(90), lunch.EndsAt - lunch.StartsAt);
    }
}

[Collection(PostgresCollection.Name)]
public sealed class CalendarFeedTests(PostgresFixture postgres)
{
    private sealed class FeedServer : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, string Body)> Feeds { get; } = [];

        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            var (status, body) = Feeds.TryGetValue(request.RequestUri!.AbsoluteUri, out var feed) ? feed : (HttpStatusCode.NotFound, "");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    internal static string Feed(DateTime now) => $"""
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Test//EN
        BEGIN:VEVENT
        UID:standup@test
        DTSTART:{now.AddDays(-14):yyyyMMdd}T150000Z
        DTEND:{now.AddDays(-14):yyyyMMdd}T151500Z
        RRULE:FREQ=WEEKLY;COUNT=10
        SUMMARY:Standup
        END:VEVENT
        BEGIN:VEVENT
        UID:holiday@test
        DTSTART;VALUE=DATE:{now.AddDays(3):yyyyMMdd}
        DTEND;VALUE=DATE:{now.AddDays(5):yyyyMMdd}
        SUMMARY:Holiday
        LOCATION:Cornwall
        END:VEVENT
        BEGIN:VEVENT
        UID:old@test
        DTSTART:{now.AddDays(-30):yyyyMMdd}T090000Z
        DTEND:{now.AddDays(-30):yyyyMMdd}T100000Z
        SUMMARY:Long gone
        END:VEVENT
        END:VCALENDAR
        """.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

    [Fact]
    public async Task A_subscribed_feed_is_fetched_expanded_and_kept_secret()
    {
        var server = new FeedServer();
        server.Feeds["https://calendar.example.com/secret/basic.ics"] = (HttpStatusCode.OK, Feed(DateTime.UtcNow));
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync(),
            services: s => s.AddHttpClient(nameof(FeedSync)).ConfigurePrimaryHttpMessageHandler(() => server));
        var client = app.CreateClient();
        (await client.RegisterAsync("subscriber")).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/api/calendars", new AddFeedRequest("Work", "webcal://calendar.example.com/secret/basic.ics"));
        var added = await response.ReadAsync<CalendarView>();
        Assert.Null(added.LastError);
        Assert.NotNull(added.LastSyncedAt);
        Assert.Equal("https://calendar.example.com/secret/basic.ics", Assert.Single(server.Requested).AbsoluteUri);

        var listing = await (await client.GetAsync("/api/calendars")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret", listing, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"local\"", listing, StringComparison.Ordinal);

        var events = await (await client.GetAsync("/api/calendars/events?days=30")).ReadAsync<List<EventView>>();
        Assert.DoesNotContain(events, e => e.Title == "Long gone");
        Assert.InRange(events.Count(e => e.Title == "Standup"), 3, 5);
        var holiday = Assert.Single(events, e => e.Title == "Holiday");
        Assert.True(holiday.AllDay);
        Assert.Equal(("Cornwall", TimeSpan.FromDays(2)), (holiday.Location, holiday.EndsAt - holiday.StartsAt));

        // Feed events belong to the feed.
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/calendars/events/{holiday.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/calendars/{added.Id}")).StatusCode);
        Assert.Empty(await (await client.GetAsync("/api/calendars/events?days=30")).ReadAsync<List<EventView>>());
    }

    [Fact]
    public async Task A_feed_that_fails_says_why()
    {
        var server = new FeedServer();
        server.Feeds["https://calendar.example.com/notacalendar"] = (HttpStatusCode.OK, "<html>Sign in</html>");
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync(),
            services: s => s.AddHttpClient(nameof(FeedSync)).ConfigurePrimaryHttpMessageHandler(() => server));
        var client = app.CreateClient();
        (await client.RegisterAsync("unlucky")).EnsureSuccessStatusCode();

        var gone = await (await client.PostAsJsonAsync("/api/calendars", new AddFeedRequest("Old", "https://calendar.example.com/gone.ics"))).ReadAsync<CalendarView>();
        Assert.Contains("404", gone.LastError, StringComparison.Ordinal);

        var html = await (await client.PostAsJsonAsync("/api/calendars", new AddFeedRequest("Html", "https://calendar.example.com/notacalendar"))).ReadAsync<CalendarView>();
        Assert.Contains("isn't an iCalendar", html.LastError, StringComparison.Ordinal);

        foreach (var bad in new[] { "ftp://example.com/a.ics", "http://localhost/a.ics", "https://127.0.0.1/a.ics", "http://169.254.169.254/latest", "not a url" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/calendars", new AddFeedRequest("Bad", bad))).StatusCode);
        }
    }
}

public sealed class AwarenessUnitTests
{
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    // Thursday 1 October 2026, 17:30 in Chicago.
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 22, 30, 0, TimeSpan.Zero);

    private static CalendarEvent At(DateTimeOffset start, TimeSpan length, bool allDay = false) =>
        new() { Title = "x", StartsAt = start, EndsAt = start + length, AllDay = allDay };

    [Fact]
    public void Event_times_are_said_relative_to_now()
    {
        Assert.Equal("in 25 minutes", Spoken.EventWhen(At(Now.AddMinutes(25), TimeSpan.FromHours(1)), Chicago, Now));
        Assert.Equal("now, until 6:00 PM", Spoken.EventWhen(At(Now.AddMinutes(-10), TimeSpan.FromMinutes(40)), Chicago, Now));
        Assert.Equal("tomorrow at 10:00 AM", Spoken.EventWhen(At(Now.AddHours(16.5), TimeSpan.FromHours(1)), Chicago, Now));
        Assert.Equal("today (all day)", Spoken.EventWhen(At(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromDays(1), allDay: true), Chicago, Now));
        Assert.Equal("on Saturday (all day, for 2 days)", Spoken.EventWhen(At(new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromDays(2), allDay: true), Chicago, Now));
    }

    [Fact]
    public void A_feed_in_a_time_zone_is_read_in_that_zone()
    {
        const string ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//T//EN\r\nBEGIN:VEVENT\r\nUID:a\r\n" +
            "DTSTART;TZID=Europe/London:20261005T090000\r\nDTEND;TZID=Europe/London:20261005T100000\r\n" +
            "RRULE:FREQ=DAILY;COUNT=3\r\nEXDATE;TZID=Europe/London:20261006T090000\r\nSUMMARY:Gym\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var occurrences = IcsFeed.Read(ics, Now, Now.AddDays(30));

        Assert.Equal(
            [new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 7, 8, 0, 0, TimeSpan.Zero)],
            occurrences.Select(o => o.StartsAt));
        Assert.All(occurrences, o => Assert.Equal(TimeSpan.FromHours(1), o.EndsAt - o.StartsAt));
        Assert.NotEqual(occurrences[0].ExternalId, occurrences[1].ExternalId);
    }

    [Fact]
    public void Garbage_is_not_a_feed() => Assert.Throws<FormatException>(() => IcsFeed.Read("<html></html>", Now, Now.AddDays(1)));

    [Theory]
    [InlineData("webcal://cal.example.com/x.ics", "https://cal.example.com/x.ics")]
    [InlineData("http://nextcloud.lan/remote.php/dav/x?export", "http://nextcloud.lan/remote.php/dav/x?export")]
    [InlineData("https://192.168.1.20/cal.ics", "https://192.168.1.20/cal.ics")]
    [InlineData("http://localhost/x.ics", null)]
    [InlineData("http://[::1]/x.ics", null)]
    [InlineData("http://169.254.169.254/latest/meta-data", null)]
    [InlineData("file:///etc/passwd", null)]
    public void Feed_addresses_are_checked(string url, string? expected) =>
        Assert.Equal(expected, FeedSync.Acceptable(url)?.AbsoluteUri);

    [Theory]
    [InlineData("  their   name is Sam ", "their name is Sam.")]
    [InlineData("Is it?", "Is it?")]
    [InlineData("", "")]
    public void Memories_are_tidied(string text, string expected) => Assert.Equal(expected, MemoryStore.Normalise(text));

    [Fact]
    public void Memories_come_after_the_rules_so_the_cached_prefix_survives_a_new_one()
    {
        var context = new PromptContext(new FamiliarProfile { Name = "Pip" }, "sam", [], "hi", Now, Chicago,
            ["On their calendar: Dentist, in 25 minutes."], ["Their name is Sam."]);

        var messages = PromptBuilder.Build(context);
        var without = PromptBuilder.Build(context with { Memories = [] });

        Assert.StartsWith(without[0].Content, messages[0].Content, StringComparison.Ordinal);
        Assert.EndsWith("- Their name is Sam.", messages[0].Content, StringComparison.Ordinal);
        Assert.Equal("(Thursday evening, 1 October 2026, half past five (5:30 PM). On their calendar: Dentist, in 25 minutes) hi", messages[^1].Content);
    }
}
