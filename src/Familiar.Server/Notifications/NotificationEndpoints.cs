using Familiar.Domain.Notifications;
using Familiar.Domain.Scheduling;
using Familiar.Persistence;
using Familiar.Server.Auth;
using Familiar.Server.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace Familiar.Server.Notifications;

/// <param name="Endpoint">The push service URL from the browser's PushSubscription.</param>
/// <param name="Keys">The browser's keys; stored, though content-free pushes don't need them.</param>
public sealed record SubscribeRequest(string? Endpoint, SubscriptionKeys? Keys);

/// <param name="P256dh">The browser's public key.</param>
/// <param name="Auth">The browser's auth secret.</param>
public sealed record SubscriptionKeys(string? P256dh, string? Auth);

/// <param name="Endpoint">The subscription to remove.</param>
public sealed record UnsubscribeRequest(string? Endpoint);

public sealed record PushKeyResponse(string PublicKey);

/// <summary>Something that just happened, for the service worker to show as a notification.</summary>
public sealed record NotificationView(
    Guid Id,
    [property: System.ComponentModel.DataAnnotations.AllowedValues(Conversation.NotifyKinds.Timer, Conversation.NotifyKinds.Reminder, Conversation.NotifyKinds.Announcement)] string Kind,
    string Title,
    string Body,
    DateTimeOffset At);

/// <summary>
/// Announcements from hooks in the last few minutes, for the service worker. In memory: unlike a
/// fired timer there's no row to find it by, and one that's lost in a restart is past its moment.
/// </summary>
public sealed class RecentAnnouncements(TimeProvider clock)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, List<NotificationView>> _byAccount = new();

    public void Add(Guid accountId, NotificationView notification)
    {
        var list = _byAccount.GetOrAdd(accountId, _ => []);
        lock (list)
        {
            list.Add(notification);
            list.RemoveAll(n => n.At < clock.GetUtcNow() - NotificationEndpoints.RecentWindow);
        }
    }

    public IReadOnlyList<NotificationView> For(Guid accountId)
    {
        if (!_byAccount.TryGetValue(accountId, out var list))
        {
            return [];
        }

        lock (list)
        {
            return [.. list.Where(n => n.At >= clock.GetUtcNow() - NotificationEndpoints.RecentWindow)];
        }
    }
}

public static class NotificationEndpoints
{
    /// <summary>How far back the service worker is told about: a push delayed longer than this isn't worth showing.</summary>
    internal static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(15);

    public static void MapNotificationEndpoints(this IEndpointRouteBuilder routes)
    {
        var push = routes.MapGroup("/api/push").RequireAuthorization();
        push.MapGet("/key", async (VapidKeys keys, CancellationToken ct) => new PushKeyResponse(await keys.PublicKeyAsync(ct)));
        push.MapPost("/subscriptions", SubscribeAsync);
        push.MapDelete("/subscriptions", UnsubscribeAsync);

        routes.MapGet("/api/notifications/recent", RecentAsync).RequireAuthorization();
    }

    private static async Task<IResult> SubscribeAsync(
        SubscribeRequest request, HttpContext http, IDbContextFactory<FamiliarDbContext> db, PushOptions options, TimeProvider clock, CancellationToken ct)
    {
        if (request.Endpoint is not { } endpoint || !options.Accepts(endpoint))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["endpoint"] = ["Not a push service URL."] });
        }

        var accountId = http.User.AccountId()!.Value;

        // A phone signs in with a device token: its endpoint is tied to that device.
        var deviceId = http.User.DeviceId();
        await using var context = await db.CreateDbContextAsync(ct);

        // A phone's new endpoint replaces its old one.
        if (deviceId is { } device)
        {
            await context.PushSubscriptions.Where(s => s.DeviceId == device && s.Endpoint != endpoint).ExecuteDeleteAsync(ct);
        }

        // One row per browser: re-subscribing (or a different account signing in on it) takes it over.
        var existing = await context.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint, ct);
        if (existing is not null && existing.AccountId != accountId)
        {
            context.PushSubscriptions.Remove(existing);
            existing = null;
        }

        if (existing is null)
        {
            context.PushSubscriptions.Add(new PushSubscription
            {
                AccountId = accountId,
                Endpoint = endpoint,
                P256dh = Truncate(request.Keys?.P256dh, 256),
                Auth = Truncate(request.Keys?.Auth, 64),
                DeviceId = deviceId,
                CreatedAt = clock.GetUtcNow(),
            });
        }
        else
        {
            existing.P256dh = Truncate(request.Keys?.P256dh, 256);
            existing.Auth = Truncate(request.Keys?.Auth, 64);
        }

        await context.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UnsubscribeAsync(
        [Microsoft.AspNetCore.Mvc.FromBody] UnsubscribeRequest request, HttpContext http, IDbContextFactory<FamiliarDbContext> db, CancellationToken ct)
    {
        var accountId = http.User.AccountId()!.Value;
        await using var context = await db.CreateDbContextAsync(ct);
        await context.PushSubscriptions
            .Where(s => s.AccountId == accountId && s.Endpoint == request.Endpoint)
            .ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }

    /// <summary>What the service worker shows when a push arrives (the push itself says nothing).</summary>
    private static async Task<IReadOnlyList<NotificationView>> RecentAsync(
        HttpContext http, SchedulerStore store, Conversation.ConversationStore conversations, RecentAnnouncements announcements,
        TimeProvider clock, CancellationToken ct)
    {
        var accountId = http.User.AccountId()!.Value;
        var fired = await store.FiredSinceAsync(accountId, clock.GetUtcNow() - RecentWindow, ct);
        var name = (await conversations.ProfileAsync(accountId, ct)).Name;

        var jobs = fired.Select(job => new NotificationView(
            job.Id,
            job.Kind == JobKind.Timer ? Conversation.NotifyKinds.Timer : Conversation.NotifyKinds.Reminder,
            job.Kind == JobKind.Timer ? $"{name}: time's up" : $"{name}: reminder",
            Spoken.Announcement(job, job.FiredAt ?? job.DueAt),
            job.FiredAt ?? job.DueAt));

        return [.. jobs.Concat(announcements.For(accountId)).OrderBy(n => n.At)];
    }

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : null;
}
