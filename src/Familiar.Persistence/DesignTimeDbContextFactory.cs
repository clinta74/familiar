using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Familiar.Persistence;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the model without starting the server. The connection
/// string is never opened for that command; it only has to parse.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<FamiliarDbContext>
{
    public FamiliarDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("FAMILIAR_DESIGN_CONNECTION")
            ?? "Host=localhost;Port=5433;Database=familiar;Username=familiar;Password=familiar_dev_only";

        var options = new DbContextOptionsBuilder<FamiliarDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new FamiliarDbContext(options);
    }
}
