using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Familiar.Persistence;

/// <summary>How hard a request tries again when the database fails it transiently.</summary>
/// <param name="MaxRetryCount">Attempts after the first.</param>
/// <param name="MaxRetryDelay">The longest wait between attempts.</param>
public sealed record DatabaseRetry(int MaxRetryCount, TimeSpan MaxRetryDelay)
{
    /// <summary>
    /// Short, because someone is usually waiting on the other end. Enough to get past a pool full of
    /// connections a Postgres restart has killed - the first attempt fails, Npgsql drops the dead
    /// connection, the retry opens a live one - and a database that's gone for a few seconds.
    /// </summary>
    public static readonly DatabaseRetry Default = new(3, TimeSpan.FromSeconds(2));
}

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers a pooled <see cref="IDbContextFactory{TContext}"/> (for singletons and auth
    /// handlers) and a scoped <see cref="FamiliarDbContext"/> (for endpoints) over one data source.
    /// </summary>
    /// <remarks>
    /// Every context retries transient failures, as Npgsql judges them: 57P01 (the server shutting
    /// a connection down, as a restart does), broken connections, serialization failures and the
    /// like. One consequence of EF's retrying strategy: a transaction opened by hand must run inside
    /// <c>Database.CreateExecutionStrategy()</c>, so that the whole transaction is what's retried -
    /// EF refuses it otherwise.
    /// </remarks>
    public static IServiceCollection AddFamiliarPersistence(this IServiceCollection services, string connectionString, DatabaseRetry? retry = null)
    {
        retry ??= DatabaseRetry.Default;

        var builder = new NpgsqlDataSourceBuilder(connectionString).EnableDynamicJson();
        builder.UseVector();
        var dataSource = builder.Build();

        services.AddSingleton(dataSource);
        services.AddPooledDbContextFactory<FamiliarDbContext>(options => options.UseNpgsql(dataSource, npgsql =>
        {
            npgsql.UseVector();
            npgsql.EnableRetryOnFailure(retry.MaxRetryCount, retry.MaxRetryDelay, errorCodesToAdd: null);
        }));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<FamiliarDbContext>>().CreateDbContext());

        return services;
    }
}
