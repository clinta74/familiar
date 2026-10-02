using Familiar.Domain.Calendars;
using Familiar.Server.Auth;

namespace Familiar.Server.Calendars;

/// <summary>A calendar as Settings shows it. Never the feed's address: that's a secret.</summary>
public sealed record CalendarView(Guid Id, CalendarKind Kind, string Name, DateTimeOffset? LastSyncedAt, string? LastError)
{
    public static CalendarView From(CalendarSource s) => new(s.Id, s.Kind, s.Name, s.LastSyncedAt, s.LastError);
}

public sealed record EventView(Guid Id, Guid CalendarId, string Title, string? Location, DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool AllDay)
{
    public static EventView From(CalendarEvent e) => new(e.Id, e.SourceId, e.Title, e.Location, e.StartsAt, e.EndsAt, e.AllDay);
}

/// <param name="Name">What to call it.</param>
/// <param name="Url">Its secret iCal address (https:// or webcal://).</param>
public sealed record AddFeedRequest(string? Name, string? Url);

public static class CalendarEndpoints
{
    public static void MapCalendarEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/calendars").RequireAuthorization();

        group.MapGet("/", async (HttpContext http, CalendarStore store, CancellationToken ct) =>
        {
            var accountId = http.User.AccountId()!.Value;
            await store.LocalAsync(accountId, ct);
            return (await store.SourcesAsync(accountId, ct)).Select(CalendarView.From).ToList();
        });

        group.MapPost("/", AddFeedAsync);

        group.MapPost("/{id:guid}/sync", async (Guid id, HttpContext http, CalendarStore store, FeedSync sync, CancellationToken ct) =>
        {
            var source = (await store.SourcesAsync(http.User.AccountId()!.Value, ct)).FirstOrDefault(s => s.Id == id && s.Kind == CalendarKind.Ics);
            if (source is null)
            {
                return Results.NotFound();
            }

            await sync.SyncAsync(source, ct);
            var after = (await store.SourcesAsync(source.AccountId, ct)).First(s => s.Id == id);
            return Results.Ok(CalendarView.From(after));
        });

        group.MapDelete("/{id:guid}", async (Guid id, HttpContext http, CalendarStore store, CancellationToken ct) =>
            await store.RemoveAsync(http.User.AccountId()!.Value, id, ct) ? Results.NoContent() : Results.NotFound());

        group.MapGet("/events", async (HttpContext http, CalendarStore store, TimeProvider clock, int? days, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            var events = await store.BetweenAsync(http.User.AccountId()!.Value, now, now.AddDays(Math.Clamp(days ?? 14, 1, 60)), ct);
            return events.Select(EventView.From).ToList();
        });

        group.MapDelete("/events/{id:guid}", async (Guid id, HttpContext http, CalendarStore store, CancellationToken ct) =>
            await store.DeleteLocalEventAsync(http.User.AccountId()!.Value, id, ct) ? Results.NoContent() : Results.NotFound());
    }

    /// <summary>Subscribes to a feed and fetches it straight away, so a bad address is reported now.</summary>
    private static async Task<IResult> AddFeedAsync(
        AddFeedRequest request, HttpContext http, CalendarStore store, FeedSync sync, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > CalendarSource.MaxNameLength)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, $"Calendar names are 1-{CalendarSource.MaxNameLength} characters.");
        }

        if (FeedSync.Acceptable(request.Url) is not { } url)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest,
                "That isn't a calendar address Familiar can use. Paste the secret iCal address (https:// or webcal://).");
        }

        var accountId = http.User.AccountId()!.Value;
        if ((await store.SourcesAsync(accountId, ct)).Count >= 20)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "That's as many calendars as Familiar keeps.");
        }

        var source = await store.AddFeedAsync(accountId, name, url, ct);
        await sync.SyncAsync(source, ct);
        var after = (await store.SourcesAsync(accountId, ct)).First(s => s.Id == source.Id);
        return Results.Ok(CalendarView.From(after));
    }
}
