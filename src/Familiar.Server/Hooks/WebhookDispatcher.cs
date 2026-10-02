using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Familiar.Domain.Hooks;
using Familiar.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Familiar.Server.Hooks;

/// <summary>What a webhook can be told about (PLAN.md §6.5).</summary>
public static class WebhookEvents
{
    public const string TimerDone = "timer.done";
    public const string ReminderDue = "reminder.due";
    public const string Reply = "reply";
    public const string ToolUsed = "tool.used";
    public const string Test = "test";

    public static readonly IReadOnlyList<string> All = [TimerDone, ReminderDue, Reply, ToolUsed];
}

/// <summary>The JSON a webhook receives.</summary>
/// <param name="Event">Which event.</param>
/// <param name="At">When it happened.</param>
/// <param name="Data">What happened; depends on the event.</param>
public sealed record WebhookPayload(string Event, DateTimeOffset At, object Data);

/// <summary>
/// Sends events to the user's webhooks: queued, so nothing that raises an event waits on someone
/// else's server, and retried twice. Each POST is signed: <c>X-Familiar-Signature: sha256=&lt;hex
/// HMAC of the body&gt;</c> with the webhook's secret, so the receiver can tell it's genuine.
/// </summary>
public sealed class WebhookDispatcher(
    IDbContextFactory<FamiliarDbContext> db,
    IDataProtectionProvider protection,
    IHttpClientFactory httpFactory,
    TimeProvider clock,
    ILogger<WebhookDispatcher> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan[] Retries = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)];

    private readonly Channel<(Guid AccountId, string Event, object Data, DateTimeOffset At)> _queue =
        Channel.CreateBounded<(Guid, string, object, DateTimeOffset)>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });

    internal IDataProtector Protector { get; } = protection.CreateProtector("Familiar.Hooks.Webhook");

    /// <summary>Queues an event for whichever of the account's webhooks want it. Never blocks, never throws.</summary>
    public void Raise(Guid accountId, string eventName, object data) =>
        _queue.Writer.TryWrite((accountId, eventName, data, clock.GetUtcNow()));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (accountId, eventName, data, at) in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                List<Webhook> hooks;
                await using (var context = await db.CreateDbContextAsync(stoppingToken))
                {
                    hooks = await context.Webhooks.AsNoTracking()
                        .Where(w => w.AccountId == accountId && w.Enabled && w.Events.Contains(eventName))
                        .ToListAsync(stoppingToken);
                }

                foreach (var hook in hooks)
                {
                    // Each webhook on its own: one slow receiver doesn't hold up the rest.
                    _ = DeliverWithRetriesAsync(hook, new WebhookPayload(eventName, at, data), stoppingToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.WebhookFailed(exception, eventName);
            }
        }
    }

    private async Task DeliverWithRetriesAsync(Webhook hook, WebhookPayload payload, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await DeliverAsync(hook, payload, ct);
            if (result.Ok || attempt >= Retries.Length)
            {
                return;
            }

            await Task.Delay(Retries[attempt], clock, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>One POST, its outcome recorded on the webhook. Used directly by "Send a test".</summary>
    public async Task<(bool Ok, string Result)> DeliverAsync(Webhook hook, WebhookPayload payload, CancellationToken ct)
    {
        string result;
        var ok = false;
        try
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
            var secret = Protector.Unprotect(hook.ProtectedSecret);
            var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

            using var request = new HttpRequestMessage(HttpMethod.Post, Protector.Unprotect(hook.ProtectedUrl))
            {
                Content = new ByteArrayContent(body),
            };
            request.Content.Headers.ContentType = new("application/json");
            request.Headers.Add("X-Familiar-Event", payload.Event);
            request.Headers.Add("X-Familiar-Signature", $"sha256={signature}");

            using var response = await httpFactory.CreateClient(nameof(WebhookDispatcher)).SendAsync(request, ct);
            ok = response.IsSuccessStatusCode;
            result = ok ? $"{(int)response.StatusCode}" : $"{(int)response.StatusCode} {response.ReasonPhrase}";
        }
        catch (HttpRequestException exception)
        {
            result = $"Couldn't connect: {exception.Message}";
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            result = "No answer within 10 seconds.";
        }
        catch (CryptographicException)
        {
            result = "Its saved address can't be read any more (the server's keys changed). Delete it and add it again.";
        }

        result = result.Length <= 300 ? result : result[..300];
        await using var context = await db.CreateDbContextAsync(CancellationToken.None);
        await context.Webhooks.Where(w => w.Id == hook.Id).ExecuteUpdateAsync(w => w
            .SetProperty(x => x.LastDeliveredAt, clock.GetUtcNow())
            .SetProperty(x => x.LastResult, result), CancellationToken.None);

        if (!ok)
        {
            logger.WebhookRejected(hook.Id, payload.Event, result);
        }

        return (ok, result);
    }
}
