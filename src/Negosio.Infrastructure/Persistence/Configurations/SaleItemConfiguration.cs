using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class SaleItemConfiguration : IEntityTypeConfiguration<SaleItem>
{
    public void Configure(EntityTypeBuilder<SaleItem> builder)
    {
        builder.ToTable("SaleItems");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();

        builder.Property(i => i.TenantId).IsRequired();
        builder.Property(i => i.SaleId).IsRequired();
        builder.Property(i => i.ProductVariantId).IsRequired();
        builder.Property(i => i.ProductNameSnapshot).IsRequired().HasMaxLength(150);
        builder.Property(i => i.VariantNameSnapshot).HasMaxLength(150);
        builder.Property(i => i.SkuSnapshot).HasMaxLength(64).IsUnicode(false);
        builder.Property(i => i.BarcodeSnapshot).HasMaxLength(64).IsUnicode(false);
        builder.Property(i => i.UnitPrice).HasPrecision(18, 2);
        builder.Property(i => i.Quantity).HasPrecision(18, 3);
        builder.Property(i => i.DiscountKind).IsRequired().HasConversion<int>();
        builder.Property(i => i.DiscountValue).HasPrecision(18, 2);
        builder.Property(i => i.GrossAmount).HasPrecision(18, 2);
        builder.Property(i => i.DiscountAmount).HasPrecision(18, 2);
        builder.Property(i => i.TaxAmount).HasPrecision(18, 2);
        builder.Property(i => i.NetAmount).HasPrecision(18, 2);
        builder.Property(i => i.CostPriceSnapshot).HasPrecision(18, 2);
        builder.Property(i => i.ReturnedQuantity).HasPrecision(18, 3);
        builder.Property(i => i.CreatedAtUtc).IsRequired();
        builder.Property(i => i.UpdatedAtUtc).IsRequired();

        builder.HasOne<ProductVariant>()
            .WithMany()
            .HasForeignKey(i => i.ProductVariantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(i => new { i.TenantId, i.SaleId }).HasDatabaseName("IX_SaleItems_TenantId_SaleId");
        builder.HasIndex(i => new { i.TenantId, i.ProductVariantId }).HasDatabaseName("IX_SaleItems_TenantId_ProductVariantId");
    }
}
