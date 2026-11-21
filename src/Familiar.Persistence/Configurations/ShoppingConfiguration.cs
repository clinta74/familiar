using Familiar.Domain.Accounts;
using Familiar.Domain.Shopping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Familiar.Persistence.Configurations;

internal sealed class ShoppingItemConfiguration : IEntityTypeConfiguration<ShoppingItem>
{
    public void Configure(EntityTypeBuilder<ShoppingItem> builder)
    {
        builder.ToTable("ShoppingItems");
        builder.Property(i => i.Id).HasDefaultValueSql("uuidv7()");
        builder.Property(i => i.Text).HasMaxLength(ShoppingItem.MaxText).IsRequired();
        builder.HasOne<Account>().WithMany().HasForeignKey(i => i.AccountId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(i => new { i.AccountId, i.AddedAt });
    }
}
