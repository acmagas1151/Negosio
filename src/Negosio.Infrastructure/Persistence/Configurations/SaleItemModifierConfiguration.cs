using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class SaleItemModifierConfiguration : IEntityTypeConfiguration<SaleItemModifier>
{
    public void Configure(EntityTypeBuilder<SaleItemModifier> builder)
    {
        builder.ToTable("SaleItemModifiers");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.TenantId).IsRequired();
        builder.Property(m => m.SaleItemId).IsRequired();
        builder.Property(m => m.ModifierGroupNameSnapshot).IsRequired().HasMaxLength(100);
        builder.Property(m => m.ModifierOptionNameSnapshot).IsRequired().HasMaxLength(100);
        builder.Property(m => m.PriceDeltaSnapshot).HasPrecision(18, 2);
        builder.Property(m => m.SortOrder).IsRequired();
        builder.Property(m => m.CreatedAtUtc).IsRequired();
        builder.Property(m => m.UpdatedAtUtc).IsRequired();

        builder.HasIndex(m => new { m.TenantId, m.SaleItemId })
            .HasDatabaseName("IX_SaleItemModifiers_TenantId_SaleItemId");
    }
}
