using Familiar.Domain.Calendars;
using Familiar.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Familiar.Server.Calendars;

/// <summary>Calendars and their events (PLAN.md §6.4).</summary>
public sealed class CalendarStore(IDbContextFactory<FamiliarDbContext> db, IDataProtectionProvider protection, TimeProvider clock)
{
    private readonly IDataProtector _urls = protection.CreateProtector("Familiar.Calendars.FeedUrl");

    public async Task<IReadOnlyList<CalendarSource>> SourcesAsync(Guid accountId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        return await context.CalendarSources.AsNoTracking()
            .Where(s => s.AccountId == accountId)
            .OrderBy(s => s.Kind).ThenBy(s => s.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>Familiar's own calendar, created the first time it's needed.</summary>
    public async Task<CalendarSource> LocalAsync(Guid accountId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var local = await context.CalendarSources.FirstOrDefaultAsync(s => s.AccountId == accountId && s.Kind == CalendarKind.Local, ct);
        if (local is not null)
        {
            return local;
        }

        local = new CalendarSource { AccountId = accountId, Kind = CalendarKind.Local, Name = "Familiar", CreatedAt = clock.GetUtcNow() };
        context.CalendarSources.Add(local);
        await context.SaveChangesAsync(ct);
        return local;
    }

    public async Task<CalendarSource> AddFeedAsync(Guid accountId, string name, Uri url, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var source = new CalendarSource
        {
            AccountId = accountId,
            Kind = CalendarKind.Ics,
            Name = name,
            ProtectedUrl = _urls.Protect(url.AbsoluteUri),
            CreatedAt = clock.GetUtcNow(),
        };
        context.CalendarSources.Add(source);
        await context.SaveChangesAsync(ct);
        return source;
    }

    public Uri FeedUrl(CalendarSource source) => new(_urls.Unprotect(source.ProtectedUrl!));

    /// <summary>Removes a subscribed feed and its events. Familiar's own calendar can't be removed.</summary>
    public async Task<bool> RemoveAsync(Guid accountId, Guid sourceId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        return await context.CalendarSources
            .Where(s => s.Id == sourceId && s.AccountId == accountId && s.Kind != CalendarKind.Local)
            .ExecuteDeleteAsync(ct) > 0;
    }

    /// <summary>Events overlapping a window, soonest first.</summary>
    /// <summary>
    /// A phone's calendars older than this are left out: it's been signed out, or not opened in days,
    /// and what it last said may well be wrong by now.
    /// </summary>
    public static readonly TimeSpan DeviceCalendarStale = TimeSpan.FromDays(3);

    public async Task<IReadOnlyList<CalendarEvent>> BetweenAsync(Guid accountId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var fresh = clock.GetUtcNow() - DeviceCalendarStale;
        var stale = context.CalendarSources
            .Where(s => s.AccountId == accountId && s.Kind == CalendarKind.Device && (s.LastSyncedAt == null || s.LastSyncedAt < fresh))
            .Select(s => s.Id);
        return await context.CalendarEvents.AsNoTracking()
            .Where(e => e.AccountId == accountId && e.StartsAt < to && e.EndsAt > from && !stale.Contains(e.SourceId))
            .OrderBy(e => e.StartsAt).ThenBy(e => e.Title)
            .Take(200)
            .ToListAsync(ct);
    }

    public async Task<CalendarEvent> AddLocalEventAsync(
        Guid accountId, string title, DateTimeOffset startsAt, DateTimeOffset endsAt, bool allDay, CancellationToken ct)
    {
        var local = await LocalAsync(accountId, ct);
        await using var context = await db.CreateDbContextAsync(ct);
        var calendarEvent = new CalendarEvent
        {
            SourceId = local.Id,
            AccountId = accountId,
            Title = title,
            StartsAt = startsAt,
            EndsAt = endsAt,
            AllDay = allDay,
            CreatedAt = clock.GetUtcNow(),
        };
        context.CalendarEvents.Add(calendarEvent);
        await context.SaveChangesAsync(ct);
        return calendarEvent;
    }

    /// <summary>Deletes one of Familiar's own events; a feed's events belong to the feed.</summary>
    public async Task<bool> DeleteLocalEventAsync(Guid accountId, Guid eventId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var local = await context.CalendarSources.AsNoTracking()
            .Where(s => s.AccountId == accountId && s.Kind == CalendarKind.Local)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);
        return local is not null && await context.CalendarEvents
            .Where(e => e.Id == eventId && e.SourceId == local)
            .ExecuteDeleteAsync(ct) > 0;
    }

    /// <summary>
    /// A phone's calendars, replaced with what it uploaded: one source per phone, created the first
    /// time, its events swapped wholesale (the phone sends its whole window each time).
    /// </summary>
    public async Task<CalendarSource> ReplaceDeviceEventsAsync(
        Guid accountId, Guid deviceId, string deviceName, IReadOnlyList<FeedOccurrence> events, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var source = await context.CalendarSources.AsNoTracking().FirstOrDefaultAsync(s => s.DeviceId == deviceId, ct);
        if (source is null)
        {
            source = new CalendarSource
            {
                AccountId = accountId,
                Kind = CalendarKind.Device,
                Name = deviceName.Length <= CalendarSource.MaxNameLength ? deviceName : deviceName[..CalendarSource.MaxNameLength],
                DeviceId = deviceId,
                CreatedAt = clock.GetUtcNow(),
            };
            context.CalendarSources.Add(source);
            await context.SaveChangesAsync(ct);
        }

        await ReplaceFeedEventsAsync(source, events, ct);
        return source;
    }

    /// <summary>A signed-out phone's calendars go with it.</summary>
    public async Task RemoveDeviceAsync(Guid deviceId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await context.CalendarSources.Where(s => s.DeviceId == deviceId).ExecuteDeleteAsync(ct);
    }

    /// <summary>Feeds not synced since <paramref name="before"/>.</summary>
    public async Task<IReadOnlyList<CalendarSource>> FeedsDueAsync(DateTimeOffset before, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        return await context.CalendarSources.AsNoTracking()
            .Where(s => s.Kind == CalendarKind.Ics && (s.LastSyncedAt == null || s.LastSyncedAt < before))
            .OrderBy(s => s.LastSyncedAt)
            .Take(20)
            .ToListAsync(ct);
    }

    /// <summary>A feed's events, replaced wholesale with what it says now.</summary>
    public async Task ReplaceFeedEventsAsync(CalendarSource source, IReadOnlyList<FeedOccurrence> occurrences, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);

        // Retried as a whole (the execution strategy requires it): delete, insert and mark together.
        await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(ct);

            await context.CalendarEvents.Where(e => e.SourceId == source.Id).ExecuteDeleteAsync(ct);
            var now = clock.GetUtcNow();
            context.CalendarEvents.AddRange(occurrences.Select(o => new CalendarEvent
            {
                SourceId = source.Id,
                AccountId = source.AccountId,
                ExternalId = o.ExternalId.Length <= 512 ? o.ExternalId : o.ExternalId[..512],
                Title = o.Title,
                Location = o.Location,
                StartsAt = o.StartsAt,
                EndsAt = o.EndsAt,
                AllDay = o.AllDay,
                CreatedAt = now,
            }));
            await context.SaveChangesAsync(ct);

            await context.CalendarSources
                .Where(s => s.Id == source.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSyncedAt, now).SetProperty(x => x.LastError, (string?)null), ct);
            await transaction.CommitAsync(ct);
        });
    }

    /// <summary>
    /// Records a failed sync. LastSyncedAt moves too, so a broken feed is retried on the normal
    /// schedule rather than every pass; its old events are kept meanwhile.
    /// </summary>
    public async Task MarkFailedAsync(Guid sourceId, string error, CancellationToken ct)
    {
        error = error.Length <= 500 ? error : error[..500];
        var now = clock.GetUtcNow();
        await using var context = await db.CreateDbContextAsync(ct);
        await context.CalendarSources
            .Where(s => s.Id == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSyncedAt, now).SetProperty(x => x.LastError, error), ct);
    }
}
