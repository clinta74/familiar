using Familiar.Domain.Accounts;
using Familiar.Domain.Calendars;
using Familiar.Domain.Memories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pgvector;

namespace Familiar.Persistence.Configurations;

internal sealed class MemoryFactConfiguration : IEntityTypeConfiguration<MemoryFact>
{
    public void Configure(EntityTypeBuilder<MemoryFact> builder)
    {
        builder.ToTable("Memories");
        builder.Property(m => m.Id).HasDefaultValueSql("uuidv7()");
        builder.HasOne<Account>().WithMany().HasForeignKey(m => m.AccountId).OnDelete(DeleteBehavior.Cascade);

        builder.Property(m => m.Text).HasMaxLength(MemoryFact.MaxTextLength).IsRequired();
        builder.Property(m => m.Source).HasConversion<string>().HasMaxLength(16);

        // float[] in the domain (it shouldn't know about pgvector), vector(768) in the database.
        builder.Property(m => m.Embedding)
            .HasColumnType($"vector({MemoryFact.EmbeddingDimensions})")
            .HasConversion(
                v => v == null ? null : new Vector(v),
                v => v == null ? null : v.ToArray(),
                new ValueComparer<float[]?>(
                    (a, b) => a == null ? b == null : b != null && a.SequenceEqual(b),
                    v => v == null ? 0 : v.Length,
                    v => v == null ? null : v.ToArray()));

        builder.HasIndex(m => new { m.AccountId, m.CreatedAt });
    }
}

internal sealed class CalendarSourceConfiguration : IEntityTypeConfiguration<CalendarSource>
{
    public void Configure(EntityTypeBuilder<CalendarSource> builder)
    {
        builder.ToTable("CalendarSources");
        builder.Property(c => c.Id).HasDefaultValueSql("uuidv7()");
        builder.HasOne<Account>().WithMany().HasForeignKey(c => c.AccountId).OnDelete(DeleteBehavior.Cascade);

        builder.Property(c => c.Kind).HasConversion<string>().HasMaxLength(16);
        builder.Property(c => c.Name).HasMaxLength(CalendarSource.MaxNameLength).IsRequired();
        builder.Property(c => c.LastError).HasMaxLength(500);
        builder.HasIndex(c => c.AccountId);
    }
}

internal sealed class CalendarEventConfiguration : IEntityTypeConfiguration<CalendarEvent>
{
    public void Configure(EntityTypeBuilder<CalendarEvent> builder)
    {
        builder.ToTable("CalendarEvents");
        builder.Property(e => e.Id).HasDefaultValueSql("uuidv7()");
        builder.HasOne<CalendarSource>().WithMany().HasForeignKey(e => e.SourceId).OnDelete(DeleteBehavior.Cascade);

        builder.Property(e => e.Title).HasMaxLength(CalendarEvent.MaxTitleLength).IsRequired();
        builder.Property(e => e.Location).HasMaxLength(CalendarEvent.MaxTitleLength);
        builder.Property(e => e.ExternalId).HasMaxLength(512);

        // Every question asked of events is "this account's, between then and then".
        builder.HasIndex(e => new { e.AccountId, e.StartsAt });
        builder.HasIndex(e => e.SourceId);
    }
}
