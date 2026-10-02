using Familiar.Agent.Llm;
using Familiar.Server.Auth;
using Familiar.Server.Infrastructure;
using Microsoft.AspNetCore.Http.Features;

namespace Familiar.Server.Conversation;

public sealed record SendMessageRequest(string? Text, string? TimeZone);

public static class ConversationEndpoints
{
    public const int MaxMessageLength = 4000;
    public const int MaxHistory = 200;

    /// <summary>
    /// Well inside nginx's 60 s proxy_read_timeout, so an idle stream is never cut by the proxy.
    /// Also how a vanished client is noticed: the ping's write fails.
    /// </summary>
    internal static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);

    public static void MapConversationEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/conversation").RequireAuthorization();

        group.MapGet("/stream", StreamAsync);
        group.MapGet("/messages", HistoryAsync);
        group.MapPost("/messages", SendAsync).RequireRateLimiting(RateLimiting.Conversation);
        group.MapPost("/interrupt", InterruptAsync);
        group.MapPost("/new", StartOverAsync);
    }

    /// <summary>
    /// The live conversation as server-sent events (PLAN.md §3.1). Browsers authenticate with the
    /// cookie, because EventSource can't set headers; the phone apps send their device token.
    /// </summary>
    private static async Task StreamAsync(
        HttpContext http,
        ConversationHub hub,
        ConversationStore store,
        ILlmClient llm,
        CancellationToken ct)
    {
        var accountId = http.User.AccountId()!.Value;

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache, no-transform";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var lastEventId = http.Request.Headers["Last-Event-ID"].ToString();
        using var subscription = hub.For(accountId).Subscribe(lastEventId);

        try
        {
            // Reconnect quickly: a phone coming back from a tunnel shouldn't wait.
            await WriteAsync(http, "retry: 2000\n\n", ct);

            if (subscription.Resync)
            {
                await WriteAsync(http, ConversationHub.Frame(EventTypes.Resync, new EmptyEvent()), ct);
            }

            foreach (var missed in subscription.Replay)
            {
                await WriteAsync(http, missed.Frame, ct);
            }

            var profile = await store.ProfileAsync(accountId, ct);
            var ready = new ReadyEvent(new FamiliarSummary(profile.Name, profile.Expressiveness), await llm.IsAvailableAsync(ct));
            await WriteAsync(http, ConversationHub.Frame(EventTypes.Ready, ready), ct);

            Task<bool>? waiting = null;
            while (!ct.IsCancellationRequested)
            {
                waiting ??= subscription.Reader.WaitToReadAsync(ct).AsTask();
                if (await Task.WhenAny(waiting, Task.Delay(PingInterval, ct)) != waiting)
                {
                    await WriteAsync(http, ": ping\n\n", ct);
                    continue;
                }

                if (!await waiting)
                {
                    // The hub dropped this subscriber for falling behind. Ending the response makes
                    // the client reconnect and replay from where it got to.
                    return;
                }

                waiting = null;
                while (subscription.Reader.TryRead(out var next))
                {
                    await WriteAsync(http, next.Frame, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client went away.
        }
    }

    private static async Task<IResult> HistoryAsync(HttpContext http, ConversationStore store, int? limit, CancellationToken ct)
    {
        var messages = await store.RecentAsync(http.User.AccountId()!.Value, Math.Clamp(limit ?? 50, 1, MaxHistory), ct);
        return TypedResults.Ok(messages.Select(MessageView.From).ToList());
    }

    private static async Task<IResult> SendAsync(
        SendMessageRequest request, HttpContext http, ConversationTurns turns, CancellationToken ct)
    {
        var text = request.Text?.Trim() ?? string.Empty;
        if (text.Length is 0 or > MaxMessageLength)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, $"Messages are 1-{MaxMessageLength} characters.");
        }

        var message = await turns.SubmitAsync(http.User.AccountId()!.Value, text, Zone(request.TimeZone), ct);
        return TypedResults.Accepted((string?)null, MessageView.From(message));
    }

    private static async Task<IResult> InterruptAsync(HttpContext http, ConversationTurns turns)
    {
        await turns.InterruptAsync(http.User.AccountId()!.Value);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> StartOverAsync(HttpContext http, ConversationTurns turns, CancellationToken ct)
    {
        await turns.StartOverAsync(http.User.AccountId()!.Value, ct);
        return TypedResults.NoContent();
    }

    /// <summary>The client's IANA zone ("America/Chicago"); UTC if it's missing or unknown.</summary>
    internal static TimeZoneInfo Zone(string? id) =>
        !string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Utc;

    private static async Task WriteAsync(HttpContext http, string frame, CancellationToken ct)
    {
        await http.Response.WriteAsync(frame, ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
