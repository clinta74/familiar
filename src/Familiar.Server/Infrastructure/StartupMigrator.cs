using System.Diagnostics;
using Familiar.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Familiar.Server.Infrastructure;

/// <summary>
/// Applies pending migrations at startup, in every environment.
/// </summary>
/// <remarks>
/// <para>
/// Explicit migrations, never <c>EnsureCreated</c>. There is one server instance, and EF's
/// migration lock serialises an accidental second. Rolling back means deploying the previous image.
/// </para>
/// <para>
/// Retries only transient connection failures, for a bounded time, because on the NAS the server
/// and Postgres start together and the database is often a few seconds behind. A wrong password or
/// a broken migration fails immediately: no amount of waiting fixes those.
/// </para>
/// </remarks>
public static class StartupMigrator
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(10);

    public static async Task MigrateAsync(IServiceProvider services, TimeSpan budget, ILogger logger, CancellationToken ct)
    {
        var clock = services.GetRequiredService<TimeProvider>();
        var factory = services.GetRequiredService<IDbContextFactory<FamiliarDbContext>>();
        var deadline = clock.GetUtcNow() + budget;
        var delay = FirstDelay;
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                var pending = (await db.Database.GetPendingMigrationsAsync(ct)).Count();
                await db.Database.MigrateAsync(ct);
                logger.Migrated(pending, stopwatch.ElapsedMilliseconds);
                return;
            }
            catch (Exception exception) when (IsTransient(exception) && clock.GetUtcNow() + delay <= deadline)
            {
                logger.MigrationRetry((long)delay.TotalMilliseconds, exception.GetBaseException().Message);
                await Task.Delay(delay, clock, ct);
                delay = TimeSpan.FromTicks(Math.Min(MaxDelay.Ticks, delay.Ticks * 2));
            }
        }
    }

    private static bool IsTransient(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException { IsTransient: true })
            {
                return true;
            }
        }

        return false;
    }
}
