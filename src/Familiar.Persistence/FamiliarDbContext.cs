using Familiar.Domain.Accounts;
using Familiar.Domain.Conversations;
using Familiar.Domain.Familiars;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Familiar.Persistence;

public sealed class FamiliarDbContext(DbContextOptions<FamiliarDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<AccessToken> AccessTokens => Set<AccessToken>();

    public DbSet<FamiliarProfile> FamiliarProfiles => Set<FamiliarProfile>();

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<Message> Messages => Set<Message>();

    /// <summary>
    /// ASP.NET Data Protection's key ring. Kept in the database rather than a container path, so
    /// recreating the server container doesn't invalidate every session cookie.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("citext");

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FamiliarDbContext).Assembly);

        // Last, so it sees every table, column, key and index the configurations declared.
        SnakeCaseNaming.ApplyTo(modelBuilder);
    }
}
