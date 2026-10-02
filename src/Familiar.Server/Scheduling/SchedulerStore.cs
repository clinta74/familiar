using Familiar.Domain.Scheduling;
using Familiar.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Familiar.Server.Scheduling;

/// <summary>Timers and reminders in Postgres (PLAN.md §6.2).</summary>
public sealed class SchedulerStore(IDbContextFactory<FamiliarDbContext> factory)
{
    public async Task<ScheduledJob> AddAsync(ScheduledJob job, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.ScheduledJobs.Add(job);
        await db.SaveChangesAsync(ct);
        return job;
    }

    /// <summary>The account's pending jobs, soonest first.</summary>
    public async Task<IReadOnlyList<ScheduledJob>> PendingAsync(Guid accountId, JobKind? kind, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ScheduledJobs.AsNoTracking()
            .Where(j => j.AccountId == accountId && j.Status == JobStatus.Pending && (kind == null || j.Kind == kind))
            .OrderBy(j => j.DueAt)
            .ToListAsync(ct);
    }

    public async Task<bool> CancelAsync(Guid accountId, Guid jobId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var changed = await db.ScheduledJobs
            .Where(j => j.Id == jobId && j.AccountId == accountId && j.Status == JobStatus.Pending)
            .ExecuteUpdateAsync(j => j.SetProperty(x => x.Status, JobStatus.Cancelled), ct);
        return changed > 0;
    }

    /// <summary>
    /// Atomically takes every due job, marking it fired. <c>FOR UPDATE SKIP LOCKED</c>, so even two
    /// server instances could never fire the same job twice.
    /// </summary>
    public async Task<IReadOnlyList<ScheduledJob>> ClaimDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ScheduledJobs
            .FromSql($"""
                UPDATE scheduled_jobs SET status = 'Fired', fired_at = {now}
                WHERE id IN (
                    SELECT id FROM scheduled_jobs
                    WHERE status = 'Pending' AND due_at <= {now}
                    ORDER BY due_at
                    LIMIT 50
                    FOR UPDATE SKIP LOCKED)
                RETURNING *
                """)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    /// <summary>When the next pending job is due, across all accounts; null if none.</summary>
    public async Task<DateTimeOffset?> NextDueAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ScheduledJobs
            .Where(j => j.Status == JobStatus.Pending)
            .OrderBy(j => j.DueAt)
            .Select(j => (DateTimeOffset?)j.DueAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Jobs that fired since a moment - what a service worker asks for when a push arrives.</summary>
    public async Task<IReadOnlyList<ScheduledJob>> FiredSinceAsync(Guid accountId, DateTimeOffset since, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ScheduledJobs.AsNoTracking()
            .Where(j => j.AccountId == accountId && j.Status == JobStatus.Fired && j.FiredAt >= since)
            .OrderBy(j => j.FiredAt)
            .ToListAsync(ct);
    }
}

/// <summary>
/// Wakes the scheduler early: it sleeps until the next due job, so a new 30-second timer must be able
/// to cut a 10-minute sleep short.
/// </summary>
public sealed class SchedulerSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Poke()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already poked.
        }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}
