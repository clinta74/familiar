using Familiar.Domain.Accounts;
using Familiar.Domain.Notifications;
using Familiar.Domain.Scheduling;
using Familiar.Domain.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Familiar.Persistence.Configurations;

internal sealed class ScheduledJobConfiguration : IEntityTypeConfiguration<ScheduledJob>
{
    public void Configure(EntityTypeBuilder<ScheduledJob> builder)
    {
        builder.ToTable("ScheduledJobs");
        builder.Property(j => j.Id).HasDefaultValueSql("uuidv7()");
        builder.HasOne<Account>().WithMany().HasForeignKey(j => j.AccountId).OnDelete(DeleteBehavior.Cascade);

        builder.Property(j => j.Kind).HasConversion<string>().HasMaxLength(16);
        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(j => j.Label).HasMaxLength(ScheduledJob.MaxLabelLength).IsRequired();
        builder.Property(j => j.TimeZone).HasMaxLength(64);

        // The scheduler's question is always "what's pending, soonest first".
        builder.HasIndex(j => new { j.Status, j.DueAt });
        builder.HasIndex(j => new { j.AccountId, j.Status });
    }
}

internal sealed class ToolInvocationConfiguration : IEntityTypeConfiguration<ToolInvocation>
{
    public void Configure(EntityTypeBuilder<ToolInvocation> builder)
    {
        builder.ToTable("ToolInvocations");
        builder.Property(t => t.Id).HasDefaultValueSql("uuidv7()");
        builder.HasOne<Account>().WithMany().HasForeignKey(t => t.AccountId).OnDelete(DeleteBehavior.Cascade);

        builder.Property(t => t.Tool).HasMaxLength(64).IsRequired();
        builder.Property(t => t.Risk).HasConversion<string>().HasMaxLength(16);
        builder.Property(t => t.Outcome).HasConversion<string>().HasMaxLength(16);
        builder.Property(t => t.Arguments).HasColumnType("jsonb").IsRequired();
        builder.Property(t => t.Result).HasColumnType("jsonb");

        builder.HasIndex(t => new { t.AccountId, t.CreatedAt }).IsDescending(false, true);
    }
}

internal sealed class PushSubscriptionConfiguration : IEntityTypeConfiguration<PushSubscription>
{
    public void Configure(EntityTypeBuilder<PushSubscription> builder)
    {
        builder.ToTable("PushSubscriptions");
        builder.Property(p => p.Id).HasDefaultValueSql("uuidv7()");
        builder.HasOne<Account>().WithMany().HasForeignKey(p => p.AccountId).OnDelete(DeleteBehavior.Cascade);

        builder.Property(p => p.Endpoint).HasMaxLength(1024).IsRequired();
        builder.HasIndex(p => p.Endpoint).IsUnique();
        builder.Property(p => p.P256dh).HasMaxLength(256);
        builder.Property(p => p.Auth).HasMaxLength(64);
        builder.HasOne<AccessToken>().WithMany().HasForeignKey(p => p.DeviceId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Server-wide secrets the server generates for itself on first need (the VAPID key pair).</summary>
public sealed class ServerSecret
{
    public required string Name { get; init; }

    public required string Value { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class ServerSecretConfiguration : IEntityTypeConfiguration<ServerSecret>
{
    public void Configure(EntityTypeBuilder<ServerSecret> builder)
    {
        builder.ToTable("ServerSecrets");
        builder.HasKey(s => s.Name);
        builder.Property(s => s.Name).HasMaxLength(64);
        builder.Property(s => s.Value).IsRequired();
    }
}
