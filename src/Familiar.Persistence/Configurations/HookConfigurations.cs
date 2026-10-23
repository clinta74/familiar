using Familiar.Domain.Accounts;
using Familiar.Domain.Hooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Familiar.Persistence.Configurations;

internal sealed class HookTokenConfiguration : IEntityTypeConfiguration<HookToken>
{
    public void Configure(EntityTypeBuilder<HookToken> builder)
    {
        builder.ToTable("HookTokens");
        builder.Property(t => t.Id).HasDefaultValueSql("uuidv7()");
        builder.HasOne<Account>().WithMany().HasForeignKey(t => t.AccountId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(t => t.Name).HasMaxLength(64).IsRequired();
        builder.Property(t => t.SecretHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(t => t.AccountId);
    }
}

internal sealed class WebhookConfiguration : IEntityTypeConfiguration<Webhook>
{
    public void Configure(EntityTypeBuilder<Webhook> builder)
    {
        builder.ToTable("Webhooks");
        builder.Property(w => w.Id).HasDefaultValueSql("uuidv7()");
        builder.HasOne<Account>().WithMany().HasForeignKey(w => w.AccountId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(w => w.Name).HasMaxLength(64).IsRequired();
        builder.Property(w => w.ProtectedUrl).IsRequired();
        builder.Property(w => w.ProtectedSecret).IsRequired();
        builder.Property(w => w.LastResult).HasMaxLength(300);
        builder.HasIndex(w => w.AccountId);
    }
}

internal sealed class McpServerConfiguration : IEntityTypeConfiguration<McpServer>
{
    public void Configure(EntityTypeBuilder<McpServer> builder)
    {
        builder.ToTable("McpServers");
        builder.Property(m => m.Id).HasDefaultValueSql("uuidv7()");
        builder.HasOne<Account>().WithMany().HasForeignKey(m => m.AccountId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(m => m.Name).HasMaxLength(24).IsRequired();
        builder.HasIndex(m => new { m.AccountId, m.Name }).IsUnique();
        builder.Property(m => m.ProtectedUrl).IsRequired();
        builder.Property(m => m.ProtectedOAuth).HasColumnName("protected_oauth");
        builder.Property(m => m.ToolsJson).HasColumnType("jsonb").IsRequired();
        builder.Property(m => m.LastError).HasMaxLength(500);
    }
}

internal sealed class HomeAssistantLinkConfiguration : IEntityTypeConfiguration<HomeAssistantLink>
{
    public void Configure(EntityTypeBuilder<HomeAssistantLink> builder)
    {
        builder.ToTable("HomeAssistantLinks");
        builder.Property(h => h.Id).HasDefaultValueSql("uuidv7()");
        builder.HasOne<Account>().WithMany().HasForeignKey(h => h.AccountId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(h => h.AccountId).IsUnique();
        builder.Property(h => h.ProtectedUrl).IsRequired();
        builder.Property(h => h.ProtectedToken).IsRequired();
        builder.Property(h => h.DevicesJson).HasColumnType("jsonb").IsRequired();
        builder.Property(h => h.LastError).HasMaxLength(500);
    }
}
