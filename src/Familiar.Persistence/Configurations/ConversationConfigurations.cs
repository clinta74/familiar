using Familiar.Domain.Accounts;
using Familiar.Domain.Conversations;
using Familiar.Domain.Familiars;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Familiar.Persistence.Configurations;

internal sealed class FamiliarProfileConfiguration : IEntityTypeConfiguration<FamiliarProfile>
{
    public void Configure(EntityTypeBuilder<FamiliarProfile> builder)
    {
        builder.ToTable("FamiliarProfiles");
        builder.Property(p => p.Id).HasDefaultValueSql("uuidv7()");

        // One familiar per account; the unique index is what settles two requests racing to create it.
        builder.HasIndex(p => p.AccountId).IsUnique();
        builder.HasOne<Account>().WithMany().HasForeignKey(p => p.AccountId).OnDelete(DeleteBehavior.Cascade);

        builder.Property(p => p.Name).HasMaxLength(FamiliarProfile.MaxNameLength).IsRequired();
        builder.Property(p => p.UserNickname).HasMaxLength(FamiliarProfile.MaxNameLength);
        builder.Property(p => p.HomePlace).HasMaxLength(FamiliarProfile.MaxHomePlaceLength);
        builder.Property(p => p.Voice).HasMaxLength(FamiliarProfile.MaxVoiceLength);
        builder.Property(p => p.VoiceSpeed).HasDefaultValue(1.0);
        builder.Property(p => p.Appearance).HasColumnType("jsonb");
    }
}

internal sealed class AvatarAssetConfiguration : IEntityTypeConfiguration<AvatarAsset>
{
    public void Configure(EntityTypeBuilder<AvatarAsset> builder)
    {
        builder.ToTable("AvatarAssets");
        builder.Property(a => a.Id).HasDefaultValueSql("uuidv7()");
        builder.HasOne<Account>().WithMany().HasForeignKey(a => a.AccountId).OnDelete(DeleteBehavior.Cascade);

        builder.Property(a => a.Name).HasMaxLength(128).IsRequired();
        builder.Property(a => a.Sha256).HasMaxLength(64).IsRequired();
        builder.Property(a => a.Meta).HasColumnType("jsonb").IsRequired();
        builder.HasIndex(a => a.AccountId);
    }
}

internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("Conversations");
        builder.Property(c => c.Id).HasDefaultValueSql("uuidv7()");

        builder.HasOne<Account>().WithMany().HasForeignKey(c => c.AccountId).OnDelete(DeleteBehavior.Cascade);

        // "The current conversation" is the most recently active one.
        builder.HasIndex(c => new { c.AccountId, c.LastMessageAt }).IsDescending(false, true);

        builder.HasMany(c => c.Messages)
            .WithOne()
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("Messages");
        builder.Property(m => m.Id).HasDefaultValueSql("uuidv7()");

        builder.Property(m => m.Role).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(m => m.Emotion).HasConversion<string>().HasMaxLength(16);
        builder.Property(m => m.Content).IsRequired();

        // Ids are uuidv7, so they sort by creation; history is read newest-first by id.
        builder.HasIndex(m => new { m.ConversationId, m.Id }).IsDescending(false, true);
    }
}
