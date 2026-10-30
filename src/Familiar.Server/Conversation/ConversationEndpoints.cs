using Familiar.Agent.Llm;
using Familiar.Agent.Speech;
using Familiar.Server.Auth;
using Familiar.Server.Devices;
using Familiar.Server.Infrastructure;
using Familiar.Server.Scheduling;
using Familiar.Server.Tools;
using Microsoft.AspNetCore.Http.Features;

namespace Familiar.Server.Conversation;

/// <param name="Text">What the user typed.</param>
/// <param name="TimeZone">The user's IANA time zone, for the situation in the prompt.</param>
/// <param name="Speak">Speak the reply as well as showing it. Spoken messages are always answered aloud.</param>
/// <param name="ImageId">A picture uploaded to /api/conversation/images to send with it (BACKLOG item 35); the text may then be empty.</param>
public sealed record SendMessageRequest(string? Text, string? TimeZone, bool? Speak = null, Guid? ImageId = null);

/// <param name="ReplyId">The reply that was playing when the user interrupted.</param>
/// <param name="Heard">What of it they heard: the reply is cut to this.</param>
public sealed record InterruptRequest(Guid? ReplyId, string? Heard);

/// <param name="CallId">The tool call being answered, from the <c>tool.confirm</c> event.</param>
/// <param name="Approve">Whether the user allows it.</param>
public sealed record ConfirmRequest(string? CallId, bool Approve);

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
        group.MapPost("/images", UploadImageAsync).RequireRateLimiting(RateLimiting.Conversation);
        group.MapGet("/images/{imageId:guid}", ImageAsync);
        group.MapPost("/interrupt", InterruptAsync);
        group.MapPost("/new", StartOverAsync);
        group.MapPost("/tool-result", (ToolResultRequest request, HttpContext http, DeviceTools devices) =>
            devices.Answer(http.User.AccountId()!.Value, request) ? Results.NoContent() : Results.NotFound());
        group.MapPost("/confirm", (ConfirmRequest request, HttpContext http, ToolConfirmations confirmations) =>
            request.CallId is { } callId && confirmations.Answer(http.User.AccountId()!.Value, callId, request.Approve)
                ? Results.NoContent()
                : Results.NotFound());
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
        Scheduler scheduler,
        ConversationTurns turns,
        DeviceTools devices,
        string? timeZone,
        bool? speak,
        string? capabilities,
        bool? back,
        CancellationToken ct)
    {
        var accountId = http.User.AccountId()!.Value;

        // A phone says what it can do for the familiar while it's connected (PLAN.md §6.1); a browser
        // may offer where it is, and nothing else (BACKLOG item 24).
        DeviceSession? device = null;
        IDisposable? registration = null;
        var offered = (capabilities ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (http.User.DeviceId() is { } deviceId)
        {
            (device, registration) = devices.Connect(accountId, deviceId, offered);
        }
        else if (offered.Any(DeviceCapabilities.Browser.Contains))
        {
            (device, registration) = devices.Connect(accountId, null, offered);
        }

        using var connected = registration;

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
                hearing.Result && voices.Result.Count > 0,
                device?.ConnectionId);

            // The timers and reminders as they are now (changes follow as events), then ready: once
            // a client has ready, it has everything.
            var jobs = await scheduler.PendingAsync(accountId, null, ct);
            await WriteAsync(http, ConversationHub.Frame(EventTypes.Jobs, new JobsEvent([.. jobs.Select(JobView.From)])), ct);
            await WriteAsync(http, ConversationHub.Frame(EventTypes.Ready, ready), ct);

            // Coming back after a while: the familiar may say hello first. A fresh connection, or the
            // phone app back in front (`back`) - it stays running in the background now (push), so it
            // comes back on a reconnect, not a fresh one; any other reconnect is the same visit. The
            // greeting itself still waits for a quiet spell (GreetingGap). Its events arrive on this
            // stream like any other.
            if ((string.IsNullOrEmpty(lastEventId) || back == true) && model.Result)
            {
                _ = turns.GreetAsync(accountId, Zone(timeZone), speak ?? false, CancellationToken.None);
            }

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

    /// <summary>Where a request came from: the phone's device id, or a browser.</summary>
    private static string From(HttpContext http) => http.User.DeviceId()?.ToString() ?? DeviceCapabilities.FromBrowser;

    private static async Task<IResult> SendAsync(
        SendMessageRequest request, HttpContext http, ConversationTurns turns, ConversationStore store, TimeProvider clock, CancellationToken ct)
    {
        var text = request.Text?.Trim() ?? string.Empty;
        if ((text.Length == 0 && request.ImageId is null) || text.Length > MaxMessageLength)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, $"Messages are 1-{MaxMessageLength} characters.");
        }

        var accountId = http.User.AccountId()!.Value;
        if (request.ImageId is { } imageId && !await store.OwnsUnsentImageAsync(accountId, imageId, ct))
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "That picture isn't there any more. Add it again.");
        }

        var turn = new TurnRequest(Zone(request.TimeZone), request.Speak ?? false, clock.GetTimestamp(), From: From(http));
        var message = await turns.SubmitAsync(accountId, text, turn, ct, request.ImageId);
        return TypedResults.Accepted((string?)null, MessageView.From(message));
    }

    /// <summary>
    /// A picture to send (BACKLOG item 35), as the raw image/jpeg body: checked, its metadata stripped,
    /// and kept until a message names it - a day at most.
    /// </summary>
    private static async Task<IResult> UploadImageAsync(HttpContext http, ConversationStore store, CancellationToken ct)
    {
        if (!string.Equals(http.Request.ContentType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return AuthEndpoints.Error(StatusCodes.Status415UnsupportedMediaType, "Send the picture as an image/jpeg body.");
        }

        if (http.Request.ContentLength > Domain.Conversations.MessageImage.MaxBytes)
        {
            return AuthEndpoints.Error(StatusCodes.Status413PayloadTooLarge, "That picture is too big.");
        }

        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, ct);
        if (buffer.Length > Domain.Conversations.MessageImage.MaxBytes)
        {
            return AuthEndpoints.Error(StatusCodes.Status413PayloadTooLarge, "That picture is too big.");
        }

        try
        {
            var (bytes, width, height) = ChatImages.Clean(buffer.ToArray());
            var image = await store.AddImageAsync(http.User.AccountId()!.Value, bytes, width, height, ct);
            return TypedResults.Ok(new ImageView(image.Id, image.Width, image.Height));
        }
        catch (ChatImageException exception)
        {
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, exception.Message);
        }
    }

    /// <summary>A picture from the chat, to its owner only.</summary>
    private static async Task<IResult> ImageAsync(Guid imageId, HttpContext http, ConversationStore store, CancellationToken ct)
    {
        if (await store.ImageAsync(http.User.AccountId()!.Value, imageId, ct) is not { } bytes)
        {
            return Results.NotFound();
        }

        http.Response.Headers.CacheControl = "private, max-age=604800";
        return Results.File(bytes, "image/jpeg");
    }

    /// <summary>
    /// A spoken message: the recorded utterance as the raw request body (Opus/WebM from browsers,
    /// AAC/M4A or WAV from phones), the user's time zone in the query string (PLAN.md §3.2).
    /// </summary>
    private static async Task<IResult> SendSpokenAsync(HttpContext http, ConversationTurns turns, string? timeZone, bool? overReply, CancellationToken ct)
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
            var message = await turns.SubmitSpokenAsync(http.User.AccountId()!.Value, buffer.ToArray(), contentType, Zone(timeZone), ct, From(http), overReply);
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
