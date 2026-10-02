using Npgsql;
using Testcontainers.PostgreSql;

namespace Familiar.Server.Tests.Infrastructure;

/// <summary>
/// One real PostgreSQL 18 (+pgvector) container for the whole run, and a fresh database inside it
/// for every test - so citext, uuidv7() and the advisory lock are genuinely exercised, and "the
/// first account becomes admin" can be tested without tests seeing each other's accounts.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // The same image the compose files run.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg18").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"t_{Guid.NewGuid():N}";

        await using (var connection = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = name,

            // Each test builds its own host, and a disposed host's pool lingers briefly. Small,
            // promptly-pruned pools keep the overlap well under Postgres's connection limit.
            MaxPoolSize = 8,
            ConnectionIdleLifetime = 5,
            ConnectionPruningInterval = 1,
        }.ConnectionString;
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
