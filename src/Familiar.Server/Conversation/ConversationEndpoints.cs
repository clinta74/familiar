using Familiar.Agent.Llm;
using Familiar.Agent.Speech;
using Familiar.Server.Auth;
using Familiar.Server.Infrastructure;
using Microsoft.AspNetCore.Http.Features;

namespace Familiar.Server.Conversation;

/// <param name="Text">What the user typed.</param>
/// <param name="TimeZone">The user's IANA time zone, for the situation in the prompt.</param>
/// <param name="Speak">Speak the reply as well as showing it. Spoken messages are always answered aloud.</param>
public sealed record SendMessageRequest(string? Text, string? TimeZone, bool? Speak = null);

/// <param name="ReplyId">The reply that was playing when the user interrupted.</param>
/// <param name="Heard">What of it they heard: the reply is cut to this.</param>
public sealed record InterruptRequest(Guid? ReplyId, string? Heard);

public static class ConversationEndpoints
{
    public const int MaxMessageLength = 4000;
    public const int MaxHistory = 200;

    /// <summary>A minute of Opus is ~100 KB; this allows several minutes of anything reasonable.</summary>
    public const int MaxUtteranceBytes = 10 * 1024 * 1024;

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
        group.MapPost("/audio", SendSpokenAsync).RequireRateLimiting(RateLimiting.Conversation);
        group.MapGet("/audio/{clipId:guid}", ClipAsync);
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
        ISpeechToText stt,
        ITextToSpeech tts,
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

            // Checked together, so the slowest of three sidecars sets the wait rather than their sum.
            var profile = store.ProfileAsync(accountId, ct);
            var model = llm.IsAvailableAsync(ct);
            var hearing = stt.IsAvailableAsync(ct);
            var voices = tts.VoicesAsync(ct);
            await Task.WhenAll(profile, model, hearing, voices);

            var ready = new ReadyEvent(
                new FamiliarSummary(profile.Result.Name, profile.Result.Expressiveness),
                model.Result,
                hearing.Result && voices.Result.Count > 0);
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
        SendMessageRequest request, HttpContext http, ConversationTurns turns, TimeProvider clock, CancellationToken ct)
    {
        var text = request.Text?.Trim() ?? string.Empty;
        if (text.Length is 0 or > MaxMessageLength)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, $"Messages are 1-{MaxMessageLength} characters.");
        }

        var turn = new TurnRequest(Zone(request.TimeZone), request.Speak ?? false, clock.GetTimestamp());
        var message = await turns.SubmitAsync(http.User.AccountId()!.Value, text, turn, ct);
        return TypedResults.Accepted((string?)null, MessageView.From(message));
    }

    /// <summary>
    /// A spoken message: the recorded utterance as the raw request body (Opus/WebM from browsers,
    /// AAC/M4A or WAV from phones), the user's time zone in the query string (PLAN.md §3.2).
    /// </summary>
    private static async Task<IResult> SendSpokenAsync(HttpContext http, ConversationTurns turns, string? timeZone, CancellationToken ct)
    {
        var contentType = http.Request.ContentType ?? string.Empty;
        if (!contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            return AuthEndpoints.Error(StatusCodes.Status415UnsupportedMediaType, "Send the recording as an audio/* body.");
        }

        if (http.Request.ContentLength > MaxUtteranceBytes)
        {
            return AuthEndpoints.Error(StatusCodes.Status413PayloadTooLarge, "That recording is too long.");
        }

        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, ct);
        if (buffer.Length is 0 or > MaxUtteranceBytes)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "That recording was empty or too long.");
        }

        try
        {
            var message = await turns.SubmitSpokenAsync(http.User.AccountId()!.Value, buffer.ToArray(), contentType, Zone(timeZone), ct);
            return message is null
                ? TypedResults.NoContent()
                : TypedResults.Accepted((string?)null, MessageView.From(message));
        }
        catch (SpeechUnavailableException)
        {
            return AuthEndpoints.Error(StatusCodes.Status503ServiceUnavailable, "I can't hear right now - speech recognition isn't reachable. You can still type.");
        }
    }

    private static IResult ClipAsync(Guid clipId, HttpContext http, SpeechClipStore clips) =>
        clips.Get(http.User.AccountId()!.Value, clipId) is { } clip
            ? TypedResults.File(clip.Audio, clip.ContentType)
            : TypedResults.NotFound();

    /// <summary>
    /// Stop. Optionally with what was heard of the reply that was playing, so it's stored as the user
    /// experienced it (hands-free barge-in, PLAN.md §3.2). The body is optional.
    /// </summary>
    private static async Task<IResult> InterruptAsync(InterruptRequest? request, HttpContext http, ConversationTurns turns)
    {
        var heard = request?.Heard is { Length: <= MaxMessageLength } text ? text : null;
        await turns.InterruptAsync(http.User.AccountId()!.Value, request?.ReplyId, heard);
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
