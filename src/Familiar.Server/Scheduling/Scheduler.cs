using Familiar.Domain.Scheduling;
using Familiar.Server.Conversation;
using Familiar.Server.Notifications;

namespace Familiar.Server.Scheduling;

/// <summary>A pending timer or reminder, as the client shows it.</summary>
/// <param name="Id">The job.</param>
/// <param name="Kind">Timer or reminder.</param>
/// <param name="Label">What it's for.</param>
/// <param name="DueAt">When it goes off.</param>
/// <param name="DurationSeconds">For a timer, how long it was set for.</param>
/// <param name="CreatedAt">When it was set.</param>
/// <param name="Ring">It rings until dismissed (timers, and alarm reminders) rather than notifying.</param>
public sealed record JobView(Guid Id, JobKind Kind, string Label, DateTimeOffset DueAt, int? DurationSeconds, DateTimeOffset CreatedAt, bool Ring)
{
    public static JobView From(ScheduledJob job) =>
        new(job.Id, job.Kind, job.Label, job.DueAt, job.DurationSeconds, job.CreatedAt, job.Kind == JobKind.Timer || job.Ring);
}

/// <summary>The account's pending jobs, sent whenever they change, so every device's timer list stays current.</summary>
public sealed record JobsEvent(IReadOnlyList<JobView> Jobs);

/// <summary>
/// Timers and reminders: what the tools and the jobs endpoints use. Every change wakes the scheduler
/// and tells the account's devices.
/// </summary>
public sealed class Scheduler(SchedulerStore store, SchedulerSignal signal, ConversationHub hub, PushSender push)
{
    /// <summary>A sanity limit, not a feature: no one needs more running at once.</summary>
    public const int MaxPending = 50;

    public async Task<ScheduledJob?> AddAsync(ScheduledJob job, CancellationToken ct)
    {
        if ((await store.PendingAsync(job.AccountId, null, ct)).Count >= MaxPending)
        {
            return null;
        }

        await store.AddAsync(job, ct);
        signal.Poke();
        await PublishAsync(job.AccountId, ct);
        return job;
    }

    public async Task<bool> CancelAsync(Guid accountId, Guid jobId, CancellationToken ct)
    {
        var cancelled = await store.CancelAsync(accountId, jobId, ct);
        if (cancelled)
        {
            await PublishAsync(accountId, ct);
        }

        return cancelled;
    }

    public Task<IReadOnlyList<ScheduledJob>> PendingAsync(Guid accountId, JobKind? kind, CancellationToken ct) =>
        store.PendingAsync(accountId, kind, ct);

    public async Task PublishAsync(Guid accountId, CancellationToken ct)
    {
        var pending = await store.PendingAsync(accountId, null, ct);
        hub.For(accountId).Publish(EventTypes.Jobs, new JobsEvent([.. pending.Select(JobView.From)]));

        // Phones that aren't connected keep their own alarms for these (so they ring offline): tell
        // them to catch up. In the background - a slow push service mustn't hold up the tool.
        _ = Task.Run(() => push.NotifyAsync(accountId, CancellationToken.None, devicesOnly: true), CancellationToken.None);
    }
}

/// <summary>
/// Fires due jobs (PLAN.md §6.2). Sleeps until the next one is due - at most
/// <see cref="MaxSleep"/>, and less when a new job is added - then claims and announces whatever is
/// due. Jobs that came due while the server was down are found on the first pass and fire late.
/// </summary>
public sealed class SchedulerLoop(
    SchedulerStore store,
    SchedulerSignal signal,
    ConversationTurns turns,
    PushSender push,
    Hooks.WebhookDispatcher webhooks,
    TimeProvider clock,
    ILogger<SchedulerLoop> logger) : BackgroundService
{
    internal static readonly TimeSpan MaxSleep = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var job in await store.ClaimDueAsync(clock.GetUtcNow(), stoppingToken))
                {
                    await FireAsync(job, stoppingToken);
                }

                var next = await store.NextDueAsync(stoppingToken);
                var sleep = next is { } due ? due - clock.GetUtcNow() : MaxSleep;
                sleep = sleep < TimeSpan.Zero ? TimeSpan.Zero : sleep > MaxSleep ? MaxSleep : sleep;

                // A few milliseconds over, so the job is due when the loop wakes rather than just before.
                await signal.WaitAsync(sleep + TimeSpan.FromMilliseconds(20), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.SchedulerFailed(exception);
                await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private async Task FireAsync(ScheduledJob job, CancellationToken ct)
    {
        var text = Spoken.Announcement(job, clock.GetUtcNow());
        logger.JobFired(job.Kind, job.Id, (long)(clock.GetUtcNow() - job.DueAt).TotalSeconds);

        // Both, always: the push is how a closed tab or a locked phone hears about it, and the service
        // worker stays quiet when a familiar window is already showing it.
        try
        {
            await turns.AnnounceAsync(job, text, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.AnnounceFailed(exception, job.Id);
        }

        await push.NotifyAsync(job.AccountId, ct);

        webhooks.Raise(job.AccountId, job.Kind == JobKind.Timer ? Hooks.WebhookEvents.TimerDone : Hooks.WebhookEvents.ReminderDue, new
        {
            id = job.Id,
            label = job.Label,
            dueAt = job.DueAt,
            durationSeconds = job.DurationSeconds,
            text,
        });
    }
}
