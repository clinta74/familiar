using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Familiar.Agent.Llm;
using Familiar.Persistence;
using Familiar.Server.Calendars;
using Familiar.Server.Conversation;
using Familiar.Server.Devices;
using Familiar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Familiar.Server.Tests;

[Collection(PostgresCollection.Name)]
public sealed class DeviceCalendarTests(PostgresFixture postgres)
{
    private static async Task<(HttpClient Browser, HttpClient Phone, Guid DeviceId)> SignedInAsync(FamiliarAppFactory app)
    {
        var browser = app.CreateClient();
        (await browser.RegisterAsync("sam")).EnsureSuccessStatusCode();
        var login = await app.CreateClient().DeviceLoginAsync("sam", device: "Pixel 10");
        return (browser, app.DeviceClient(login.Token), Guid.Parse(login.Device.Id.ToString()));
    }

    private static DeviceEventView Event(string id, string title, DateTimeOffset start, int minutes = 60) =>
        new(id, title, null, start, start.AddMinutes(minutes), false, "sam@gmail.com");

    [Fact]
    public async Task A_phone_uploads_its_calendars_and_the_familiar_sees_them()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var (browser, phone, _) = await SignedInAsync(app);
        var soon = DateTimeOffset.UtcNow.AddMinutes(40);

        var response = await phone.PutAsJsonAsync("/api/calendars/device", new DeviceCalendarUpload([
            Event("1", "School run", soon),
            Event("2", "Book club", soon.AddDays(1)),
        ]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var calendars = await (await browser.GetAsync("/api/calendars")).ReadAsync<List<CalendarView>>();
        Assert.Contains(calendars, c => c.Kind == Domain.Calendars.CalendarKind.Device && c.Name == "Pixel 10");

        var events = await (await browser.GetAsync("/api/calendars/events")).ReadAsync<List<EventView>>();
        Assert.Equal(["School run", "Book club"], events.Select(e => e.Title));

        // The next upload replaces the last.
        await phone.PutAsJsonAsync("/api/calendars/device", new DeviceCalendarUpload([Event("3", "Dentist", soon)]));
        Assert.Equal("Dentist", Assert.Single(await (await browser.GetAsync("/api/calendars/events")).ReadAsync<List<EventView>>()).Title);

        // And the familiar knows: it's in the situation notes.
        await using var stream = await SseClient.ConnectAsync(browser);
        await stream.ReadUntilAsync(EventTypes.Ready);
        await browser.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("what's next?", "UTC"));
        await stream.ReadUntilStateAsync(FamiliarState.Idle);
        Assert.Contains("Dentist, in 40 minutes", app.Llm.LastPrompt![^1].Content, StringComparison.Ordinal);

        // Only phones upload.
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PutAsJsonAsync("/api/calendars/device", new DeviceCalendarUpload([]))).StatusCode);
    }

    [Fact]
    public async Task A_phone_signed_out_or_gone_quiet_stops_counting()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var (browser, phone, deviceId) = await SignedInAsync(app);
        await phone.PutAsJsonAsync("/api/calendars/device", new DeviceCalendarUpload([Event("1", "Stale thing", DateTimeOffset.UtcNow.AddHours(2))]));

        // Not synced for four days: left out.
        await using (var db = await app.Services.GetRequiredService<IDbContextFactory<FamiliarDbContext>>().CreateDbContextAsync())
        {
            await db.CalendarSources.Where(s => s.DeviceId == deviceId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSyncedAt, DateTimeOffset.UtcNow.AddDays(-4)));
        }

        Assert.Empty(await (await browser.GetAsync("/api/calendars/events")).ReadAsync<List<EventView>>());

        // Signed out: gone altogether.
        Assert.Equal(HttpStatusCode.NoContent, (await browser.DeleteAsync($"/api/auth/devices/{deviceId}")).StatusCode);
        Assert.DoesNotContain(await (await browser.GetAsync("/api/calendars")).ReadAsync<List<CalendarView>>(),
            c => c.Kind == Domain.Calendars.CalendarKind.Device);
    }

    [Fact]
    public async Task With_the_phone_connected_new_events_go_into_its_calendar()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        app.Llm.ThenCall("create_event", """{"title": "Dentist", "day": "tomorrow", "time": "10:00"}""").ThenSay("[joy:0.5] Done.");
        var (browser, phone, _) = await SignedInAsync(app);

        await using var phoneStream = await SseClient.ConnectAsync(phone, capabilities: "calendar.write,teleport");
        var connection = (await phoneStream.ReadUntilAsync(EventTypes.Ready))[^1].Payload<ReadyEvent>().ConnectionId;
        Assert.NotNull(connection);

        await browser.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("dentist tomorrow at ten", "UTC"));

        // The phone is asked, and answers.
        var invoke = (await phoneStream.ReadUntilAsync(EventTypes.ToolInvoke))[^1].Payload<ToolInvokeEvent>();
        Assert.Equal(("calendar.add", connection), (invoke.Tool, invoke.ConnectionId));
        Assert.Equal("Dentist", invoke.Arguments["title"]!.GetValue<string>());
        Assert.Equal(DateTimeOffset.UtcNow.Date.AddDays(1).AddHours(10), DateTimeOffset.Parse(invoke.Arguments["startsAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture).UtcDateTime);

        Assert.Equal(HttpStatusCode.NoContent, (await phone.PostAsJsonAsync("/api/conversation/tool-result",
            new ToolResultRequest(invoke.CallId, true, new JsonObject { ["calendar"] = "sam@gmail.com" }))).StatusCode);
        await phoneStream.ReadUntilStateAsync(FamiliarState.Idle);

        var told = app.Llm.LastPrompt!.Single(m => m.Role == ChatRole.Tool).Content;
        Assert.Contains("phone calendar", told, StringComparison.Ordinal);
        Assert.Contains("sam@gmail.com", told, StringComparison.Ordinal);

        // Not also put in Familiar's own calendar.
        Assert.Empty(await (await browser.GetAsync("/api/calendars/events")).ReadAsync<List<EventView>>());

        // A late or repeated answer finds nothing waiting.
        Assert.Equal(HttpStatusCode.NotFound, (await phone.PostAsJsonAsync("/api/conversation/tool-result", new ToolResultRequest(invoke.CallId, true))).StatusCode);
    }

    [Fact]
    public async Task A_phone_that_doesnt_answer_doesnt_lose_the_event()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync(), new Dictionary<string, string> { ["Devices:ToolTimeoutSeconds"] = "1" });
        app.Llm.ThenCall("create_event", """{"title": "Lunch", "day": "tomorrow", "time": "12:30"}""").ThenSay("[joy:0.5] Done.");
        var (browser, phone, _) = await SignedInAsync(app);
        await using var phoneStream = await SseClient.ConnectAsync(phone, capabilities: "calendar.write");
        await phoneStream.ReadUntilAsync(EventTypes.Ready);

        await using var stream = await SseClient.ConnectAsync(browser);
        await stream.ReadUntilAsync(EventTypes.Ready);
        await browser.PostAsJsonAsync("/api/conversation/messages", new SendMessageRequest("lunch tomorrow", "UTC"));
        await stream.ReadUntilStateAsync(FamiliarState.Idle, TimeSpan.FromSeconds(10));

        Assert.Equal("Lunch", Assert.Single(await (await browser.GetAsync("/api/calendars/events")).ReadAsync<List<EventView>>()).Title);
        Assert.Contains("Familiar's own calendar", app.Llm.LastPrompt!.Single(m => m.Role == ChatRole.Tool).Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Browsers_dont_register_as_devices()
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());
        var (browser, _, _) = await SignedInAsync(app);
        await using var stream = await SseClient.ConnectAsync(browser, capabilities: "calendar.write");
        Assert.Null((await stream.ReadUntilAsync(EventTypes.Ready))[^1].Payload<ReadyEvent>().ConnectionId);
        Assert.Null(app.Services.GetRequiredService<DeviceTools>().Find(Guid.Empty, DeviceCapabilities.CalendarWrite));
    }
}
