using System.Security.Cryptography;
using Familiar.Domain.Expression;
using Familiar.Domain.Hooks;
using Familiar.Persistence;
using Familiar.Server.Auth;
using Familiar.Server.Conversation;
using Familiar.Server.Infrastructure;
using Familiar.Server.Notifications;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Familiar.Server.Hooks;

/// <param name="Message">What to say.</param>
/// <param name="Style">"say" (word for word, the default) or "tell" (the familiar puts it in its own words).</param>
/// <param name="TimeZone">For "tell": the user's IANA time zone, if the caller knows it.</param>
public sealed record AnnounceRequest(string? Message, string? Style = null, string? TimeZone = null);

public sealed record HookTokenView(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <param name="Id">The token's id.</param>
/// <param name="Name">What uses it.</param>
/// <param name="Token">Shown once, at creation.</param>
public sealed record NewHookTokenView(Guid Id, string Name, string Token);

public sealed record NameRequest(string? Name);

public sealed record WebhookView(Guid Id, string Name, string UrlHost, IReadOnlyList<string> Events, bool Enabled, DateTimeOffset? LastDeliveredAt, string? LastResult);

/// <summary>How a test delivery went.</summary>
/// <param name="Ok">The receiver answered with a success.</param>
/// <param name="Result">What happened: the status it answered with, or why it couldn't be reached.</param>
public sealed record WebhookTestResult(bool Ok, string Result);

/// <param name="Webhook">The webhook.</param>
/// <param name="Secret">The signing secret, shown once, at creation.</param>
public sealed record NewWebhookView(WebhookView Webhook, string Secret);

public sealed record WebhookRequest(string? Name, string? Url, IReadOnlyList<string>? Events, bool? Enabled = null);

/// <summary>
/// Hooks (PLAN.md §6.5): other systems making the familiar speak, and the familiar telling other
/// systems what happened.
/// </summary>
public static class HookEndpoints
{
    public const string TokenPrefix = "fhk_";
    public const int MaxAnnouncementLength = 500;

    public static void MapHookEndpoints(this IEndpointRouteBuilder routes)
    {
        // The token is the credential here, not the session: Home Assistant has no cookie.
        routes.MapPost("/api/hooks/announce", AnnounceAsync).AllowAnonymous().RequireRateLimiting(RateLimiting.Conversation);

        var tokens = routes.MapGroup("/api/hooks/tokens").RequireAuthorization();
        tokens.MapGet("/", ListTokensAsync);
        tokens.MapPost("/", CreateTokenAsync);
        tokens.MapDelete("/{id:guid}", DeleteTokenAsync);

        var webhooks = routes.MapGroup("/api/webhooks").RequireAuthorization();
        webhooks.MapGet("/", ListWebhooksAsync);
        webhooks.MapPost("/", CreateWebhookAsync);
        webhooks.MapPut("/{id:guid}", UpdateWebhookAsync);
        webhooks.MapDelete("/{id:guid}", DeleteWebhookAsync);
        webhooks.MapPost("/{id:guid}/test", TestWebhookAsync);
    }

    /// <summary>
    /// "Say this": the familiar announces it on every open device - spoken, with a chime - and as a
    /// notification where it isn't open. Authenticated by a hook token in the Authorization header.
    /// </summary>
    private static async Task<IResult> AnnounceAsync(
        AnnounceRequest request,
        HttpContext http,
        IDbContextFactory<FamiliarDbContext> db,
        ConversationTurns turns,
        PushSender push,
        RecentAnnouncements recent,
        ConversationStore store,
        ConversationHub hub,
        TimeProvider clock,
        CancellationToken ct)
    {
        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        if (!AccessTokenSecret.TryParse(token, TokenPrefix, out var tokenId, out var secret))
        {
            return Results.Unauthorized();
        }

        HookToken? hook;
        await using (var context = await db.CreateDbContextAsync(ct))
        {
            hook = await context.HookTokens.FirstOrDefaultAsync(t => t.Id == tokenId, ct);
            if (hook is null || !AccessTokenSecret.Matches(secret, hook.SecretHash))
            {
                return Results.Unauthorized();
            }

            hook.LastUsedAt = clock.GetUtcNow();
            await context.SaveChangesAsync(ct);
        }

        var message = string.Join(' ', (request.Message ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (message.Length is 0 or > MaxAnnouncementLength)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, $"message is 1-{MaxAnnouncementLength} characters.");
        }

        var id = Guid.CreateVersion7();
        var familiarName = (await store.ProfileAsync(hook.AccountId, ct)).Name;
        recent.Add(hook.AccountId, new NotificationView(id, NotifyKinds.Announcement, $"{familiarName}: from {hook.Name}", message, clock.GetUtcNow()));

        if (string.Equals(request.Style, "tell", StringComparison.OrdinalIgnoreCase))
        {
            // In its own words. Quoted and attributed, and the turn gets no tools, so the message
            // can only ever be passed on, not acted on.
            hub.For(hook.AccountId).Publish(EventTypes.Notify, new NotifyEvent(id, NotifyKinds.Announcement, hook.Name, message));
            var cue = $"[{hook.Name} asked you to pass this on to them: \"{message.Replace('"', '\'')}\". " +
                "Tell them in your own words, in a sentence or two.]";
            await turns.TellAsync(hook.AccountId, cue, ConversationEndpoints.Zone(request.TimeZone), speak: true, CancellationToken.None);
        }
        else
        {
            await turns.AnnounceAsync(new Announcement(
                hook.AccountId, message, Emotion.Surprised, 0.5, Gesture.Tilt,
                new NotifyEvent(id, NotifyKinds.Announcement, hook.Name, message)), CancellationToken.None);
        }

        await push.NotifyAsync(hook.AccountId, CancellationToken.None);
        return Results.Accepted();
    }

    private static async Task<List<HookTokenView>> ListTokensAsync(HttpContext http, IDbContextFactory<FamiliarDbContext> db, CancellationToken ct)
    {
        var accountId = http.User.AccountId()!.Value;
        await using var context = await db.CreateDbContextAsync(ct);
        return await context.HookTokens.AsNoTracking()
            .Where(t => t.AccountId == accountId)
            .OrderBy(t => t.CreatedAt)
            .Select(t => new HookTokenView(t.Id, t.Name, t.CreatedAt, t.LastUsedAt))
            .ToListAsync(ct);
    }

    private static async Task<IResult> CreateTokenAsync(NameRequest request, HttpContext http, IDbContextFactory<FamiliarDbContext> db, TimeProvider clock, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > 64)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "Name it after what will use it, e.g. Home Assistant (1-64 characters).");
        }

        var accountId = http.User.AccountId()!.Value;
        await using var context = await db.CreateDbContextAsync(ct);
        if (await context.HookTokens.CountAsync(t => t.AccountId == accountId, ct) >= HookToken.MaxPerAccount)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, $"You can have {HookToken.MaxPerAccount} hook tokens. Delete one first.");
        }

        var id = Guid.CreateVersion7();
        var (token, hash) = AccessTokenSecret.Mint(id, TokenPrefix);
        context.HookTokens.Add(new HookToken { Id = id, AccountId = accountId, Name = name, SecretHash = hash, CreatedAt = clock.GetUtcNow() });
        await context.SaveChangesAsync(ct);
        return Results.Ok(new NewHookTokenView(id, name, token));
    }

    private static async Task<IResult> DeleteTokenAsync(Guid id, HttpContext http, IDbContextFactory<FamiliarDbContext> db, CancellationToken ct)
    {
        var accountId = http.User.AccountId()!.Value;
        await using var context = await db.CreateDbContextAsync(ct);
        return await context.HookTokens.Where(t => t.Id == id && t.AccountId == accountId).ExecuteDeleteAsync(ct) > 0
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static WebhookView View(Webhook w, WebhookDispatcher dispatcher)
    {
        string host;
        try
        {
            host = new Uri(dispatcher.Protector.Unprotect(w.ProtectedUrl)).Host;
        }
        catch (CryptographicException)
        {
            host = "(unreadable)";
        }

        return new WebhookView(w.Id, w.Name, host, w.Events, w.Enabled, w.LastDeliveredAt, w.LastResult);
    }

    private static async Task<List<WebhookView>> ListWebhooksAsync(
        HttpContext http, IDbContextFactory<FamiliarDbContext> db, WebhookDispatcher dispatcher, CancellationToken ct)
    {
        var accountId = http.User.AccountId()!.Value;
        await using var context = await db.CreateDbContextAsync(ct);
        var hooks = await context.Webhooks.AsNoTracking().Where(w => w.AccountId == accountId).OrderBy(w => w.CreatedAt).ToListAsync(ct);
        return [.. hooks.Select(w => View(w, dispatcher))];
    }

    private static string? Problem(WebhookRequest request, out Uri? url, out List<string> events)
    {
        url = OutboundUrl.Check(request.Url);
        events = [.. (request.Events ?? []).Distinct()];
        if (request.Name?.Trim() is not { Length: > 0 and <= 64 })
        {
            return "Webhook names are 1-64 characters.";
        }

        if (url is null)
        {
            return "That isn't an address Familiar can send to (http or https, not this machine).";
        }

        return events.Count == 0 || events.Any(e => !WebhookEvents.All.Contains(e))
            ? $"Choose one or more events: {string.Join(", ", WebhookEvents.All)}."
            : null;
    }

    private static async Task<IResult> CreateWebhookAsync(
        WebhookRequest request, HttpContext http, IDbContextFactory<FamiliarDbContext> db, WebhookDispatcher dispatcher, TimeProvider clock, CancellationToken ct)
    {
        if (Problem(request, out var url, out var events) is { } problem)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, problem);
        }

        var accountId = http.User.AccountId()!.Value;
        await using var context = await db.CreateDbContextAsync(ct);
        if (await context.Webhooks.CountAsync(w => w.AccountId == accountId, ct) >= Webhook.MaxPerAccount)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, $"You can have {Webhook.MaxPerAccount} webhooks. Delete one first.");
        }

        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var hook = new Webhook
        {
            AccountId = accountId,
            Name = request.Name!.Trim(),
            ProtectedUrl = dispatcher.Protector.Protect(url!.AbsoluteUri),
            ProtectedSecret = dispatcher.Protector.Protect(secret),
            Events = events,
            Enabled = request.Enabled ?? true,
            CreatedAt = clock.GetUtcNow(),
        };
        context.Webhooks.Add(hook);
        await context.SaveChangesAsync(ct);
        return Results.Ok(new NewWebhookView(View(hook, dispatcher), secret));
    }

    private static async Task<IResult> UpdateWebhookAsync(
        Guid id, WebhookRequest request, HttpContext http, IDbContextFactory<FamiliarDbContext> db, WebhookDispatcher dispatcher, CancellationToken ct)
    {
        var accountId = http.User.AccountId()!.Value;
        await using var context = await db.CreateDbContextAsync(ct);
        var hook = await context.Webhooks.FirstOrDefaultAsync(w => w.Id == id && w.AccountId == accountId, ct);
        if (hook is null)
        {
            return Results.NotFound();
        }

        // The address may be left out: it's never sent back, so the client can't echo it.
        var withUrl = request with { Url = request.Url ?? dispatcher.Protector.Unprotect(hook.ProtectedUrl) };
        if (Problem(withUrl, out var url, out var events) is { } problem)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, problem);
        }

        hook.Name = request.Name!.Trim();
        hook.ProtectedUrl = dispatcher.Protector.Protect(url!.AbsoluteUri);
        hook.Events = events;
        hook.Enabled = request.Enabled ?? hook.Enabled;
        await context.SaveChangesAsync(ct);
        return Results.Ok(View(hook, dispatcher));
    }

    private static async Task<IResult> DeleteWebhookAsync(Guid id, HttpContext http, IDbContextFactory<FamiliarDbContext> db, CancellationToken ct)
    {
        var accountId = http.User.AccountId()!.Value;
        await using var context = await db.CreateDbContextAsync(ct);
        return await context.Webhooks.Where(w => w.Id == id && w.AccountId == accountId).ExecuteDeleteAsync(ct) > 0
            ? Results.NoContent()
            : Results.NotFound();
    }

    /// <summary>Sends a test event now and says how it went.</summary>
    private static async Task<IResult> TestWebhookAsync(
        Guid id, HttpContext http, IDbContextFactory<FamiliarDbContext> db, WebhookDispatcher dispatcher, TimeProvider clock, CancellationToken ct)
    {
        var accountId = http.User.AccountId()!.Value;
        await using var context = await db.CreateDbContextAsync(ct);
        var hook = await context.Webhooks.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id && w.AccountId == accountId, ct);
        if (hook is null)
        {
            return Results.NotFound();
        }

        var (ok, result) = await dispatcher.DeliverAsync(hook, new WebhookPayload(WebhookEvents.Test, clock.GetUtcNow(), new { message = "Hello from Familiar." }), ct);
        return Results.Ok(new WebhookTestResult(ok, result));
    }
}
