using Familiar.Domain.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Familiar.Persistence.Configurations;

internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("Accounts");

        // Minted in .NET (Guid.CreateVersion7); the database default covers hand-written inserts.
        builder.Property(a => a.Id).HasDefaultValueSql("uuidv7()");

        builder.Property(a => a.Username).HasColumnType("citext").HasMaxLength(32).IsRequired();
        builder.HasIndex(a => a.Username).IsUnique();

        builder.Property(a => a.Email).HasColumnType("citext").HasMaxLength(320).IsRequired();
        builder.HasIndex(a => a.Email).IsUnique();

        builder.Property(a => a.PasswordHash).HasMaxLength(256).IsRequired();
        builder.Property(a => a.SecurityStamp).HasMaxLength(64).IsRequired();

        builder.Property(a => a.Role).HasConversion<string>().HasMaxLength(16).IsRequired();

        builder.HasMany(a => a.AccessTokens)
            .WithOne(t => t.Account)
            .HasForeignKey(t => t.AccountId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
