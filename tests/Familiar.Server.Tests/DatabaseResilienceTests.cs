using System.Net;
using System.Net.Http.Json;
using Familiar.Server.Familiars;
using Familiar.Server.Tests.Infrastructure;
using Npgsql;

namespace Familiar.Server.Tests;

/// <summary>
/// Postgres restarting under a running server (a container recreated, the NAS updating) leaves the
/// connection pool full of dead connections. The next request on each one used to fail with 57P01
/// ("terminating connection due to administrator command") and a 500, until the pool turned over.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DatabaseResilienceTests(PostgresFixture postgres)
{
    /// <summary>What a Postgres restart does to every connection the server holds.</summary>
    private static async Task DropServerConnectionsAsync(string connectionString)
    {
        await using var admin = new NpgsqlConnection(connectionString);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid()",
            admin);
        Assert.True((long)(await command.ExecuteScalarAsync())! > 0, "the server should have had pooled connections to drop");
    }

    [Fact]
    public async Task Requests_ride_out_connections_dropped_by_a_database_restart()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var app = new FamiliarAppFactory(database);
        var client = app.CreateClient();
        (await client.RegisterAsync("survivor")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/familiar")).StatusCode);

        await DropServerConnectionsAsync(database);

        // Authenticated (the session is revalidated against the database on every request in
        // tests), read, and written - each on a pooled connection that's now dead.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/familiar")).StatusCode);
        var familiar = await (await client.GetAsync("/api/familiar")).ReadAsync<FamiliarView>();

        await DropServerConnectionsAsync(database);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/familiar", familiar with { Name = "Phoenix" })).StatusCode);
        Assert.Equal("Phoenix", (await (await client.GetAsync("/api/familiar")).ReadAsync<FamiliarView>()).Name);
    }

    [Fact]
    public async Task Registration_rides_it_out_too()
    {
        // Registration runs in a transaction (it takes an advisory lock), which a retrying execution
        // strategy only allows when the whole transaction is retried as a unit.
        var database = await postgres.CreateDatabaseAsync();
        await using var app = new FamiliarAppFactory(database);
        (await app.CreateClient().RegisterAsync("first")).EnsureSuccessStatusCode();

        await DropServerConnectionsAsync(database);

        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().RegisterAsync("second")).StatusCode);
    }
}
