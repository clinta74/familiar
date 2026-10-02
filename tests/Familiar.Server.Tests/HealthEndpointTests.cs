using System.Net;
using Familiar.Server.Tests.Infrastructure;

namespace Familiar.Server.Tests;

[Collection(PostgresCollection.Name)]
public sealed class HealthEndpointTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/health/ready")]
    [InlineData("/api/version")]
    public async Task Anonymous_operational_endpoints_answer(string path)
    {
        await using var app = new FamiliarAppFactory(await postgres.CreateDatabaseAsync());

        var response = await app.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
