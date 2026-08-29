using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class ProductVariantConfiguration : IEntityTypeConfiguration<ProductVariant>
{
    public void Configure(EntityTypeBuilder<ProductVariant> builder)
    {
        builder.ToTable("ProductVariants");

        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();

        builder.Property(v => v.TenantId).IsRequired();
        builder.Property(v => v.ProductId).IsRequired();
        builder.Property(v => v.Name).IsRequired().HasMaxLength(150);
        builder.Property(v => v.IsDefault).IsRequired();
        builder.Property(v => v.Sku).HasMaxLength(64).IsUnicode(false);
        builder.Property(v => v.Barcode).HasMaxLength(64).IsUnicode(false);
        builder.Property(v => v.CostPrice).HasPrecision(18, 2);
        builder.Property(v => v.SellingPrice).HasPrecision(18, 2);
        builder.Property(v => v.IsActive).IsRequired();
        builder.Property(v => v.CreatedAtUtc).IsRequired();
        builder.Property(v => v.UpdatedAtUtc).IsRequired();

        builder.HasIndex(v => new { v.TenantId, v.ProductId })
            .HasDatabaseName("IX_ProductVariants_TenantId_ProductId");

        // SKU / barcode are unique per tenant when present. Filtered so many NULLs are allowed.
        builder.HasIndex(v => new { v.TenantId, v.Sku })
            .IsUnique()
            .HasFilter("[Sku] IS NOT NULL")
            .HasDatabaseName("IX_ProductVariants_TenantId_Sku");

        builder.HasIndex(v => new { v.TenantId, v.Barcode })
            .IsUnique()
            .HasFilter("[Barcode] IS NOT NULL")
            .HasDatabaseName("IX_ProductVariants_TenantId_Barcode");
    }
}
