using Familiar.Domain.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Familiar.Persistence.Configurations;

internal sealed class AccessTokenConfiguration : IEntityTypeConfiguration<AccessToken>
{
    public void Configure(EntityTypeBuilder<AccessToken> builder)
    {
        builder.ToTable("AccessTokens");

        // The id is part of the token string, so the server mints it alongside the secret.
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.Name).HasMaxLength(64).IsRequired();
        builder.Property(t => t.SecretHash).HasMaxLength(64).IsRequired();
        builder.Property(t => t.SecurityStamp).HasMaxLength(64).IsRequired();

        builder.HasIndex(t => new { t.AccountId, t.CreatedAt }).IsDescending(false, true);
    }
}
