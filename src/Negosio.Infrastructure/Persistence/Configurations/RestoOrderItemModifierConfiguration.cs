using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class RestoOrderItemModifierConfiguration : IEntityTypeConfiguration<RestoOrderItemModifier>
{
    public void Configure(EntityTypeBuilder<RestoOrderItemModifier> builder)
    {
        builder.ToTable("RestoOrderItemModifiers");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.TenantId).IsRequired();
        builder.Property(m => m.RestoOrderItemId).IsRequired();
        builder.Property(m => m.ModifierGroupNameSnapshot).IsRequired().HasMaxLength(100);
        builder.Property(m => m.ModifierOptionNameSnapshot).IsRequired().HasMaxLength(100);
        builder.Property(m => m.PriceDeltaSnapshot).HasPrecision(18, 2);
        builder.Property(m => m.CreatedAtUtc).IsRequired();
        builder.Property(m => m.UpdatedAtUtc).IsRequired();

        builder.HasIndex(m => new { m.TenantId, m.RestoOrderItemId })
            .HasDatabaseName("IX_RestoOrderItemModifiers_TenantId_RestoOrderItemId");
    }
}
