using Familiar.Domain.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Familiar.Persistence.Configurations;

internal sealed class InviteConfiguration : IEntityTypeConfiguration<Invite>
{
    public void Configure(EntityTypeBuilder<Invite> builder)
    {
        builder.ToTable("Invites");

        // The id is part of the code, so the server mints it alongside the secret.
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.SecretHash).HasMaxLength(64).IsRequired();
        builder.Property(i => i.Note).HasMaxLength(80);
        builder.Property(i => i.UsedByUsername).HasMaxLength(24);

        // An invite outlives the accounts on either end of it: its record says what happened.
        builder.HasOne<Account>().WithMany().HasForeignKey(i => i.CreatedBy).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<Account>().WithMany().HasForeignKey(i => i.UsedBy).OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(i => i.CreatedAt).IsDescending();
    }
}

/// <summary>
/// Server-wide settings an admin changes while it runs, by name: whether anyone may register
/// ("registration.open"), overriding the configuration's default.
/// </summary>
public sealed class ServerSetting
{
    public required string Name { get; init; }

    public required string Value { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class ServerSettingConfiguration : IEntityTypeConfiguration<ServerSetting>
{
    public void Configure(EntityTypeBuilder<ServerSetting> builder)
    {
        builder.ToTable("ServerSettings");
        builder.HasKey(s => s.Name);
        builder.Property(s => s.Name).HasMaxLength(64);
        builder.Property(s => s.Value).HasMaxLength(256).IsRequired();
    }
}
