using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class RestoOrderItemConfiguration : IEntityTypeConfiguration<RestoOrderItem>
{
    public void Configure(EntityTypeBuilder<RestoOrderItem> builder)
    {
        builder.ToTable("RestoOrderItems");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();

        builder.Property(i => i.TenantId).IsRequired();
        builder.Property(i => i.RestoOrderRoundId).IsRequired();
        builder.Property(i => i.ProductVariantId).IsRequired();
        builder.Property(i => i.ProductNameSnapshot).IsRequired().HasMaxLength(150);
        builder.Property(i => i.VariantNameSnapshot).HasMaxLength(150);
        builder.Property(i => i.StationId).IsRequired();
        builder.Property(i => i.StationNameSnapshot).IsRequired().HasMaxLength(100);
        builder.Property(i => i.UnitPriceSnapshot).HasPrecision(18, 2);
        builder.Property(i => i.TaxRateSnapshot).HasPrecision(9, 4);
        builder.Property(i => i.GrossAmount).HasPrecision(18, 2);
        builder.Property(i => i.DiscountAmount).HasPrecision(18, 2);
        builder.Property(i => i.TaxAmount).HasPrecision(18, 2);
        builder.Property(i => i.NetAmount).HasPrecision(18, 2);
        builder.Property(i => i.Quantity).HasPrecision(18, 3);
        builder.Property(i => i.DiscountKind).IsRequired().HasConversion<int>();
        builder.Property(i => i.DiscountValue).HasPrecision(18, 2);
        builder.Property(i => i.DiscountApprovedByUserId);
        builder.Property(i => i.CostPriceSnapshot).HasPrecision(18, 2);
        builder.Property(i => i.KitchenNote).HasMaxLength(500);
        builder.Property(i => i.KitchenStatus).HasConversion<int?>();
        builder.Property(i => i.VoidReason).HasMaxLength(500);
        builder.Property(i => i.CreatedAtUtc).IsRequired();
        builder.Property(i => i.UpdatedAtUtc).IsRequired();

        builder.HasOne<ProductVariant>()
            .WithMany()
            .HasForeignKey(i => i.ProductVariantId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<RestoStation>()
            .WithMany()
            .HasForeignKey(i => i.StationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(i => i.Modifiers)
            .WithOne()
            .HasForeignKey(m => m.RestoOrderItemId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(i => i.Modifiers).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(i => new { i.TenantId, i.RestoOrderRoundId })
            .HasDatabaseName("IX_RestoOrderItems_TenantId_RestoOrderRoundId");
        builder.HasIndex(i => new { i.TenantId, i.StationId, i.KitchenStatus })
            .HasDatabaseName("IX_RestoOrderItems_TenantId_StationId_KitchenStatus");
    }
}
